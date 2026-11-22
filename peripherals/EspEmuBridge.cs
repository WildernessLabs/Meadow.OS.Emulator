//
// SPDX-FileCopyrightText: 2026 Espressif Systems (Shanghai) CO LTD
// SPDX-FileCopyrightText: 2026 Wilderness Labs
//
// SPDX-License-Identifier: Apache-2.0
//
// EspEmuBridge — the Meadow ESP32 coprocessor, running in esp-emu, on the
// STM32's SPI2.
//
// Based on esp-emulator's tools/renode/EspEmuSpiBridge.cs (v1.2.0). The STM32
// is the SPI master and the ESP (an ESP32-S3 in esp-emu, built with
// Meadow-ESP32's `build.sh --emulator`) is its slave, served by IDF's stock
// spi_slave driver, over esp-emu's co-simulation socket (--cosim-tcp). The
// changes for Meadow:
//
//   - No handshake line. Meadow's ESP signals over its UART0 instead
//     (+++RST, +++RDY, +++SIR, +++MW); AttachUart joins esp-emu's UART0
//     (--uart-tcp) to an STM32 UART, so the OS's espcp_uart_monitor sees them
//     as on a board.
//   - TransferBuffer exchanges a whole buffer in one co-simulation transfer,
//     for EspcpSpiBypass, which replaces espcp_send_data_over_spi (the STM32
//     SPI model drives no DMA). Byte-at-a-time Transmit is kept for anything
//     else.
//   - The ESP's EN line is claimed and follows GPIO input 1 (the board's PF7,
//     open drain, low holds the ESP in reset). It is held low from the moment
//     this attaches, so the ESP boots when the OS lets it go, as on a board,
//     and not before the UART is listening.
//
// Usage: start esp-emu first,
//
//     esp-emu --chip esp32s3 --firmware merged_flash.bin \
//         --cosim-tcp 127.0.0.1:5590 --uart-tcp 127.0.0.1:5591
//
// then, in the Renode script, `include @peripherals/EspEmuBridge.cs` before
// the platform is loaded, and in the platform description
//
//     espEmu: SPI.EspEmuBridge @ spi2
//         port: 5590
//         csPin: 10           // the ESP GPIO its spi_slave driver takes CS on
//         quantumUs: 100      // lockstep; 0 lets each run at its own pace
//
//     gpioPortF:
//         7 -> espEmu@1       // the ESP's EN
//
// and after it, `sysbus.spi2.espEmu AttachUart sysbus.uart5 5591`.
//
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Peripherals.UART;

namespace Antmicro.Renode.Peripherals.SPI
{
    public class EspEmuBridge : ISPIPeripheral, IGPIOReceiver
    {
        // host, port:  where esp-emu listens (--cosim-tcp HOST:PORT)
        // csPin:       the ESP GPIO its spi_slave driver takes chip select on
        // sckHz:       the clock the master is taken to drive, for the ESP's timing
        // quantumUs:   0 lets the two emulators run each at its own pace. Above 0
        //              this machine holds the ESP's clock: the ESP runs one
        //              quantum of emulated time for each quantum of this
        //              machine's virtual time, and each waits for the other at
        //              the boundary.
        // holdInReset: hold the ESP's EN low from the moment this attaches, until
        //              GPIO input 1 rises.
        public EspEmuBridge(IMachine machine, string host = "127.0.0.1", int port = 5590, int csPin = 10,
            int sckHz = 5000000, ulong quantumUs = 0, bool holdInReset = true)
        {
            this.machine = machine;
            this.host = host;
            this.sckHz = sckHz;
            this.quantumNs = quantumUs * 1000;

            // The stream owns the socket from here on.
            var client = new TcpClient(host, port) { NoDelay = true };
            stream = client.GetStream();

            // Register: the SPI master on the chip-select pad, the ESP's EN, and
            // the clock if asked to.
            var claims = new MemoryStream();
            Ld(claims, Proto.RegisterClaims, Msg(m => Ld(m, Proto.ClaimSpiMaster, Msg(c => Varint(c, Proto.SpiMasterClaimCsPin, (ulong)csPin)))));
            Ld(claims, Proto.RegisterClaims, Msg(m => Ld(m, Proto.ClaimEn, new byte[0])));
            if(quantumNs > 0)
            {
                Ld(claims, Proto.RegisterClaims, Msg(m => Ld(m, Proto.ClaimTime, new byte[0])));
            }
            Send(Msg(m => Ld(m, Proto.Register, Msg(r =>
            {
                Varint(r, Proto.RegisterProtoVersion, Proto.Version);
                Ld(r, Proto.RegisterName, Encoding.ASCII.GetBytes("meadow-stm32"));
                r.Write(claims.ToArray(), 0, (int)claims.Length);
            }))));

            new Thread(ReadLoop) { IsBackground = true, Name = "esp-emu cosim reader" }.Start();

            if(holdInReset)
            {
                DriveEn(false);
            }

            if(quantumNs > 0)
            {
                // The ESP stops where this registered and says so. Only then
                // is the first quantum granted, so that every later notice
                // is the end of a quantum: the ESP runs each one while this
                // machine runs its own, and each boundary waits for it.
                if(!espWaiting.Wait(ReplyTimeoutMs))
                {
                    this.Log(LogLevel.Error, "esp-emu did not stop for this machine's clock within {0} ms", ReplyTimeoutMs);
                }
                EspTimeAtAttachNs = Interlocked.Read(ref espTimeNs);
                Grant();
                quantum = new LimitTimer(machine.ClockSource, 1000000, this, "quantum", limit: quantumUs,
                    enabled: true, eventEnabled: true);
                quantum.LimitReached += OnQuantum;
            }
        }

        // Bytes exchanged, transfers made, quanta granted and control lines
        // seen, for a script to read.
        public long BytesExchanged { get; private set; }

        public long Transfers { get; private set; }

        public long QuantaGranted { get; private set; }

        // Quanta granted while a transfer waited, beyond this machine's own.
        public long ExtraGrants { get; private set; }

        public long ControlLines => Interlocked.Read(ref controlLines);

        // The ESP's emulated time as last heard, and when this attached (with
        // a quantum: where it stopped for this machine's clock), in
        // nanoseconds.
        public long EspTimeNs => Interlocked.Read(ref espTimeNs);

        public long EspTimeAtAttachNs { get; private set; }

        // Wait for the ESP's +++SIR before each TransferBuffer (see there).
        public bool WaitForSpiReady { get; set; } = true;

        // TransferBuffer calls that went ahead without the ESP's +++SIR.
        public long SpiReadyTimeouts { get; private set; }

        // A file that gets a copy of everything the ESP sends on its UART0 (its log
        // and the control lines), as /tmp/renode-uart-*.txt does for the STM32
        // console. Set before AttachUart; empty for none.
        public string EspLogFile { get; set; }

        // Join esp-emu's UART0 (--uart-tcp HOST:PORT, on this bridge's host) to
        // an STM32 UART: what the ESP sends is written into the UART's receiver,
        // and what the STM32 transmits goes to the ESP. Control lines (+++...)
        // are logged.
        public void AttachUart(IUART uart, int uartPort)
        {
            if(uartStream != null)
            {
                this.Log(LogLevel.Warning, "AttachUart: a UART is already attached");
                return;
            }
            var client = new TcpClient(host, uartPort) { NoDelay = true };
            uartStream = client.GetStream();
            uart.CharReceived += b =>
            {
                try
                {
                    uartStream.WriteByte(b);
                }
                catch(IOException)
                {
                    // esp-emu left; the reader says so once.
                }
            };
            if(!string.IsNullOrEmpty(EspLogFile))
            {
                espLog = new FileStream(EspLogFile, FileMode.Create, FileAccess.Write, FileShare.Read);
            }
            new Thread(() => UartReadLoop(uart)) { IsBackground = true, Name = "esp-emu uart reader" }.Start();
            this.Log(LogLevel.Info, "ESP UART0 joined to {0}", uart.GetName());
        }

        // GPIO 0: the chip-select line, low selects (byte-at-a-time transfers).
        // GPIO 1: the ESP's EN, low holds it in reset.
        public void OnGPIO(int number, bool value)
        {
            switch(number)
            {
            case ChipSelectInput:
                if(value == !selected)
                {
                    return;
                }
                selected = !value;
                if(!selected && held)
                {
                    // Chip select rises: a closing piece with no bytes, and the
                    // ESP's transaction completes.
                    Exchange(new byte[0], false);
                    held = false;
                }
                break;

            case EnInput:
                this.Log(LogLevel.Info, "ESP EN {0}", value ? "released" : "held low (reset)");
                DriveEn(value);
                break;

            default:
                this.Log(LogLevel.Warning, "Unhandled GPIO {0} = {1}", number, value);
                break;
            }
        }

        public byte Transmit(byte data)
        {
            held = true;
            BytesExchanged++;
            var reply = Exchange(new[] { data }, true);
            // No slave selected: nothing drives MISO.
            return reply != null && reply.Length > 0 ? reply[0] : (byte)0xFF;
        }

        public void FinishTransmission()
        {
            // Controllers that signal the end of a transfer this way rather
            // than through a chip-select GPIO.
            OnGPIO(ChipSelectInput, true);
        }

        // One whole transfer: chip select falls, tx goes out while as many
        // bytes come back, chip select rises. With no slave on the chip select
        // (the ESP has not started its spi_slave driver, or is in reset) the
        // bytes read are 0xFF, as from an undriven MISO line.
        //
        // On a board the OS waits, before each exchange, for the ESP's +++SIR (SPI
        // interface ready: it has loaded its next transaction), through the
        // espcp_spi_interface_lock semaphore that espcp_uart_monitor gives. The shared
        // Legacy hooks make that lock a no-op (the EspcpStub sends no +++SIR), so the
        // wait is done here instead: until +++SIR has come since the last
        // transfer. Without it the STM32 clocks the ESP before its reply is loaded and
        // reads back its own previous packet. The first exchange waits too, as on a board,
        // where the semaphore starts taken.
        public byte[] TransferBuffer(byte[] tx)
        {
            if(held)
            {
                Exchange(new byte[0], false);
                held = false;
            }
            if(WaitForSpiReady)
            {
                WaitUntil(() => Volatile.Read(ref espSpiReady), "the ESP's +++SIR");
                Volatile.Write(ref espSpiReady, false);
            }
            Transfers++;
            BytesExchanged += tx.Length;
            var reply = Exchange(tx, false);
            var rx = new byte[tx.Length];
            if(reply == null)
            {
                for(var i = 0; i < rx.Length; i++)
                {
                    rx[i] = 0xFF;
                }
            }
            else
            {
                Array.Copy(reply, rx, Math.Min(reply.Length, rx.Length));
            }
            this.Log(LogLevel.Debug, "transfer {0}: {1} bytes{2}, tx {3}, rx {4}", Transfers, tx.Length,
                reply == null ? " (no slave selected)" : "", Hex(tx), Hex(rx));
            return rx;
        }

        public void Reset()
        {
            selected = false;
            held = false;
        }

        private void DriveEn(bool level)
        {
            // Not stamped (sim_time_ns 0, a time already past): esp-emu takes it at
            // once. See Exchange.
            Send(Msg(m => Ld(m, Proto.EnDrive, level ? Msg(e => Varint(e, Proto.EnDriveLevel, 1)) : new byte[0])));
        }

        // One SpiMasterTransfer, and the bytes its SpiMasterResult brings; null
        // when the ESP has no slave on the chip select.
        private byte[] Exchange(byte[] tx, bool holdCs)
        {
            var id = (ulong)Interlocked.Increment(ref nextId);
            // Not stamped (sim_time_ns 0, a time already past), so esp-emu starts the
            // transfer at once. EspEmuSpiBridge stamps it with the ESP time last heard,
            // but after the ESP restarts (EN) that can be ahead of its new timeline:
            // esp-emu then holds the transfer until its clock gets there, and with the
            // clock held, this machine, waiting here for the answer, never grants it.
            Send(Msg(m =>
            {
                Ld(m, Proto.SpiMasterTransfer, Msg(t =>
                {
                    Varint(t, Proto.TransferId, id);
                    Ld(t, Proto.TransferTx, tx);
                    Varint(t, Proto.TransferSckHz, (ulong)sckHz);
                    if(holdCs)
                    {
                        Varint(t, Proto.TransferHoldCs, 1);
                    }
                }));
            }));
            // A transfer starts when the one before it has ended, on the ESP's clock. With
            // the clock held, the ESP may be stopped at the end of its quantum before then,
            // while this machine, waiting here (in a CPU hook, so its virtual time stands
            // still), grants it nothing more. So while waiting, a quantum the ESP has used up
            // is granted again: the ESP runs at most a quantum or so ahead for the transfer.
            Result? answer = null;
            if(!WaitUntil(() =>
                {
                    while(results.TryTake(out var r))
                    {
                        if(r.Id == id)
                        {
                            answer = r;
                            return true;
                        }
                    }
                    return false;
                }, null))
            {
                this.Log(LogLevel.Error, "esp-emu did not answer SPI transfer {0} within {1} ms", id, ReplyTimeoutMs);
                return null;
            }
            return answer.Value.Selected ? answer.Value.Rx : null;
        }

        // Wait, on the host's clock, up to ReplyTimeoutMs for the condition. This runs
        // in a CPU hook, so this machine's virtual time stands still meanwhile: with the
        // ESP's clock held, a quantum it has used up is granted again, so that it can
        // get there (it runs ahead of this machine by that much). False on a timeout,
        // logged as a warning when the condition has a name.
        private bool WaitUntil(Func<bool> condition, string what)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(ReplyTimeoutMs);
            while(!condition())
            {
                if(quantumNs > 0 && espWaiting.IsSet)
                {
                    ExtraGrants++;
                    Grant();
                }
                if(DateTime.UtcNow > deadline)
                {
                    if(what != null)
                    {
                        SpiReadyTimeouts++;
                        this.Log(LogLevel.Warning, "no {0} within {1} ms; transferring anyway", what, ReplyTimeoutMs);
                    }
                    return false;
                }
                Thread.Sleep(WaitStepMs);
            }
            return true;
        }

        // The end of a quantum of this machine's virtual time: wait for the
        // ESP to have used up its own, then let it run the next.
        private void OnQuantum()
        {
            if(!espWaiting.Wait(ReplyTimeoutMs))
            {
                this.Log(LogLevel.Warning, "esp-emu has not reached the end of its quantum after {0} ms", ReplyTimeoutMs);
            }
            Grant();
        }

        private void Grant()
        {
            espWaiting.Reset();
            Send(Msg(m => Ld(m, Proto.TimeGrant, Msg(g => Varint(g, Proto.TimeGrantAdvanceNs, quantumNs)))));
            QuantaGranted++;
        }

        private void UartReadLoop(IUART uart)
        {
            var line = new StringBuilder();
            try
            {
                var buf = new byte[256];
                while(true)
                {
                    var n = uartStream.Read(buf, 0, buf.Length);
                    if(n <= 0)
                    {
                        throw new EndOfStreamException("esp-emu left");
                    }
                    if(espLog != null)
                    {
                        espLog.Write(buf, 0, n);
                        espLog.Flush();
                    }
                    for(var i = 0; i < n; i++)
                    {
                        var b = buf[i];
                        // From outside the machine's time domain, as a socket terminal's bytes are.
                        machine.HandleTimeDomainEvent(uart.WriteChar, b, false);
                        if(b == (byte)'\n')
                        {
                            var text = line.ToString().TrimEnd('\r');
                            if(text.StartsWith("+++"))
                            {
                                Interlocked.Increment(ref controlLines);
                                // Only +++SIR: +++RDY comes from the ESP's transport as it starts,
                                // before its spi_slave driver has loaded a transaction.
                                if(text.StartsWith("+++SIR"))
                                {
                                    Volatile.Write(ref espSpiReady, true);
                                }
                                else if(text.StartsWith("+++RST"))
                                {
                                    Volatile.Write(ref espSpiReady, false);
                                }
                                this.Log(LogLevel.Info, "ESP control: {0}", text);
                            }
                            line.Clear();
                        }
                        else if(line.Length < 256)
                        {
                            line.Append((char)b);
                        }
                    }
                }
            }
            catch(Exception e)
            {
                this.Log(LogLevel.Warning, "esp-emu UART connection closed: {0}", e.Message);
            }
        }

        private void ReadLoop()
        {
            try
            {
                var head = new byte[4];
                while(true)
                {
                    ReadExact(head, 4);
                    var frame = new byte[BitConverter.ToInt32(head, 0)];
                    ReadExact(frame, frame.Length);
                    var topicLen = BitConverter.ToUInt16(frame, 0);
                    var msg = Decode(frame, 2 + topicLen, frame.Length);
                    if(msg.TryGetValue(Proto.SimTimeNs, out var t))
                    {
                        Interlocked.Exchange(ref espTimeNs, (long)(ulong)t);
                    }
                    if(msg.TryGetValue(Proto.RegisterAck, out var ackRaw))
                    {
                        var ack = Decode((byte[])ackRaw);
                        if(!ack.ContainsKey(Proto.AckAccepted))
                        {
                            this.Log(LogLevel.Error, "esp-emu refused the registration: {0}",
                                ack.TryGetValue(Proto.AckRejectReason, out var why) ? Encoding.UTF8.GetString((byte[])why) : "?");
                        }
                        else
                        {
                            if(quantumNs == 0)
                            {
                                EspTimeAtAttachNs = Interlocked.Read(ref espTimeNs);
                            }
                            this.Log(LogLevel.Info, "attached to esp-emu");
                        }
                    }
                    if(msg.TryGetValue(Proto.SpiMasterResult, out var resRaw))
                    {
                        var r = Decode((byte[])resRaw);
                        results.Add(new Result
                        {
                            Id = r.TryGetValue(Proto.ResultId, out var i) ? (ulong)i : 0,
                            Selected = r.TryGetValue(Proto.ResultSelected, out var s) && (ulong)s != 0,
                            Rx = r.TryGetValue(Proto.ResultRx, out var rx) ? (byte[])rx : new byte[0],
                        });
                    }
                    if(msg.ContainsKey(Proto.TimeWaiting))
                    {
                        espWaiting.Set();
                    }
                }
            }
            catch(Exception e)
            {
                this.Log(LogLevel.Warning, "esp-emu connection closed: {0}", e.Message);
                // Nothing will answer any more: do not hold this machine's clock.
                espWaiting.Set();
            }
        }

        private void ReadExact(byte[] buf, int count)
        {
            var at = 0;
            while(at < count)
            {
                var n = stream.Read(buf, at, count - at);
                if(n <= 0)
                {
                    throw new EndOfStreamException("esp-emu left");
                }
                at += n;
            }
        }

        private void Send(byte[] msg)
        {
            try
            {
                // One write: the socket does not hold a short one back, and a
                // frame in two would be two segments for every byte clocked.
                var frame = new byte[4 + msg.Length];
                BitConverter.GetBytes(msg.Length).CopyTo(frame, 0);
                msg.CopyTo(frame, 4);
                lock(stream)
                {
                    stream.Write(frame, 0, frame.Length);
                }
            }
            catch(IOException)
            {
                // esp-emu left; the reader says so once.
            }
        }

        // ---- protobuf by hand: the few messages of proto/esp_cosim.proto used
        // here ----

        // That file's field numbers, by the names it gives them. They never
        // change once shipped; a message this file does not know is skipped.
        private static class Proto
        {
            public const ulong Version = 1;             // ProtoVersion.PROTO_VERSION_CURRENT

            // EmulatorMessage
            public const int SimTimeNs = 1;
            public const int Register = 4;
            public const int RegisterAck = 5;
            public const int EnDrive = 34;
            public const int SpiMasterTransfer = 38;
            public const int SpiMasterResult = 39;
            public const int TimeGrant = 40;
            public const int TimeWaiting = 41;

            // Register, and the Claim kinds it lists
            public const int RegisterProtoVersion = 1;
            public const int RegisterName = 2;
            public const int RegisterClaims = 3;
            public const int ClaimEn = 19;
            public const int ClaimSpiMaster = 22;
            public const int ClaimTime = 23;
            public const int SpiMasterClaimCsPin = 1;

            // RegisterAck
            public const int AckAccepted = 5;
            public const int AckRejectReason = 6;

            // EnDrive
            public const int EnDriveLevel = 1;

            // SpiMasterTransfer and SpiMasterResult
            public const int TransferId = 1;
            public const int TransferTx = 2;
            public const int TransferSckHz = 3;
            public const int TransferHoldCs = 4;
            public const int ResultId = 1;
            public const int ResultSelected = 2;
            public const int ResultRx = 3;

            // TimeGrant
            public const int TimeGrantAdvanceNs = 1;
        }

        // The first 32 bytes, for the log.
        private static string Hex(byte[] data)
        {
            var n = Math.Min(data.Length, 32);
            return BitConverter.ToString(data, 0, n).Replace("-", " ") + (data.Length > n ? " ..." : "");
        }

        private static byte[] Msg(Action<MemoryStream> fill)
        {
            var m = new MemoryStream();
            fill(m);
            return m.ToArray();
        }

        private static void RawVarint(MemoryStream m, ulong v)
        {
            while(v > 0x7F)
            {
                m.WriteByte((byte)((v & 0x7F) | 0x80));
                v >>= 7;
            }
            m.WriteByte((byte)v);
        }

        private static void Varint(MemoryStream m, int field, ulong v)
        {
            RawVarint(m, (ulong)field << 3);
            RawVarint(m, v);
        }

        private static void Ld(MemoryStream m, int field, byte[] inner)
        {
            RawVarint(m, ((ulong)field << 3) | 2);
            RawVarint(m, (ulong)inner.Length);
            m.Write(inner, 0, inner.Length);
        }

        private static Dictionary<int, object> Decode(byte[] buf)
        {
            return Decode(buf, 0, buf.Length);
        }

        private static Dictionary<int, object> Decode(byte[] buf, int at, int end)
        {
            var fields = new Dictionary<int, object>();
            while(at < end)
            {
                var tag = ReadVarint(buf, ref at);
                var field = (int)(tag >> 3);
                switch((int)(tag & 7))
                {
                case 0:
                    fields[field] = ReadVarint(buf, ref at);
                    break;
                case 1:
                    at += 8;
                    break;
                case 2:
                {
                    var n = (int)ReadVarint(buf, ref at);
                    var inner = new byte[n];
                    Array.Copy(buf, at, inner, 0, n);
                    fields[field] = inner;
                    at += n;
                    break;
                }
                case 5:
                    at += 4;
                    break;
                default:
                    return fields;
                }
            }
            return fields;
        }

        private static ulong ReadVarint(byte[] buf, ref int at)
        {
            ulong v = 0;
            var shift = 0;
            while(true)
            {
                var b = buf[at++];
                v |= (ulong)(b & 0x7F) << shift;
                shift += 7;
                if((b & 0x80) == 0)
                {
                    return v;
                }
            }
        }

        private struct Result
        {
            public ulong Id;
            public bool Selected;
            public byte[] Rx;
        }

        private const int ChipSelectInput = 0;
        private const int EnInput = 1;

        // How long this machine waits on esp-emu, on the host's clock, before
        // it says so and goes on.
        private const int ReplyTimeoutMs = 5000;

        // How often a transfer waiting for its answer checks whether the ESP is
        // stopped at the end of its quantum.
        private const int WaitStepMs = 2;

        private readonly IMachine machine;
        private readonly string host;
        private readonly int sckHz;
        private readonly ulong quantumNs;
        private readonly NetworkStream stream;
        // Kept so the timer lives as long as this does.
        private readonly LimitTimer quantum;
        private readonly BlockingCollection<Result> results = new BlockingCollection<Result>();
        private readonly ManualResetEventSlim espWaiting = new ManualResetEventSlim(false);
        private NetworkStream uartStream;
        private FileStream espLog;
        private bool espSpiReady;
        private bool selected;
        private bool held;
        private long nextId;
        private long espTimeNs;
        private long controlLines;
    }
}
