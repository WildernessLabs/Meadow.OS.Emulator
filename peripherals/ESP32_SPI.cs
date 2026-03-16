// ESP32 SPI Peripheral — implements the SPI3 (VSPI) register interface
// at 0x3FF65000 for the ESP-IDF spi_slave driver.
//
// When the firmware arms a transaction (cmd.usr = 1), this peripheral
// checks SpiBridge for a pending frame from the STM32 side, injects it
// via the DMA descriptor chain into ESP32 DRAM, and fires the SPI
// interrupt so the firmware's ISR processes the frame.

using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.SPI
{
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public class ESP32_SPI : IDoubleWordPeripheral, IKnownSize
    {
        public ESP32_SPI(IMachine machine)
        {
            this.machine = machine;
            sysbus = (Antmicro.Renode.Peripherals.Bus.SystemBus)machine.SystemBus;
            IRQ = new GPIO();
            registers = new DoubleWordRegisterCollection(this);
            DefineRegisters();
        }

        public uint ReadDoubleWord(long offset)
        {
            // Data buffer region: 0x80..0xBC (W0-W15)
            if(offset >= DataBufBase && offset <= DataBufEnd)
            {
                int index = (int)(offset - DataBufBase) / 4;
                this.Log(LogLevel.Debug, "SPI: Read data_buf[{0}] = 0x{1:X8}", index, dataBuf[index]);
                return dataBuf[index];
            }

            if(registers.TryRead(offset, out var value))
            {
                return value;
            }

            // Unhandled offset — return from backing store
            backingStore.TryGetValue(offset, out var stored);
            this.Log(LogLevel.Debug, "SPI: Unhandled read at 0x{0:X3} = 0x{1:X8}", offset, stored);
            return stored;
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            // Data buffer region: 0x80..0xBC (W0-W15)
            if(offset >= DataBufBase && offset <= DataBufEnd)
            {
                int index = (int)(offset - DataBufBase) / 4;
                dataBuf[index] = value;
                this.Log(LogLevel.Debug, "SPI: Write data_buf[{0}] = 0x{1:X8}", index, value);
                return;
            }

            if(offset == 0x38)
            {
                this.Log(LogLevel.Warning, "SPI: SLAVE REG WRITE 0x38 = 0x{0:X8}", value);
            }

            if(registers.TryWrite(offset, value))
            {
                return;
            }

            // Unhandled offset — store in backing dict
            backingStore[offset] = value;
            this.Log(LogLevel.Debug, "SPI: Unhandled write at 0x{0:X3} = 0x{1:X8}", offset, value);
        }

        public void Reset()
        {
            registers.Reset();
            backingStore.Clear();
            Array.Clear(dataBuf, 0, dataBuf.Length);
            dmaOutLinkAddr = 0;
            dmaInLinkAddr = 0;
            transInten = false;
            transDone = false;
            slaveMode = false;
            peripheralArmed = false;
            IRQ.Unset();
        }

        public long Size => 0x1000;

        public GPIO IRQ { get; }

        // ── Cross-machine bridge connection ────────────────────────────────

        /// <summary>
        /// Set the SpiBridge reference. Called from .resc script or monitor.
        /// Accepts object type to avoid compile-time dependency on SpiBridge.cs load order.
        /// Also registers a callback so we're notified when frames arrive.
        /// </summary>
        public void SetBridge(object bridgeObj)
        {
            this.bridge = bridgeObj;
            // Register callback for frame arrival notification
            bridge.SetFrameAvailableCallback((Action)OnFrameAvailable);
            this.Log(LogLevel.Info, "SPI: SpiBridge connected");
        }

        /// <summary>
        /// Called by SpiBridge when a new frame arrives from the STM32.
        /// If the peripheral is armed (cmd.usr was set), process the frame immediately.
        /// </summary>
        private void OnFrameAvailable()
        {
            if(peripheralArmed && bridge != null)
            {
                this.Log(LogLevel.Warning, "SPI: Frame arrived while peripheral armed — injecting");
                InjectFrame();
            }
        }

        // ── Register definitions ───────────────────────────────────────────

        private void DefineRegisters()
        {
            // CMD register (0x00): bit 18 = usr (arm transaction)
            Registers.Cmd.Define(registers, 0)
                .WithReservedBits(0, 18)
                .WithFlag(18, FieldMode.Read | FieldMode.Write,
                    writeCallback: (_, value) =>
                    {
                        if(value)
                        {
                            this.Log(LogLevel.Debug, "SPI: cmd.usr set — peripheral armed");
                            OnTransactionArmed();
                        }
                    },
                    name: "USR")
                .WithReservedBits(19, 13);

            // USER register (0x1C): bit 0 = doutdin, bit 27 = usr_mosi, bit 28 = usr_miso
            Registers.User.Define(registers, 0)
                .WithFlag(0, name: "DOUTDIN")
                .WithReservedBits(1, 26)
                .WithFlag(27, name: "USR_MOSI")
                .WithFlag(28, name: "USR_MISO")
                .WithReservedBits(29, 3);

            // MOSI_DLEN register (0x28)
            Registers.MosiDlen.Define(registers, 0)
                .WithValueField(0, 24, name: "USR_MOSI_DBITLEN")
                .WithReservedBits(24, 8);

            // MISO_DLEN register (0x2C)
            Registers.MisoDlen.Define(registers, 0)
                .WithValueField(0, 24, name: "USR_MISO_DBITLEN")
                .WithReservedBits(24, 8);

            // SLAVE register (0x38) — bit layout from spi_struct.h:
            // [0] rd_buf_done, [1] wr_buf_done, [2] rd_sta_done, [3] wr_sta_done,
            // [4] trans_done, [5] rd_buf_inten, [6] wr_buf_inten, [7] rd_sta_inten,
            // [8] wr_sta_inten, [9] trans_inten, [10:11] cs_i_mode, [12:16] reserved,
            // [17:19] last_command, [20:22] last_state, [23:26] trans_cnt,
            // [27] cmd_define, [28] wr_rd_sta_en, [29] wr_rd_buf_en,
            // [30] slave_mode, [31] sync_reset
            Registers.Slave.Define(registers, 0)
                .WithTag("RD_BUF_DONE", 0, 1)
                .WithTag("WR_BUF_DONE", 1, 1)
                .WithTag("RD_STA_DONE", 2, 1)
                .WithTag("WR_STA_DONE", 3, 1)
                .WithFlag(4, FieldMode.Read | FieldMode.Write,
                    writeCallback: (oldVal, newVal) =>
                    {
                        transDone = newVal;
                        if(!newVal && oldVal)
                        {
                            this.Log(LogLevel.Debug, "SPI: trans_done cleared by driver");
                        }
                        UpdateIrq();
                    },
                    valueProviderCallback: _ => transDone,
                    name: "TRANS_DONE")
                .WithTag("RD_BUF_INTEN", 5, 1)
                .WithTag("WR_BUF_INTEN", 6, 1)
                .WithTag("RD_STA_INTEN", 7, 1)
                .WithTag("WR_STA_INTEN", 8, 1)
                .WithFlag(9, FieldMode.Read | FieldMode.Write,
                    writeCallback: (_, value) => { transInten = value; UpdateIrq(); },
                    valueProviderCallback: _ => transInten,
                    name: "TRANS_INTEN")
                .WithTag("CS_I_MODE", 10, 2)
                .WithReservedBits(12, 5)
                .WithTag("LAST_COMMAND", 17, 3)
                .WithTag("LAST_STATE", 20, 3)
                .WithTag("TRANS_CNT", 23, 4)
                .WithTag("CMD_DEFINE", 27, 1)
                .WithTag("WR_RD_STA_EN", 28, 1)
                .WithFlag(29, name: "WR_RD_BUF_EN")
                .WithFlag(30, FieldMode.Read | FieldMode.Write,
                    writeCallback: (_, value) => { slaveMode = value; },
                    valueProviderCallback: _ => slaveMode,
                    name: "SLAVE_MODE")
                .WithFlag(31, FieldMode.Write,
                    writeCallback: (_, value) =>
                    {
                        if(value)
                            this.Log(LogLevel.Debug, "SPI: Sync reset");
                    },
                    name: "SYNC_RESET");

            // SLV_WRBUF_DLEN register (0x44): TX data bit length
            Registers.SlvWrbufDlen.Define(registers, 0)
                .WithValueField(0, 24, name: "SLV_WRBUF_DBITLEN")
                .WithReservedBits(24, 8);

            // SLV_RDBUF_DLEN register (0x48): RX data bit length
            Registers.SlvRdbufDlen.Define(registers, 0)
                .WithValueField(0, 24, name: "SLV_RDBUF_DBITLEN")
                .WithReservedBits(24, 8);

            // SLV_RD_BIT register (0x64): received data bit count
            Registers.SlvRdBit.Define(registers, 0)
                .WithValueField(0, 24,
                    valueProviderCallback: _ => slvRdBit,
                    name: "SLV_RDATA_BIT")
                .WithReservedBits(24, 8);

            // DMA_CONF register (0x100)
            Registers.DmaConf.Define(registers, 0)
                .WithReservedBits(0, 2)
                .WithFlag(2, FieldMode.Write,
                    writeCallback: (_, value) =>
                    {
                        if(value)
                            this.Log(LogLevel.Debug, "SPI: DMA in_rst");
                    },
                    name: "IN_RST")
                .WithFlag(3, FieldMode.Write,
                    writeCallback: (_, value) =>
                    {
                        if(value)
                            this.Log(LogLevel.Debug, "SPI: DMA out_rst");
                    },
                    name: "OUT_RST")
                .WithReservedBits(4, 28);

            // DMA_OUT_LINK register (0x104): TX DMA descriptor address
            Registers.DmaOutLink.Define(registers, 0)
                .WithValueField(0, 20,
                    writeCallback: (_, value) =>
                    {
                        dmaOutLinkAddr = (uint)value | DramBaseAddr;
                        this.Log(LogLevel.Debug, "SPI: DMA out_link addr = 0x{0:X8}", dmaOutLinkAddr);
                    },
                    valueProviderCallback: _ => dmaOutLinkAddr & 0xFFFFF,
                    name: "OUTLINK_ADDR")
                .WithReservedBits(20, 8)
                .WithFlag(28, FieldMode.Write,
                    writeCallback: (_, value) =>
                    {
                        if(value)
                            this.Log(LogLevel.Debug, "SPI: DMA out_link start");
                    },
                    name: "OUTLINK_START")
                .WithReservedBits(29, 3);

            // DMA_IN_LINK register (0x108): RX DMA descriptor address
            Registers.DmaInLink.Define(registers, 0)
                .WithValueField(0, 20,
                    writeCallback: (_, value) =>
                    {
                        dmaInLinkAddr = (uint)value | DramBaseAddr;
                        this.Log(LogLevel.Debug, "SPI: DMA in_link addr = 0x{0:X8}", dmaInLinkAddr);
                    },
                    valueProviderCallback: _ => dmaInLinkAddr & 0xFFFFF,
                    name: "INLINK_ADDR")
                .WithReservedBits(20, 8)
                .WithFlag(28, FieldMode.Write,
                    writeCallback: (_, value) =>
                    {
                        if(value)
                            this.Log(LogLevel.Debug, "SPI: DMA in_link start");
                    },
                    name: "INLINK_START")
                .WithReservedBits(29, 3);
        }

        // ── Transaction handling ───────────────────────────────────────────

        private void OnTransactionArmed()
        {
            peripheralArmed = true;

            if(bridge == null)
            {
                this.Log(LogLevel.Warning, "SPI: Transaction armed but no SpiBridge connected");
                return;
            }

            // Check if SpiBridge has a pending frame from STM32
            byte[] frame = (byte[])bridge.DequeueFrameFromStm32();
            if(frame == null)
            {
                this.Log(LogLevel.Debug, "SPI: Transaction armed, waiting for frame from STM32");
                return;
            }

            InjectFrame();
        }

        private void InjectFrame()
        {
            byte[] frame = (byte[])bridge.DequeueFrameFromStm32();
            if(frame == null)
                return;

            peripheralArmed = false;

            this.Log(LogLevel.Warning, "SPI: Injecting frame from STM32 ({0} bytes)", frame.Length);

            // Write frame data into ESP32 DRAM via RX DMA descriptor chain
            WriteToDmaDescriptorChain(dmaInLinkAddr, frame, isRx: true);

            // Set the received bit count
            slvRdBit = (uint)(frame.Length * 8);

            // Read the TX DMA descriptor chain to get firmware's response data
            var txData = ReadFromDmaDescriptorChain(dmaOutLinkAddr);
            if(txData != null && txData.Length > 0)
            {
                this.Log(LogLevel.Warning, "SPI: Sending firmware response to STM32 ({0} bytes)", txData.Length);
                bridge.EnqueueFrameToStm32(txData);
            }

            // Signal transaction complete
            transDone = true;
            UpdateIrq();

            this.Log(LogLevel.Debug, "SPI: Transaction complete, trans_done set");
        }

        private void UpdateIrq()
        {
            bool shouldAssert = transDone && transInten;
            var cpuEnum = machine.SystemBus.GetCPUs().GetEnumerator();
            cpuEnum.MoveNext();
            var gpioReceiver = cpuEnum.Current as IGPIOReceiver;
            if(gpioReceiver != null)
            {
                gpioReceiver.OnGPIO(SpiInterruptNumber, shouldAssert);
                if(shouldAssert)
                {
                    this.Log(LogLevel.Debug, "SPI: Interrupt 6 asserted (level-triggered)");
                }
            }
        }

        private const int SpiInterruptNumber = 6;

        // ── DMA descriptor chain operations ────────────────────────────────

        /// <summary>
        /// Write data into DRAM following a DMA descriptor chain (for RX path).
        /// Updates descriptor length and clears owner bit.
        /// </summary>
        private void WriteToDmaDescriptorChain(uint descAddr, byte[] data, bool isRx)
        {
            int dataOffset = 0;
            uint currentDesc = descAddr;

            while(currentDesc != 0 && dataOffset < data.Length)
            {
                // Read the 3-word DMA descriptor
                uint word0 = sysbus.ReadDoubleWord(currentDesc);
                uint bufAddr = sysbus.ReadDoubleWord(currentDesc + 4);
                uint nextDesc = sysbus.ReadDoubleWord(currentDesc + 8);

                // Parse word0: size(12) | length(12) | offset(5) | sosf(1) | eof(1) | owner(1)
                uint size = word0 & 0xFFF;
                // length is bits 12..23
                // owner is bit 31

                if(size == 0)
                {
                    this.Log(LogLevel.Warning, "SPI DMA: Descriptor at 0x{0:X8} has size=0, stopping", currentDesc);
                    break;
                }

                // Calculate how much data to write to this buffer
                int bytesToWrite = Math.Min((int)size, data.Length - dataOffset);

                this.Log(LogLevel.Debug,
                    "SPI DMA RX: desc=0x{0:X8} buf=0x{1:X8} size={2} writing={3}",
                    currentDesc, bufAddr, size, bytesToWrite);

                // Write data to the buffer in DRAM
                sysbus.WriteBytes((long)bufAddr, data, dataOffset, bytesToWrite);

                // Update the descriptor: set length, set eof if last chunk, clear owner
                uint newLength = (uint)bytesToWrite;
                bool isEof = (dataOffset + bytesToWrite >= data.Length) || nextDesc == 0;
                uint newWord0 = size                          // size: bits 0..11
                    | (newLength << 12)                       // length: bits 12..23
                    | (0u << 24)                              // offset: bits 24..28 (zero)
                    | (0u << 29)                              // sosf: bit 29
                    | ((isEof ? 1u : 0u) << 30)               // eof: bit 30
                    | (0u << 31);                              // owner: bit 31 (cleared = CPU owns)

                sysbus.WriteDoubleWord(currentDesc, newWord0);

                dataOffset += bytesToWrite;

                // Follow the chain
                currentDesc = nextDesc;
            }

            if(dataOffset < data.Length)
            {
                this.Log(LogLevel.Warning,
                    "SPI DMA: Could not write all data — {0}/{1} bytes written (descriptor chain exhausted)",
                    dataOffset, data.Length);
            }
        }

        /// <summary>
        /// Read data from DRAM following a DMA descriptor chain (for TX path).
        /// Returns the concatenated buffer contents.
        /// </summary>
        private byte[] ReadFromDmaDescriptorChain(uint descAddr)
        {
            var result = new List<byte>();
            uint currentDesc = descAddr;
            int safetyLimit = 64; // prevent infinite loops on corrupt chains

            while(currentDesc != 0 && safetyLimit-- > 0)
            {
                uint word0 = sysbus.ReadDoubleWord(currentDesc);
                uint bufAddr = sysbus.ReadDoubleWord(currentDesc + 4);
                uint nextDesc = sysbus.ReadDoubleWord(currentDesc + 8);

                uint size = word0 & 0xFFF;
                uint length = (word0 >> 12) & 0xFFF;
                bool eof = ((word0 >> 30) & 1) != 0;

                // Use length if set, otherwise use size
                uint bytesToRead = length > 0 ? length : size;

                if(bytesToRead == 0)
                    break;

                this.Log(LogLevel.Debug,
                    "SPI DMA TX: desc=0x{0:X8} buf=0x{1:X8} size={2} length={3} reading={4}",
                    currentDesc, bufAddr, size, length, bytesToRead);

                var chunk = sysbus.ReadBytes((long)bufAddr, (int)bytesToRead);
                result.AddRange(chunk);

                if(eof)
                    break;

                currentDesc = nextDesc;
            }

            return result.ToArray();
        }

        // ── Constants ──────────────────────────────────────────────────────

        private const long DataBufBase = 0x80;
        private const long DataBufEnd = 0xBC;
        private const uint DramBaseAddr = 0x3FF00000;

        // ── State ──────────────────────────────────────────────────────────

        private readonly IMachine machine;
        private readonly Antmicro.Renode.Peripherals.Bus.SystemBus sysbus;
        private readonly DoubleWordRegisterCollection registers;
        private readonly Dictionary<long, uint> backingStore = new Dictionary<long, uint>();
        private readonly uint[] dataBuf = new uint[16]; // W0-W15

        private dynamic bridge;
        private uint dmaOutLinkAddr;
        private uint dmaInLinkAddr;
        private bool transInten;
        private bool transDone;
        private bool slaveMode;
        private bool peripheralArmed;
        private uint slvRdBit;

        // ── Register map ───────────────────────────────────────────────────

        private enum Registers : long
        {
            Cmd          = 0x00,
            // addr = 0x04, ctrl = 0x08, ctrl1 = 0x0C not needed
            // rd_status = 0x10, ctrl2 = 0x14, clock = 0x18
            User         = 0x1C,
            // user1 = 0x20, user2 = 0x24
            MosiDlen     = 0x28,
            MisoDlen     = 0x2C,
            // 0x30..0x34 reserved
            Slave        = 0x38,
            // slave1 = 0x3C, slave2 = 0x40
            SlvWrbufDlen = 0x44,
            SlvRdbufDlen = 0x48,
            // 0x4C..0x60 various
            SlvRdBit     = 0x64,
            // 0x68..0xFC various
            DmaConf      = 0x100,
            DmaOutLink   = 0x104,
            DmaInLink    = 0x108,
        }
    }
}
