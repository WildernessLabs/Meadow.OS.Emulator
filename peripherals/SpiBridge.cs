// SPI Bridge — Cross-machine ESPCP frame forwarding for Renode
//
// Replaces EspcpStub on STM32 SPI2. Instead of handling ESPCP messages
// internally, forwards complete frames to/from the ESP32 machine where
// the real firmware processes them.
//
// STM32 side: ISPIPeripheral on SPI2, receives frames byte-by-byte
// ESP32 side: Python hooks call EnqueueFrameToStm32/DequeueFrameFromStm32
//
// Wire format: same as EspcpStub — 27-byte header + payload + 4-byte padding,
// SPI mode 3, max frame 4092 bytes.

using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.SPI;

namespace Antmicro.Renode.Peripherals.SPI
{
    public class SpiBridge : ISPIPeripheral, IGPIOReceiver
    {
        public SpiBridge()
        {
            Ready = new GPIO();
            MessageWaiting = new GPIO();
            toEsp32Queue = new Queue<byte[]>();
            toStm32Queue = new Queue<byte[]>();
            Reset();
        }

        // ── ISPIPeripheral (STM32 side) ──────────────────────────────────

        public byte Transmit(byte data)
        {
            // Buffer incoming MOSI byte from STM32
            if(rxIndex < rxBuffer.Length)
            {
                rxBuffer[rxIndex] = data;
            }
            rxIndex++;

            // Return MISO byte from ESP32's response (if available)
            byte result = 0x00;
            if(hasResponse && txIndex < txLength)
            {
                result = txBuffer[txIndex];
            }
            txIndex++;

            return result;
        }

        public void FinishTransmission()
        {
            int frameLength = rxIndex;
            rxIndex = 0;
            txIndex = 0;

            // Log frame details for debugging
            if(frameLength >= HeaderSize && rxBuffer[0] != 0x00)
            {
                // Decode ESPCP header: proto(1) + crc(4) + pktOff(2) + pktLen(2) + type(1) + iface(1) + func(4) + status(4) + msgId(4) + payloadLen(4)
                int msgType = rxBuffer[9];
                int iface = rxBuffer[10];
                uint func = (uint)(rxBuffer[11] | (rxBuffer[12] << 8) | (rxBuffer[13] << 16) | (rxBuffer[14] << 24));
                uint msgId = (uint)(rxBuffer[19] | (rxBuffer[20] << 8) | (rxBuffer[21] << 16) | (rxBuffer[22] << 24));
                uint payloadLen = (uint)(rxBuffer[23] | (rxBuffer[24] << 8) | (rxBuffer[25] << 16) | (rxBuffer[26] << 24));
                this.Log(LogLevel.Debug,
                    "SpiBridge: STM32 TX frame ({0}B) type=0x{1:X2} iface={2} func={3} msgId=0x{4:X8} payloadLen={5}",
                    frameLength, msgType, iface, func, msgId, payloadLen);
            }

            if(hasResponse)
            {
                // Previous response was consumed
                hasResponse = false;
                txLength = 0;
                MessageWaiting.Set(false);
            }

            if(frameLength < HeaderSize)
            {
                this.Log(LogLevel.Debug, "SpiBridge: Short frame ({0} bytes), ignoring", frameLength);
                return;
            }

            // Skip all-zero frames (rx-only exchanges)
            if(rxBuffer[0] == 0x00)
            {
                this.Log(LogLevel.Debug, "SpiBridge: Zero protocol byte, skipping");
                return;
            }

            if(!booted)
            {
                this.Log(LogLevel.Debug, "SpiBridge: Frame received but ESP32 not booted, ignoring");
                return;
            }

            // Copy the complete frame and queue it for the ESP32
            var frame = new byte[frameLength];
            Array.Copy(rxBuffer, 0, frame, 0, frameLength);

            lock(toEsp32Queue)
            {
                toEsp32Queue.Enqueue(frame);
            }

            this.Log(LogLevel.Debug,
                "SpiBridge: STM32→ESP32 frame queued ({0} bytes, proto=0x{1:X2})",
                frameLength, frame[0]);

            // Notify ESP32 SPI peripheral that a frame is available
            frameAvailableCallback?.Invoke();

            // Check if ESP32 already has a response ready
            CheckForEsp32Response();
        }

        // ── IGPIOReceiver ────────────────────────────────────────────────

        public void OnGPIO(int number, bool value)
        {
            switch(number)
            {
                case ResetGpioNumber:
                    if(value && !booted)
                    {
                        this.Log(LogLevel.Info, "SpiBridge: Reset released — ESP32 booting");
                        booted = true;
                        Ready.Set(true);
                    }
                    else if(!value)
                    {
                        this.Log(LogLevel.Info, "SpiBridge: Reset asserted");
                        booted = false;
                        hasResponse = false;
                        txLength = 0;
                        Ready.Set(false);
                        MessageWaiting.Set(false);
                        lock(toEsp32Queue) { toEsp32Queue.Clear(); }
                        lock(toStm32Queue) { toStm32Queue.Clear(); }
                    }
                    break;

                case CsGpioNumber:
                    break;

                default:
                    this.Log(LogLevel.Debug, "SpiBridge: Unhandled GPIO {0} = {1}", number, value);
                    break;
            }
        }

        // ── Public GPIO outputs ──────────────────────────────────────────

        public GPIO Ready { get; }
        public GPIO MessageWaiting { get; }

        // ── Cross-machine API (called by ESP32 Python hooks) ─────────────

        /// <summary>
        /// ESP32 calls this to get the next frame from STM32.
        /// Returns null if no frame is pending.
        /// </summary>
        public byte[] DequeueFrameFromStm32()
        {
            lock(toEsp32Queue)
            {
                if(toEsp32Queue.Count > 0)
                {
                    var frame = toEsp32Queue.Dequeue();
                    this.Log(LogLevel.Debug,
                        "SpiBridge: ESP32 dequeued STM32 frame ({0} bytes)", frame.Length);
                    return frame;
                }
            }
            return null;
        }

        /// <summary>
        /// ESP32 calls this to send a response/event frame back to STM32.
        /// </summary>
        public void EnqueueFrameToStm32(byte[] frame)
        {
            if(frame == null || frame.Length == 0)
                return;

            lock(toStm32Queue)
            {
                toStm32Queue.Enqueue(frame);
            }

            this.Log(LogLevel.Debug,
                "SpiBridge: ESP32→STM32 frame queued ({0} bytes)", frame.Length);

            // Make response available immediately
            CheckForEsp32Response();
        }

        /// <summary>
        /// ESP32 calls this to set the Ready signal state.
        /// </summary>
        public void SetReady(bool value)
        {
            Ready.Set(value);
        }

        /// <summary>
        /// ESP32 calls this to set the MessageWaiting signal state.
        /// </summary>
        public void SetMessageWaiting(bool value)
        {
            MessageWaiting.Set(value);
            if(value)
            {
                this.Log(LogLevel.Debug, "SpiBridge: MessageWaiting asserted by ESP32");
            }
        }

        /// <summary>
        /// Register a callback that fires when a frame is enqueued for the ESP32.
        /// </summary>
        public void SetFrameAvailableCallback(Action callback)
        {
            frameAvailableCallback = callback;
        }

        /// <summary>
        /// Returns the number of frames pending for ESP32 to process.
        /// </summary>
        public int FramesPendingForEsp32
        {
            get { lock(toEsp32Queue) { return toEsp32Queue.Count; } }
        }

        /// <summary>
        /// Returns the number of response frames pending for STM32.
        /// </summary>
        public int FramesPendingForStm32
        {
            get { lock(toStm32Queue) { return toStm32Queue.Count; } }
        }

        // ── Reset ────────────────────────────────────────────────────────

        public void Reset()
        {
            booted = true;
            hasResponse = false;
            rxIndex = 0;
            txIndex = 0;
            txLength = 0;
            Array.Clear(rxBuffer, 0, rxBuffer.Length);
            Array.Clear(txBuffer, 0, txBuffer.Length);
            lock(toEsp32Queue) { toEsp32Queue.Clear(); }
            lock(toStm32Queue) { toStm32Queue.Clear(); }
            Ready.Set(true);
            MessageWaiting.Set(true);
        }

        // ── Internal ─────────────────────────────────────────────────────

        private void CheckForEsp32Response()
        {
            if(hasResponse)
                return;

            byte[] frame = null;
            lock(toStm32Queue)
            {
                if(toStm32Queue.Count > 0)
                {
                    frame = toStm32Queue.Dequeue();
                }
            }

            if(frame != null)
            {
                int copyLen = Math.Min(frame.Length, txBuffer.Length);
                Array.Copy(frame, 0, txBuffer, 0, copyLen);
                txLength = copyLen;
                hasResponse = true;
                MessageWaiting.Set(true);

                this.Log(LogLevel.Debug,
                    "SpiBridge: ESP32 response loaded ({0} bytes), MessageWaiting=true",
                    copyLen);
            }
        }

        // ── Constants ────────────────────────────────────────────────────

        private const int HeaderSize = 27;
        private const int MaxSpiFrameSize = 4092;
        private const int ResetGpioNumber = 0;
        private const int CsGpioNumber = 1;

        // ── State ────────────────────────────────────────────────────────

        private readonly byte[] rxBuffer = new byte[MaxSpiFrameSize];
        private readonly byte[] txBuffer = new byte[MaxSpiFrameSize];
        private int rxIndex;
        private int txIndex;
        private int txLength;
        private bool hasResponse;
        private bool booted;

        // Cross-machine frame queues
        private readonly Queue<byte[]> toEsp32Queue;
        private readonly Queue<byte[]> toStm32Queue;
        private Action frameAvailableCallback;
    }
}
