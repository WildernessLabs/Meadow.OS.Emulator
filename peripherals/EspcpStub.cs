// ESPCP Stub — Virtual ESP32 coprocessor for Renode
// Implements the ESPCP SPI protocol so the STM32 ESPCP driver can complete
// its boot handshake and issue system/WiFi/BLE commands.
//
// Wire format: 27-byte header + payload + 4-byte padding, all LSB-first.
// SPI mode 3 (CPOL=1, CPHA=1), max frame 4092 bytes.

using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.SPI;

namespace Antmicro.Renode.Peripherals.SPI
{
    public class EspcpStub : ISPIPeripheral, IGPIOReceiver
    {
        public EspcpStub()
        {
            Ready = new GPIO();
            MessageWaiting = new GPIO();
            Reset();
        }

        // ── ISPIPeripheral ──────────────────────────────────────────────

        public byte Transmit(byte data)
        {
            // Buffer incoming MOSI byte
            if(rxIndex < rxBuffer.Length)
            {
                rxBuffer[rxIndex] = data;
            }
            rxIndex++;

            // Return outgoing MISO byte from prepared response
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

            if(hasResponse)
            {
                // Previous response was consumed by this transaction
                hasResponse = false;
                txLength = 0;
                MessageWaiting.Set(false);
            }

            if(frameLength < HeaderSize)
            {
                this.Log(LogLevel.Debug, "ESPCP: Short frame ({0} bytes), ignoring", frameLength);
                return;
            }

            // Skip frames that are all zeros (rx-only SPI exchanges send no meaningful data)
            if(rxBuffer[0] == 0x00)
            {
                this.Log(LogLevel.Debug, "ESPCP: Zero protocol byte, skipping (rx-only exchange)");
                return;
            }

            if(!booted)
            {
                this.Log(LogLevel.Debug, "ESPCP: Frame received but ESP32 not booted yet, ignoring");
                return;
            }

            // Parse the received ESPCP message
            var msg = DecodeMessage(rxBuffer, frameLength);
            if(msg == null)
            {
                this.Log(LogLevel.Warning, "ESPCP: Failed to decode message (CRC mismatch or bad frame)");
                // Prepare a NAK response
                PrepareNak(0, 0, 0, StatusCrcError);
                return;
            }

            this.Log(LogLevel.Debug,
                "ESPCP: RX type=0x{0:X2} iface={1} func={2} msgId=0x{3:X8} payloadLen={4}",
                msg.MessageType, msg.Interface, msg.Function, msg.MessageId, msg.PayloadLength);

            HandleMessage(msg, frameLength);

            // Signal MessageWaiting if we prepared a response
            if(hasResponse)
            {
                MessageWaiting.Set(true);
            }
        }

        // ── IGPIOReceiver ───────────────────────────────────────────────

        public void OnGPIO(int number, bool value)
        {
            switch(number)
            {
                case ResetGpioNumber:
                    // Reset line: active LOW (asserted = ESP32 held in reset)
                    // When released (HIGH), ESP32 boots
                    if(value && !booted)
                    {
                        this.Log(LogLevel.Info, "ESPCP: Reset released — ESP32 booting");
                        booted = true;
                        // Signal ready after "boot"
                        Ready.Set(true);
                    }
                    else if(!value)
                    {
                        this.Log(LogLevel.Info, "ESPCP: Reset asserted — ESP32 held in reset");
                        booted = false;
                        hasResponse = false;
                        txLength = 0;
                        Ready.Set(false);
                        MessageWaiting.Set(false);
                        pendingEvents.Clear();
                    }
                    break;

                case CsGpioNumber:
                    // CS managed by SPI controller — informational only
                    break;

                default:
                    this.Log(LogLevel.Debug, "ESPCP: Unhandled GPIO {0} = {1}", number, value);
                    break;
            }
        }

        // ── Public ──────────────────────────────────────────────────────

        public GPIO Ready { get; }
        public GPIO MessageWaiting { get; }

        public void Reset()
        {
            booted = true;  // Start ready — GPIO reset wiring is optional
            hasResponse = false;
            rxIndex = 0;
            txIndex = 0;
            txLength = 0;
            nextMessageId = 1;
            pendingEvents.Clear();
            Array.Clear(rxBuffer, 0, rxBuffer.Length);
            Array.Clear(txBuffer, 0, txBuffer.Length);
            // Signal ready so the STM32 ESPCP driver knows the ESP32 is up
            Ready.Set(true);
            // Signal message waiting to trigger initial ESPCP poll from STM32
            MessageWaiting.Set(true);
        }

        // ── Message handling ────────────────────────────────────────────

        private void HandleMessage(EspcpMessage msg, int frameLength)
        {
            switch(msg.Interface)
            {
                case InterfaceTransport:
                    HandleTransport(msg, frameLength);
                    break;

                case InterfaceSystem:
                    HandleSystem(msg, frameLength);
                    break;

                case InterfaceWiFi:
                    HandleWiFi(msg, frameLength);
                    break;

                case InterfaceBluetooth:
                    HandleBluetooth(msg, frameLength);
                    break;

                default:
                    this.Log(LogLevel.Warning,
                        "ESPCP: Unhandled interface {0}, func {1}", msg.Interface, msg.Function);
                    PrepareResponse(msg, StatusNotImplemented, null);
                    break;
            }
        }

        // ── Transport interface (5) ─────────────────────────────────────

        private void HandleTransport(EspcpMessage msg, int frameLength)
        {
            switch(msg.Function)
            {
                case TransportSendResponse:
                    // STM32 is polling: "do you have anything for me?"
                    if(pendingEvents.Count > 0)
                    {
                        // Send the queued event
                        var evt = pendingEvents.Dequeue();
                        PrepareRawResponse(evt);
                        this.Log(LogLevel.Debug, "ESPCP: Sending queued event to STM32");
                    }
                    else
                    {
                        // Nothing pending — return ACK with zero payload
                        PrepareAck(msg.Interface, msg.Function, msg.MessageId);
                    }
                    break;

                case TransportResetEsp32:
                    this.Log(LogLevel.Info, "ESPCP: Reset requested via transport");
                    booted = false;
                    booted = true;  // Immediate re-boot
                    Ready.Set(true);
                    PrepareAck(msg.Interface, msg.Function, msg.MessageId);
                    break;

                case TransportResponseReady:
                    PrepareAck(msg.Interface, msg.Function, msg.MessageId);
                    break;

                default:
                    this.Log(LogLevel.Warning, "ESPCP: Unhandled transport func {0}", msg.Function);
                    PrepareResponse(msg, StatusNotImplemented, null);
                    break;
            }
        }

        // ── System interface (4) ────────────────────────────────────────

        private void HandleSystem(EspcpMessage msg, int frameLength)
        {
            switch(msg.Function)
            {
                case SystemGetConfiguration:
                    // Return a mock device configuration
                    var configPayload = BuildConfigurationPayload();
                    PrepareResponse(msg, StatusOk, configPayload);
                    this.Log(LogLevel.Info, "ESPCP: Sent device configuration ({0} bytes)", configPayload.Length);
                    break;

                case SystemSetConfigurationItem:
                    // ACK — accept any configuration
                    PrepareAck(msg.Interface, msg.Function, msg.MessageId);
                    this.Log(LogLevel.Debug, "ESPCP: Set configuration item accepted");
                    break;

                case SystemGetBatteryChargeLevel:
                    // Return 100% charge
                    var batteryPayload = new byte[] { 0x64 }; // 100
                    PrepareResponse(msg, StatusOk, batteryPayload);
                    break;

                case SystemLoggingConfiguration:
                    PrepareAck(msg.Interface, msg.Function, msg.MessageId);
                    break;

                case SystemDeepSleep:
                    PrepareAck(msg.Interface, msg.Function, msg.MessageId);
                    this.Log(LogLevel.Info, "ESPCP: Deep sleep requested");
                    break;

                default:
                    this.Log(LogLevel.Warning, "ESPCP: Unhandled system func {0}", msg.Function);
                    PrepareResponse(msg, StatusNotImplemented, null);
                    break;
            }
        }

        // ── WiFi interface (1) ──────────────────────────────────────────

        private void HandleWiFi(EspcpMessage msg, int frameLength)
        {
            switch(msg.Function)
            {
                case WiFiStartInterface:
                    PrepareResponse(msg, StatusOk, null);
                    // Queue a "WiFi started" event for next poll
                    QueueEvent(InterfaceWiFi, WiFiEventStartInterface, StatusOk, null);
                    this.Log(LogLevel.Info, "ESPCP: WiFi interface started");
                    break;

                case WiFiStopInterface:
                    PrepareResponse(msg, StatusOk, null);
                    this.Log(LogLevel.Info, "ESPCP: WiFi interface stopped");
                    break;

                case WiFiConnectToDefaultAP:
                case WiFiConnectToAP:
                    PrepareResponse(msg, StatusOk, null);
                    // Queue a "network connected" event with mock IP
                    var ipPayload = BuildNetworkConnectedPayload();
                    QueueEvent(InterfaceWiFi, WiFiEventNetworkConnected, StatusOk, ipPayload);
                    this.Log(LogLevel.Info, "ESPCP: WiFi connected (mock)");
                    break;

                case WiFiDisconnectFromAP:
                    PrepareResponse(msg, StatusOk, null);
                    break;

                case WiFiGetAccessPoints:
                    // Return empty scan results
                    PrepareResponse(msg, StatusOk, new byte[0]);
                    break;

                case WiFiSetAntenna:
                    PrepareResponse(msg, StatusOk, null);
                    break;

                // Socket operations — stub responses
                case WiFiSocket:
                    // Return a mock file descriptor
                    var fdBytes = EncodeUint32((uint)nextSocketFd++);
                    PrepareResponse(msg, StatusOk, fdBytes);
                    break;

                case WiFiConnect:
                case WiFiBind:
                case WiFiListen:
                case WiFiSetSockOpt:
                case WiFiGetSockOpt:
                case WiFiIoctl:
                    PrepareResponse(msg, StatusOk, null);
                    break;

                case WiFiAccept:
                    var acceptFd = EncodeUint32((uint)nextSocketFd++);
                    PrepareResponse(msg, StatusOk, acceptFd);
                    break;

                case WiFiWrite:
                case WiFiSend:
                case WiFiSendTo:
                    // Return bytes "sent" = payload length
                    var sentBytes = EncodeUint32(msg.PayloadLength);
                    PrepareResponse(msg, StatusOk, sentBytes);
                    break;

                case WiFiRead:
                case WiFiRecv:
                case WiFiRecvFrom:
                    // Return empty data (no data available)
                    PrepareResponse(msg, StatusOk, new byte[0]);
                    break;

                case WiFiClose:
                    PrepareResponse(msg, StatusOk, null);
                    break;

                case WiFiPoll:
                    // Return: no events ready (4 bytes of zeros)
                    PrepareResponse(msg, StatusOk, new byte[4]);
                    break;

                case WiFiGetAddrInfo:
                    // Return mock address: 127.0.0.1
                    var addrPayload = BuildGetAddrInfoPayload();
                    PrepareResponse(msg, StatusOk, addrPayload);
                    break;

                default:
                    this.Log(LogLevel.Warning, "ESPCP: Unhandled WiFi func {0}", msg.Function);
                    PrepareResponse(msg, StatusNotImplemented, null);
                    break;
            }
        }

        // ── Bluetooth interface (2) ─────────────────────────────────────

        private void HandleBluetooth(EspcpMessage msg, int frameLength)
        {
            switch(msg.Function)
            {
                case BleStart:
                    PrepareResponse(msg, StatusOk, null);
                    this.Log(LogLevel.Info, "ESPCP: BLE started (stub)");
                    break;

                case BleStop:
                    PrepareResponse(msg, StatusOk, null);
                    this.Log(LogLevel.Info, "ESPCP: BLE stopped (stub)");
                    break;

                default:
                    this.Log(LogLevel.Warning, "ESPCP: Unhandled BLE func {0}", msg.Function);
                    PrepareResponse(msg, StatusNotImplemented, null);
                    break;
            }
        }

        // ── Payload builders ────────────────────────────────────────────

        private byte[] BuildConfigurationPayload()
        {
            // The configuration is a series of key-value pairs encoded as:
            // [key_length:1][key:N][value_length:2 LE][value:M]
            // Based on espcp_system.c config response format.
            var items = new List<byte>();

            AddConfigItem(items, "DeviceName", "MeadowF7");
            AddConfigItem(items, "WiFiMacAddress", "00:00:00:00:00:00");
            AddConfigItem(items, "SoftAPMacAddress", "00:00:00:00:00:00");
            AddConfigItem(items, "Antenna", "0");  // 0 = internal
            AddConfigItem(items, "AutomaticallyStartNetwork", "0");
            AddConfigItem(items, "DefaultAccessPoint", "");
            AddConfigItem(items, "SdSupported", "0");

            return items.ToArray();
        }

        private void AddConfigItem(List<byte> buffer, string key, string value)
        {
            var keyBytes = System.Text.Encoding.UTF8.GetBytes(key);
            var valueBytes = System.Text.Encoding.UTF8.GetBytes(value);

            buffer.Add((byte)keyBytes.Length);
            buffer.AddRange(keyBytes);
            buffer.Add((byte)(valueBytes.Length & 0xFF));
            buffer.Add((byte)((valueBytes.Length >> 8) & 0xFF));
            buffer.AddRange(valueBytes);
        }

        private byte[] BuildNetworkConnectedPayload()
        {
            // IP address info: ip(4) + subnet(4) + gateway(4)
            return new byte[]
            {
                192, 168, 1, 100,   // IP: 192.168.1.100
                255, 255, 255, 0,   // Subnet
                192, 168, 1, 1      // Gateway
            };
        }

        private byte[] BuildGetAddrInfoPayload()
        {
            // Return loopback address in sockaddr_in format
            return new byte[]
            {
                0x02, 0x00,         // AF_INET
                0x00, 0x00,         // port (filled by caller)
                127, 0, 0, 1,       // 127.0.0.1
                0, 0, 0, 0, 0, 0, 0, 0  // padding
            };
        }

        // ── Message encoding/decoding ───────────────────────────────────

        private EspcpMessage DecodeMessage(byte[] buffer, int length)
        {
            if(length < HeaderSize)
                return null;

            // Check protocol byte
            if(buffer[0] != ProtocolNumber)
            {
                this.Log(LogLevel.Warning, "ESPCP: Bad protocol byte 0x{0:X2}", buffer[0]);
                return null;
            }

            // Extract and verify CRC
            uint receivedCrc = ExtractUint32(buffer, CrcOffset);

            // Zero CRC field for verification
            buffer[CrcOffset] = 0;
            buffer[CrcOffset + 1] = 0;
            buffer[CrcOffset + 2] = 0;
            buffer[CrcOffset + 3] = 0;

            int crcLength = length > 4 ? length - 4 : length;  // Exclude last 4 bytes (SPI overhead)
            uint computedCrc = ComputeCrc32(buffer, crcLength);

            if(receivedCrc != computedCrc)
            {
                this.Log(LogLevel.Warning,
                    "ESPCP: CRC mismatch: received=0x{0:X8}, computed=0x{1:X8} (len={2})",
                    receivedCrc, computedCrc, crcLength);
                return null;
            }

            var msg = new EspcpMessage();
            msg.PacketOffset = ExtractUint16(buffer, 5);
            msg.PacketLength = ExtractUint16(buffer, 7);
            msg.MessageType = buffer[9];
            msg.Interface = buffer[10];
            msg.Function = ExtractUint32(buffer, 11);
            msg.StatusCode = ExtractUint32(buffer, 15);
            msg.MessageId = ExtractUint32(buffer, 19);
            msg.PayloadLength = ExtractUint32(buffer, 23);

            if(msg.PacketLength > 0 && length >= HeaderSize + msg.PacketLength)
            {
                msg.Payload = new byte[msg.PacketLength];
                Array.Copy(buffer, HeaderSize, msg.Payload, 0, msg.PacketLength);
            }

            return msg;
        }

        private void EncodeMessage(byte[] buffer, out int encodedLength,
            byte messageType, byte iface, uint function, uint statusCode,
            uint messageId, byte[] payload)
        {
            uint payloadLength = (payload != null) ? (uint)payload.Length : 0;
            uint packetLength = payloadLength;  // Single-packet for now

            // Calculate frame size: header + payload + 4-byte overhead, 4-byte aligned
            int rawSize = (int)(HeaderSize + packetLength + SpiOverhead);
            encodedLength = (rawSize + 3) & ~3;  // 4-byte align
            if(encodedLength < 8) encodedLength = 8;
            if(encodedLength > MaxSpiFrameSize) encodedLength = MaxSpiFrameSize;

            // Clear buffer
            Array.Clear(buffer, 0, Math.Min(encodedLength, buffer.Length));

            // Protocol byte
            buffer[0] = ProtocolNumber;

            // CRC placeholder (zeroed)
            buffer[1] = 0; buffer[2] = 0; buffer[3] = 0; buffer[4] = 0;

            // Header fields
            EncodeUint16((ushort)0, buffer, 5);              // packet_offset
            EncodeUint16((ushort)packetLength, buffer, 7);   // packet_length
            buffer[9] = messageType;
            buffer[10] = iface;
            EncodeUint32(function, buffer, 11);
            EncodeUint32(statusCode, buffer, 15);
            EncodeUint32(messageId, buffer, 19);
            EncodeUint32(payloadLength, buffer, 23);

            // Payload
            if(payload != null && payload.Length > 0)
            {
                Array.Copy(payload, 0, buffer, HeaderSize, payload.Length);
            }

            // CRC over everything except last 4 bytes
            int crcLength = encodedLength - 4;
            uint crc = ComputeCrc32(buffer, crcLength);
            EncodeUint32(crc, buffer, CrcOffset);
        }

        // ── Response preparation ────────────────────────────────────────

        private void PrepareResponse(EspcpMessage request, uint statusCode, byte[] payload)
        {
            uint msgId = GetNextMessageId();
            int encoded;
            EncodeMessage(txBuffer, out encoded,
                MsgTypeResponse, request.Interface, request.Function,
                statusCode, msgId, payload);
            txLength = encoded;
            hasResponse = true;
        }

        private void PrepareAck(byte iface, uint function, uint messageId)
        {
            int encoded;
            EncodeMessage(txBuffer, out encoded,
                MsgTypeAck, iface, function, StatusOk, messageId, null);
            txLength = encoded;
            hasResponse = true;
        }

        private void PrepareNak(byte iface, uint function, uint messageId, uint statusCode)
        {
            int encoded;
            EncodeMessage(txBuffer, out encoded,
                MsgTypeNak, iface, function, statusCode, messageId, null);
            txLength = encoded;
            hasResponse = true;
        }

        private void PrepareRawResponse(byte[] frame)
        {
            Array.Copy(frame, 0, txBuffer, 0, frame.Length);
            txLength = frame.Length;
            hasResponse = true;
        }

        private void QueueEvent(byte iface, uint function, uint statusCode, byte[] payload)
        {
            uint msgId = GetNextMessageId();
            var eventBuffer = new byte[MaxSpiFrameSize];
            int encoded;
            EncodeMessage(eventBuffer, out encoded,
                MsgTypeEvent, iface, function, statusCode, msgId, payload);
            var frame = new byte[encoded];
            Array.Copy(eventBuffer, 0, frame, 0, encoded);
            pendingEvents.Enqueue(frame);
        }

        // ── CRC-32 ─────────────────────────────────────────────────────

        private static uint ComputeCrc32(byte[] data, int length)
        {
            uint crc = Crc32Seed;
            for(int i = 0; i < length && i < data.Length; i++)
            {
                crc ^= data[i];
                for(int bit = 0; bit < 8; bit++)
                {
                    uint mask = (uint)(-(int)(crc & 1));
                    crc = (crc >> 1) ^ (Crc32Polynomial & mask);
                }
            }
            return crc;
        }

        // ── Encoding helpers ────────────────────────────────────────────

        private static ushort ExtractUint16(byte[] buf, int offset)
        {
            return (ushort)(buf[offset] | (buf[offset + 1] << 8));
        }

        private static uint ExtractUint32(byte[] buf, int offset)
        {
            return (uint)(buf[offset] | (buf[offset + 1] << 8) |
                          (buf[offset + 2] << 16) | (buf[offset + 3] << 24));
        }

        private static void EncodeUint16(ushort value, byte[] buf, int offset)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        private static void EncodeUint32(uint value, byte[] buf, int offset)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
            buf[offset + 2] = (byte)((value >> 16) & 0xFF);
            buf[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static byte[] EncodeUint32(uint value)
        {
            return new byte[]
            {
                (byte)(value & 0xFF),
                (byte)((value >> 8) & 0xFF),
                (byte)((value >> 16) & 0xFF),
                (byte)((value >> 24) & 0xFF)
            };
        }

        private uint GetNextMessageId()
        {
            // ESP32→STM32 messages have ID < 0x80000000
            return nextMessageId++;
        }

        // ── Message class ───────────────────────────────────────────────

        private class EspcpMessage
        {
            public byte MessageType;
            public byte Interface;
            public uint Function;
            public uint StatusCode;
            public uint MessageId;
            public ushort PacketOffset;
            public ushort PacketLength;
            public uint PayloadLength;
            public byte[] Payload;
        }

        // ── Constants ───────────────────────────────────────────────────

        // Protocol
        private const byte ProtocolNumber = 0x01;
        private const int HeaderSize = 27;
        private const int CrcOffset = 1;
        private const int MaxSpiFrameSize = 4092;
        private const int SpiOverhead = 4;

        // CRC
        private const uint Crc32Polynomial = 0xedb88320;
        private const uint Crc32Seed = 0xffffffff;

        // Message types
        private const byte MsgTypeAck = 0x00;
        private const byte MsgTypeNak = 0x01;
        private const byte MsgTypeReset = 0x02;
        private const byte MsgTypeEvent = 0x04;
        private const byte MsgTypeResponse = 0x10;
        private const byte MsgTypeTransport = 0x20;
        private const byte MsgTypeHeader = 0x40;
        private const byte MsgTypeData = 0x80;

        // Interfaces
        private const byte InterfaceNone = 0;
        private const byte InterfaceWiFi = 1;
        private const byte InterfaceBluetooth = 2;
        private const byte InterfaceMesh = 3;
        private const byte InterfaceSystem = 4;
        private const byte InterfaceTransport = 5;

        // Status codes
        private const uint StatusOk = 0;
        private const uint StatusCrcError = 1;
        private const uint StatusFailure = 3;
        private const uint StatusNotImplemented = 39;

        // Transport functions
        private const uint TransportResponseReady = 0;
        private const uint TransportSendResponse = 1;
        private const uint TransportKillNuttxThread = 2;
        private const uint TransportResetEsp32 = 3;

        // System functions
        private const uint SystemGetConfiguration = 0;
        private const uint SystemSetConfigurationItem = 1;
        private const uint SystemDeepSleep = 2;
        private const uint SystemGetBatteryChargeLevel = 3;
        private const uint SystemLoggingConfiguration = 13;

        // WiFi functions
        private const uint WiFiStartInterface = 0;
        private const uint WiFiStopInterface = 1;
        private const uint WiFiConnectToAP = 2;
        private const uint WiFiConnectToDefaultAP = 3;
        private const uint WiFiDisconnectFromAP = 5;
        private const uint WiFiGetAccessPoints = 6;
        private const uint WiFiSetAntenna = 7;
        private const uint WiFiSocket = 8;
        private const uint WiFiConnect = 9;
        private const uint WiFiWrite = 10;
        private const uint WiFiSetSockOpt = 11;
        private const uint WiFiGetSockOpt = 12;
        private const uint WiFiRead = 13;
        private const uint WiFiClose = 14;
        private const uint WiFiRecv = 15;
        private const uint WiFiRecvFrom = 16;
        private const uint WiFiPoll = 17;
        private const uint WiFiSend = 19;
        private const uint WiFiBind = 20;
        private const uint WiFiListen = 21;
        private const uint WiFiAccept = 22;
        private const uint WiFiSendTo = 24;
        private const uint WiFiGetAddrInfo = 27;
        private const uint WiFiIoctl = 39;

        // WiFi events
        private const uint WiFiEventStartInterface = 34;
        private const uint WiFiEventNetworkConnected = 36;

        // Bluetooth functions
        private const uint BleStart = 0;
        private const uint BleStop = 1;

        // GPIO numbers (as wired in .resc)
        private const int ResetGpioNumber = 0;
        private const int CsGpioNumber = 1;

        // ── State ───────────────────────────────────────────────────────

        private readonly byte[] rxBuffer = new byte[MaxSpiFrameSize];
        private readonly byte[] txBuffer = new byte[MaxSpiFrameSize];
        private int rxIndex;
        private int txIndex;
        private int txLength;
        private bool hasResponse;
        private bool booted;
        private uint nextMessageId = 1;
        private int nextSocketFd = 100;
        private readonly Queue<byte[]> pendingEvents = new Queue<byte[]>();
    }
}
