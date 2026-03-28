// ESPCP Stub — Virtual ESP32 coprocessor for Renode
// Implements the ESPCP SPI protocol so the STM32 ESPCP driver can complete
// its boot handshake and issue system/WiFi/BLE commands.
//
// Wire format: 27-byte header + payload + 4-byte padding, all LSB-first.
// SPI mode 3 (CPOL=1, CPHA=1), max frame 4092 bytes.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
            socketBridge = new SocketBridge(this);
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

            // Return outgoing MISO byte from the single response buffer.
            // Responses survive tx-only exchanges and are only consumed on rx-only.
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
            // Detect rx-only exchange: firmware sent zeros on MOSI to read our response
            bool wasRxOnly = (frameLength >= 1 && rxBuffer[0] == 0x00);
            rxIndex = 0;
            txIndex = 0;

            // Only consume responses on rx-only exchanges (firmware reading MISO).
            // On tx-only exchanges the firmware ignores MISO — don't consume.
            if(wasRxOnly)
            {
                if(hasResponse)
                {
                    this.Log(LogLevel.Info,
                        "ESPCP: Response consumed — type=0x{0:X2} iface={1} msgId=0x{2:X2}{3:X2}{4:X2}{5:X2} len={6}",
                        txLength > 9 ? txBuffer[9] : (byte)0,
                        txLength > 10 ? txBuffer[10] : (byte)0,
                        txLength > 22 ? txBuffer[22] : (byte)0,
                        txLength > 21 ? txBuffer[21] : (byte)0,
                        txLength > 20 ? txBuffer[20] : (byte)0,
                        txLength > 19 ? txBuffer[19] : (byte)0,
                        txLength);
                    hasResponse = false;
                    txLength = 0;
                    MessageWaiting.Set(false);
                }

                // Re-serve ONCE for multi-packet assembly: the firmware does one
                // additional rx-only exchange to extract the payload.
                if(!hasResponse && lastResponseBuffer != null)
                {
                    Array.Copy(lastResponseBuffer, 0, txBuffer, 0, lastResponseLength);
                    txLength = lastResponseLength;
                    hasResponse = true;
                    lastResponseBuffer = null;
                }
                // Restore displaced response (saved when PrepareResponse had to
                // overwrite txBuffer with a command ACK).
                else if(!hasResponse && displacedResponse != null)
                {
                    Array.Copy(displacedResponse, 0, txBuffer, 0, displacedResponseLength);
                    txLength = displacedResponseLength;
                    hasResponse = true;
                    lastResponseBuffer = displacedLastResponse;
                    lastResponseLength = displacedLastResponseLength;
                    displacedResponse = null;
                    displacedLastResponse = null;
                }
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
                // Silently ignore frames that fail CRC decode. During boot, the
                // firmware sends SPI handshake/sync frames (type=0x01) that have
                // the same ProtocolNumber but aren't ESPCP messages. If we send
                // a NAK here, it overwrites any pending command response in txBuffer,
                // causing the command's rx-only exchange to get the NAK instead.
                this.Log(LogLevel.Debug, "ESPCP: Failed to decode frame ({0} bytes), ignoring", frameLength);
                return;
            }

            frameCount++;

            // Configure loopback as WiFi on the first valid ESPCP frame.
            // This runs during the boot handshake, well before Mono starts,
            // ensuring Mono's GetAllNetworkInterfaces() finds a Wireless80211
            // adapter with a valid IP for DNS resolution.
            // ConfigureLoopbackAsWifi disabled for testing
            if(frameCount == 1)
            {
                ConfigureLoopbackAsWifi();
            }

            // Log non-poll frames at INFO level for debugging
            if(msg.Interface != InterfaceTransport || msg.Function != TransportSendResponse)
            {
                this.Log(LogLevel.Info,
                    "ESPCP: RX #{0} type=0x{1:X2} iface={2} func={3} msgId=0x{4:X8} payloadLen={5}",
                    frameCount, msg.MessageType, msg.Interface, msg.Function, msg.MessageId, msg.PayloadLength);
            }
            else if(frameCount % 1000 == 0)
            {
                this.Log(LogLevel.Info, "ESPCP: {0} frames processed ({1} pending events)",
                    frameCount, pendingEvents.Count);
            }

            // ACK/NAK frames from the firmware acknowledge receipt of our messages.
            // Don't prepare any response — the re-serve handles multi-packet reads.
            if(msg.MessageType == MsgTypeAck || msg.MessageType == MsgTypeNak)
            {
                return;
            }

            // New command — clear re-serve buffer
            lastResponseBuffer = null;

            HandleMessage(msg, frameLength);

            // Signal MessageWaiting if we have a response ready
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
                        socketBridge.CloseAll();
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
            if(socketBridge != null)
                socketBridge.CloseAll();
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
                    socketBridge.CheckPollRegistrations();
                    if(pendingEvents.Count > 0 && !hasResponse)
                    {
                        var evt = pendingEvents.Dequeue();
                        PrepareRawResponse(evt);
                        this.Log(LogLevel.Debug, "ESPCP: Sending queued response/event to STM32 ({0} remaining)",
                            pendingEvents.Count);
                    }
                    else if(!hasResponse)
                    {
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
                    // Queue "network connected" event with host IP info
                    var connectPayload = BuildNetworkConnectedPayload();
                    QueueEvent(InterfaceWiFi, WiFiEventNetworkConnected, StatusOk, connectPayload);
                    // Queue "got IP" event with DNS server address
                    var dnsPayload = BuildGotIpPayload();
                    QueueEvent(InterfaceWiFi, WiFiEventGotIp, StatusOk, dnsPayload);
                    this.Log(LogLevel.Info, "ESPCP: WiFi connected");
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

                // Socket operations — bridged to real host sockets
                case WiFiSocket:
                    PrepareResponse(msg, StatusOk, socketBridge.HandleSocket(msg.Payload));
                    break;

                case WiFiConnect:
                    PrepareResponse(msg, StatusOk, socketBridge.HandleConnect(msg.Payload));
                    break;

                case WiFiBind:
                    PrepareResponse(msg, StatusOk, socketBridge.HandleBind(msg.Payload));
                    break;

                case WiFiListen:
                    PrepareResponse(msg, StatusOk, socketBridge.HandleListen(msg.Payload));
                    break;

                case WiFiAccept:
                    PrepareResponse(msg, StatusOk, socketBridge.HandleAccept(msg.Payload));
                    break;

                case WiFiWrite:
                case WiFiSend:
                case WiFiSendTo:
                    PrepareResponse(msg, StatusOk, socketBridge.HandleSend(msg.Payload));
                    break;

                case WiFiRead:
                    PrepareResponse(msg, StatusOk, socketBridge.HandleRead(msg.Payload));
                    break;

                case WiFiRecvFrom:
                    PrepareResponse(msg, StatusOk, socketBridge.HandleRecvFrom(msg.Payload));
                    break;

                case WiFiClose:
                    PrepareResponse(msg, StatusOk, socketBridge.HandleClose(msg.Payload));
                    break;

                case WiFiPoll:
                    PrepareResponse(msg, StatusOk, socketBridge.HandlePoll(msg.Payload));
                    break;

                case WiFiSetSockOpt:
                    PrepareResponse(msg, StatusOk, socketBridge.HandleSetSockOpt(msg.Payload));
                    break;

                case WiFiGetSockOpt:
                    PrepareResponse(msg, StatusOk, socketBridge.HandleGetSockOpt(msg.Payload));
                    break;

                case WiFiIoctl:
                    PrepareResponse(msg, StatusOk, socketBridge.HandleIoctl(msg.Payload));
                    break;

                case WiFiGetAddrInfo:
                    // Firmware never calls this (espcp_usrsock_getaddrinfo returns -1).
                    // DNS goes through NuttX DNS client → UDP sockets → SocketBridge.
                    PrepareResponse(msg, StatusOk, new byte[0]);
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

        /// <summary>
        /// Build espcp_connect_event_data_t payload (57 bytes):
        /// ip_address(4) + subnet_mask(4) + gateway(4) + ssid(33) + bssid(6) +
        /// channel(1) + auth_mode(1) + reason(4)
        /// Uses the host machine's actual network interface when available.
        /// </summary>
        /// <summary>
        /// Configure the NuttX loopback device (g_loopback) to appear as a WiFi
        /// interface with the host's real IP. Mono's NetworkInterface API reads from
        /// NuttX's net_driver_s structs. Without a Wireless80211 interface, Mono's
        /// managed DNS resolver refuses to attempt resolution.
        /// </summary>
        private void ConfigureLoopbackAsWifi()
        {
            try
            {
                var machine = this.GetMachine();
                var bus = machine.SystemBus;

                // g_loopback struct layout (verified via GDB):
                // +0x2C: d_ifname (16 bytes)
                // +0x3D: d_lltype (1 byte) — NET_LL_LOOPBACK=1, NET_LL_IEEE80211=2
                // +0x40: d_ipaddr (4 bytes, NBO in LE memory)
                // +0x44: d_draddr (4 bytes)
                // +0x48: d_netmask (4 bytes)
                const uint loopbackAddr = 0x20030AD8;

                // Get host network info
                byte[] ip = { 192, 168, 1, 100 };
                byte[] subnet = { 255, 255, 255, 0 };
                byte[] gateway = { 192, 168, 1, 1 };
                try
                {
                    foreach(var iface in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if(iface.OperationalStatus != OperationalStatus.Up) continue;
                        if(iface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                        var props = iface.GetIPProperties();
                        foreach(var addr in props.UnicastAddresses)
                        {
                            if(addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                            ip = addr.Address.GetAddressBytes();
                            if(addr.IPv4Mask != null) subnet = addr.IPv4Mask.GetAddressBytes();
                            foreach(var gw in props.GatewayAddresses)
                            {
                                if(gw.Address.AddressFamily == AddressFamily.InterNetwork)
                                { gateway = gw.Address.GetAddressBytes(); break; }
                            }
                            goto done;
                        }
                    }
                }
                catch { }
                done:

                // Change d_lltype from Loopback(1) to IEEE80211(2)
                bus.WriteByte(loopbackAddr + 0x3D, 2);

                // Write IP addresses (NBO bytes stored directly in LE memory)
                for(int i = 0; i < 4; i++)
                {
                    bus.WriteByte(loopbackAddr + 0x40 + (uint)i, ip[i]);
                    bus.WriteByte(loopbackAddr + 0x44 + (uint)i, gateway[i]);
                    bus.WriteByte(loopbackAddr + 0x48 + (uint)i, subnet[i]);
                }

                this.Log(LogLevel.Info,
                    "ESPCP: Configured loopback as WiFi — IP={0}, gw={1}, mask={2}",
                    new IPAddress(ip), new IPAddress(gateway), new IPAddress(subnet));
            }
            catch(Exception ex)
            {
                this.Log(LogLevel.Warning,
                    "ESPCP: Failed to configure loopback: {0}", ex.Message);
            }
        }

        private byte[] BuildNetworkConnectedPayload()
        {
            byte[] ip = { 192, 168, 1, 100 };
            byte[] subnet = { 255, 255, 255, 0 };
            byte[] gateway = { 192, 168, 1, 1 };

            try
            {
                foreach(var iface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if(iface.OperationalStatus != OperationalStatus.Up)
                        continue;
                    if(iface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;

                    var props = iface.GetIPProperties();
                    foreach(var addr in props.UnicastAddresses)
                    {
                        if(addr.Address.AddressFamily != AddressFamily.InterNetwork)
                            continue;

                        ip = addr.Address.GetAddressBytes();
                        if(addr.IPv4Mask != null)
                            subnet = addr.IPv4Mask.GetAddressBytes();

                        // Get gateway
                        foreach(var gw in props.GatewayAddresses)
                        {
                            if(gw.Address.AddressFamily == AddressFamily.InterNetwork)
                            {
                                gateway = gw.Address.GetAddressBytes();
                                break;
                            }
                        }

                        this.Log(LogLevel.Info,
                            "ESPCP: Using host network — IP={0}, subnet={1}, gateway={2}",
                            new IPAddress(ip), new IPAddress(subnet), new IPAddress(gateway));
                        goto done;
                    }
                }
            }
            catch(Exception ex)
            {
                this.Log(LogLevel.Warning,
                    "ESPCP: Failed to query host network, using defaults: {0}", ex.Message);
            }
            done:

            // espcp_connect_event_data_t layout:
            // ip_address(4) + subnet_mask(4) + gateway(4) + ssid(33) + bssid(6) +
            // channel(1) + auth_mode(1) + reason(4) = 57 bytes
            var payload = new byte[57];
            // IP addresses: the ESP32 stores ip4_addr_t as uint32 in NBO. On LE hardware,
            // in_addr.s_addr for 192.168.0.105 = 0x6900A8C0 (memory: {C0, A8, 00, 69}).
            // espcp_encode_uint32 writes this uint32 in LE: bytes {C0, A8, 00, 69}.
            // GetAddressBytes() returns the same NBO bytes: {C0, A8, 00, 69}.
            // So write them directly — no reversal needed.
            Array.Copy(ip, 0, payload, 0, 4);
            Array.Copy(subnet, 0, payload, 4, 4);
            Array.Copy(gateway, 0, payload, 8, 4);

            // ssid: "Emulator" (null-padded to 33 bytes)
            var ssid = System.Text.Encoding.UTF8.GetBytes("Emulator");
            Array.Copy(ssid, 0, payload, 12, Math.Min(ssid.Length, 32));

            // bssid(6) + channel(1) + auth_mode(1) + reason(4) left as zeros
            return payload;
        }

        /// <summary>
        /// Build espcp_got_ip_event_data_t payload (4 bytes): dns_address(uint32 LE)
        /// Uses 8.8.8.8 (Google DNS) as default, or the host's configured DNS server.
        /// </summary>
        private byte[] BuildGotIpPayload()
        {
            byte[] dns = { 8, 8, 8, 8 }; // Google DNS default

            try
            {
                foreach(var iface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if(iface.OperationalStatus != OperationalStatus.Up)
                        continue;
                    if(iface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;

                    var props = iface.GetIPProperties();
                    foreach(var dnsAddr in props.DnsAddresses)
                    {
                        if(dnsAddr.AddressFamily == AddressFamily.InterNetwork)
                        {
                            dns = dnsAddr.GetAddressBytes();
                            this.Log(LogLevel.Info,
                                "ESPCP: Using host DNS server: {0}", new IPAddress(dns));
                            goto done;
                        }
                    }
                }
            }
            catch(Exception ex)
            {
                this.Log(LogLevel.Warning,
                    "ESPCP: Failed to query host DNS, using 8.8.8.8: {0}", ex.Message);
            }
            done:

            // dns_address: NBO bytes written directly (same as IP encoding)
            return new byte[] { dns[0], dns[1], dns[2], dns[3] };
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
            // The firmware's protocol: espcp_send_message sends the command, then reads
            // a header-only ACK (get_message_header_acknowledgement). The ACTUAL response
            // with payload is read later during a poll cycle via espcp_get_message.
            //
            // So: serve an ACK immediately, and queue the full response for poll delivery.

            // 1. Queue the full response for later poll delivery
            var responseBuffer = new byte[MaxSpiFrameSize];
            int responseEncoded;
            EncodeMessage(responseBuffer, out responseEncoded,
                MsgTypeResponse, request.Interface, request.Function,
                statusCode, request.MessageId, payload);
            var responseFrame = new byte[responseEncoded];
            Array.Copy(responseBuffer, 0, responseFrame, 0, responseEncoded);
            pendingEvents.Enqueue(responseFrame);

            // 2. Serve an ACK for the immediate header read.
            // If txBuffer already has a pending response (from a previous poll), save it
            // so it can be restored after the ACK is consumed. Without this, overlapping
            // commands clobber pending events/responses.
            if(hasResponse)
            {
                displacedResponse = new byte[txLength];
                Array.Copy(txBuffer, 0, displacedResponse, 0, txLength);
                displacedResponseLength = txLength;
                // Also preserve the re-serve buffer if one exists
                displacedLastResponse = lastResponseBuffer;
                displacedLastResponseLength = lastResponseLength;
                lastResponseBuffer = null;
            }
            int ackEncoded;
            EncodeMessage(txBuffer, out ackEncoded,
                MsgTypeAck, request.Interface, request.Function,
                StatusOk, request.MessageId, null);
            txLength = ackEncoded;
            hasResponse = true;

            this.Log(LogLevel.Info,
                "ESPCP: Command response queued — iface={0} func={1} msgId=0x{2:X8} len={3}",
                request.Interface, request.Function, request.MessageId, responseEncoded);
        }

        private void PrepareAck(byte iface, uint function, uint messageId)
        {
            if(hasResponse)
                return;
            int encoded;
            EncodeMessage(txBuffer, out encoded,
                MsgTypeAck, iface, function, StatusOk, messageId, null);
            txLength = encoded;
            hasResponse = true;
        }

        private void PrepareNak(byte iface, uint function, uint messageId, uint statusCode)
        {
            if(hasResponse)
                return;
            int encoded;
            EncodeMessage(txBuffer, out encoded,
                MsgTypeNak, iface, function, statusCode, messageId, null);
            txLength = encoded;
            hasResponse = true;
        }

        private void PrepareRawResponse(byte[] frame)
        {
            // Don't overwrite a pending command response
            if(hasResponse)
                return;
            // Serve header-only frame (32 bytes) for the first read.
            // The firmware reads header_only_buffer_size bytes first to get payload_length,
            // then does a second read for the full frame. CRC must match the served size.
            int headerOnlySize = ((HeaderSize + 3) & ~3) + SpiOverhead; // 32 bytes
            if(frame.Length > headerOnlySize)
            {
                // Re-encode as header-only frame with matching CRC
                var headerFrame = new byte[headerOnlySize];
                Array.Copy(frame, 0, headerFrame, 0, Math.Min(frame.Length, headerOnlySize));
                // Recompute CRC over header-only size
                headerFrame[CrcOffset] = 0;
                headerFrame[CrcOffset + 1] = 0;
                headerFrame[CrcOffset + 2] = 0;
                headerFrame[CrcOffset + 3] = 0;
                uint crc = ComputeCrc32(headerFrame, headerOnlySize - SpiOverhead);
                EncodeUint32(crc, headerFrame, CrcOffset);
                Array.Copy(headerFrame, 0, txBuffer, 0, headerOnlySize);
                txLength = headerOnlySize;
                hasResponse = true;
                // Save the FULL frame for re-serve (multi-packet payload read)
                lastResponseBuffer = new byte[frame.Length];
                Array.Copy(frame, 0, lastResponseBuffer, 0, frame.Length);
                lastResponseLength = frame.Length;
            }
            else
            {
                Array.Copy(frame, 0, txBuffer, 0, frame.Length);
                txLength = frame.Length;
                hasResponse = true;
                // Save for re-serve even for small/zero-payload frames.
                // The firmware's do-while loop always reads at least once after
                // the header read, even for payload_length=0.
                lastResponseBuffer = new byte[frame.Length];
                Array.Copy(frame, 0, lastResponseBuffer, 0, frame.Length);
                lastResponseLength = frame.Length;
            }
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

        // WiFi functions (values from espcp_shared_enums.h espcp_wi_fi_function)
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
        private const uint WiFiSendTo = 15;
        private const uint WiFiRecvFrom = 16;
        private const uint WiFiPoll = 17;
        private const uint WiFiSend = 19;
        private const uint WiFiBind = 20;
        private const uint WiFiListen = 21;
        private const uint WiFiAccept = 22;
        private const uint WiFiIoctl = 23;
        private const uint WiFiGetAddrInfo = 27;

        // WiFi events and async responses
        private const uint WiFiInterruptPollResponse = 18;
        private const uint WiFiEventStartInterface = 34;
        private const uint WiFiEventNetworkConnected = 36;
        private const uint WiFiEventGotIp = 48;

        // Bluetooth functions
        private const uint BleStart = 0;
        private const uint BleStop = 1;

        // GPIO numbers (as wired in .resc)
        private const int ResetGpioNumber = 0;
        private const int CsGpioNumber = 1;

        // ── Socket Bridge ─────────────────────────────────────────────
        // Bridges ESPCP WiFi socket operations to real host sockets.
        // All sockets are non-blocking to avoid stalling the Renode
        // simulation thread.

        private class SocketBridge
        {
            private readonly EspcpStub parent;
            private readonly Dictionary<int, Socket> sockets = new Dictionary<int, Socket>();
            private readonly Dictionary<uint, PollRegistration> pollRegistrations = new Dictionary<uint, PollRegistration>();
            private int nextFd = 1;

            private class PollRegistration
            {
                public int Fd;
                public ushort Events;
                public uint SetupMessageId;
            }

            // POSIX errno values (Linux/ARM)
            private const int EAGAIN = 11;
            private const int EBADF = 9;
            private const int EINPROGRESS = 115;
            private const int ECONNREFUSED = 111;
            private const int ECONNRESET = 104;
            private const int ETIMEDOUT = 110;
            private const int EPIPE = 32;
            private const int ENOTCONN = 107;
            private const int EADDRINUSE = 98;
            private const int ENETUNREACH = 101;
            private const int EHOSTUNREACH = 113;
            private const int EINVAL = 22;
            private const int ENOMEM = 12;
            private const int EAFNOSUPPORT = 97;

            // POSIX poll event flags
            private const ushort POLLIN = 0x0001;
            private const ushort POLLOUT = 0x0004;
            private const ushort POLLERR = 0x0008;
            private const ushort POLLHUP = 0x0010;
            private const ushort POLLNVAL = 0x0020;

            // espcp_sock_addr_t wire size
            private const int SockAddrSize = 31;

            // Max read/write buffer (firmware-side limit)
            private const int MaxReadWriteBuffer = 4000;

            public SocketBridge(EspcpStub parent)
            {
                this.parent = parent;
            }

            public void CloseAll()
            {
                foreach(var kvp in sockets)
                {
                    try { kvp.Value.Close(); } catch { }
                }
                sockets.Clear();
                pollRegistrations.Clear();
                nextFd = 1;
            }

            // ── Payload decode helpers ──────────────────────────────

            private static int DecodeInt32(byte[] buf, int offset)
            {
                return (int)(buf[offset] | (buf[offset + 1] << 8) |
                    (buf[offset + 2] << 16) | (buf[offset + 3] << 24));
            }

            private static uint DecodeUint32(byte[] buf, int offset)
            {
                return (uint)(buf[offset] | (buf[offset + 1] << 8) |
                    (buf[offset + 2] << 16) | (buf[offset + 3] << 24));
            }

            private static ushort DecodeUint16(byte[] buf, int offset)
            {
                return (ushort)(buf[offset] | (buf[offset + 1] << 8));
            }

            private static IPEndPoint DecodeSockAddr(byte[] buf, int offset)
            {
                // espcp_sock_addr_t: family(1) port(2,LE) ip4(4,LE) ...
                // byte family = buf[offset];
                ushort portWire = DecodeUint16(buf, offset + 1);
                uint ip4 = DecodeUint32(buf, offset + 3);
                // ip4: firmware stores NBO uint32, encoded as LE → wire bytes are NBO.
                // DecodeUint32 reads LE → gets NBO value. IPAddress(long) on LE
                // interprets the low bytes as the address octets, matching NBO storage.
                var addr = new IPAddress((long)ip4);
                // port: firmware stores NBO uint16, encoded as LE → wire bytes are NBO.
                // DecodeUint16 reads LE → gets NBO value (e.g., port 80 → 0x5000).
                // IPEndPoint expects host-order port, so swap bytes.
                int port = ((portWire >> 8) & 0xFF) | ((portWire & 0xFF) << 8);
                return new IPEndPoint(addr, port);
            }

            private static string DecodeNullTerminatedString(byte[] buf, int offset, out int bytesConsumed)
            {
                int end = offset;
                while(end < buf.Length && buf[end] != 0)
                    end++;
                bytesConsumed = end - offset + 1; // +1 for the null terminator
                return System.Text.Encoding.UTF8.GetString(buf, offset, end - offset);
            }

            // ── Payload encode helpers ──────────────────────────────

            private static byte[] EncodeInt32(int value)
            {
                return new byte[]
                {
                    (byte)(value & 0xFF),
                    (byte)((value >> 8) & 0xFF),
                    (byte)((value >> 16) & 0xFF),
                    (byte)((value >> 24) & 0xFF)
                };
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

            private static byte[] EncodeUint16(ushort value)
            {
                return new byte[] { (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF) };
            }

            /// <summary>
            /// Encode integer_and_errno response: result(4) + errno(4)
            /// </summary>
            private static byte[] EncodeIntegerAndErrno(int result, int errno)
            {
                var buf = new byte[8];
                buf[0] = (byte)(result & 0xFF);
                buf[1] = (byte)((result >> 8) & 0xFF);
                buf[2] = (byte)((result >> 16) & 0xFF);
                buf[3] = (byte)((result >> 24) & 0xFF);
                buf[4] = (byte)(errno & 0xFF);
                buf[5] = (byte)((errno >> 8) & 0xFF);
                buf[6] = (byte)((errno >> 16) & 0xFF);
                buf[7] = (byte)((errno >> 24) & 0xFF);
                return buf;
            }

            /// <summary>
            /// Encode espcp_sock_addr_t: family(1) port(2) ip4(4) flow_info(4) ip6(16) scope_id(4) = 31 bytes
            /// </summary>
            private static byte[] EncodeSockAddr(IPEndPoint ep)
            {
                var buf = new byte[SockAddrSize];
                buf[0] = 2; // AF_INET
                // Port: firmware reads with espcp_extract_uint16 (LE) and assigns
                // directly to sin_port (NBO). So we must write NBO port in LE format.
                // NBO port 80 = 0x0050 → LE bytes = [0x50, 0x00]. But firmware
                // stores NBO in the uint16, so encode as: high byte first in LE slot.
                // ep.Port is host-order int. Convert to NBO then write LE.
                int port = ep.Port;
                ushort portNbo = (ushort)(((port & 0xFF) << 8) | ((port >> 8) & 0xFF));
                buf[1] = (byte)(portNbo & 0xFF);
                buf[2] = (byte)((portNbo >> 8) & 0xFF);
                // IPv4 address: firmware reads with espcp_extract_uint32 (LE) and
                // assigns directly to sin_addr.s_addr (NBO). GetAddressBytes() returns
                // NBO bytes. Writing them directly matches what the firmware expects.
                var addrBytes = ep.Address.GetAddressBytes();
                if(addrBytes.Length >= 4)
                {
                    buf[3] = addrBytes[0];
                    buf[4] = addrBytes[1];
                    buf[5] = addrBytes[2];
                    buf[6] = addrBytes[3];
                }
                // flow_info(4) + ip6(16) + scope_id(4) left as zeros
                return buf;
            }

            /// <summary>
            /// Encode read response: buffer_length(4) + buffer(N) + result(4) + errno(4)
            /// </summary>
            private static byte[] EncodeReadResponse(byte[] data, int result, int errno)
            {
                uint bufLen = (data != null) ? (uint)data.Length : 0;
                var buf = new byte[4 + bufLen + 4 + 4];
                int offset = 0;
                // buffer_length
                buf[offset++] = (byte)(bufLen & 0xFF);
                buf[offset++] = (byte)((bufLen >> 8) & 0xFF);
                buf[offset++] = (byte)((bufLen >> 16) & 0xFF);
                buf[offset++] = (byte)((bufLen >> 24) & 0xFF);
                // buffer
                if(data != null && data.Length > 0)
                {
                    Array.Copy(data, 0, buf, offset, data.Length);
                    offset += data.Length;
                }
                // result
                buf[offset++] = (byte)(result & 0xFF);
                buf[offset++] = (byte)((result >> 8) & 0xFF);
                buf[offset++] = (byte)((result >> 16) & 0xFF);
                buf[offset++] = (byte)((result >> 24) & 0xFF);
                // errno
                buf[offset++] = (byte)(errno & 0xFF);
                buf[offset++] = (byte)((errno >> 8) & 0xFF);
                buf[offset++] = (byte)((errno >> 16) & 0xFF);
                buf[offset++] = (byte)((errno >> 24) & 0xFF);
                return buf;
            }

            /// <summary>
            /// Encode recv_from response: buffer_length(4) + buffer(N) + result(4) + errno(4)
            ///   + source_addr_length(4) + source_addr(M) + source_addr_len(4)
            /// </summary>
            private static byte[] EncodeRecvFromResponse(byte[] data, int result, int errno,
                IPEndPoint source, bool includeSource)
            {
                uint bufLen = (data != null) ? (uint)data.Length : 0;
                byte[] addrBytes = null;
                uint addrLen = 0;
                if(includeSource && source != null)
                {
                    addrBytes = EncodeSockAddr(source);
                    addrLen = (uint)addrBytes.Length;
                }
                // buffer_length(4) + buffer(N) + result(4) + errno(4)
                //   + source_addr_length(4) + source_addr(M) + source_addr_len(4)
                var buf = new byte[4 + bufLen + 4 + 4 + 4 + addrLen + 4];
                int offset = 0;
                // buffer_length
                buf[offset++] = (byte)(bufLen & 0xFF);
                buf[offset++] = (byte)((bufLen >> 8) & 0xFF);
                buf[offset++] = (byte)((bufLen >> 16) & 0xFF);
                buf[offset++] = (byte)((bufLen >> 24) & 0xFF);
                // buffer
                if(data != null && data.Length > 0)
                {
                    Array.Copy(data, 0, buf, offset, data.Length);
                    offset += data.Length;
                }
                // result
                buf[offset++] = (byte)(result & 0xFF);
                buf[offset++] = (byte)((result >> 8) & 0xFF);
                buf[offset++] = (byte)((result >> 16) & 0xFF);
                buf[offset++] = (byte)((result >> 24) & 0xFF);
                // errno
                buf[offset++] = (byte)(errno & 0xFF);
                buf[offset++] = (byte)((errno >> 8) & 0xFF);
                buf[offset++] = (byte)((errno >> 16) & 0xFF);
                buf[offset++] = (byte)((errno >> 24) & 0xFF);
                // source_addr_length
                buf[offset++] = (byte)(addrLen & 0xFF);
                buf[offset++] = (byte)((addrLen >> 8) & 0xFF);
                buf[offset++] = (byte)((addrLen >> 16) & 0xFF);
                buf[offset++] = (byte)((addrLen >> 24) & 0xFF);
                // source_addr
                if(addrBytes != null)
                {
                    Array.Copy(addrBytes, 0, buf, offset, addrBytes.Length);
                    offset += addrBytes.Length;
                }
                // source_addr_len (redundant but firmware expects it)
                buf[offset++] = (byte)(addrLen & 0xFF);
                buf[offset++] = (byte)((addrLen >> 8) & 0xFF);
                buf[offset++] = (byte)((addrLen >> 16) & 0xFF);
                buf[offset++] = (byte)((addrLen >> 24) & 0xFF);
                return buf;
            }

            /// <summary>
            /// Encode poll response: returned_events(2) + result(4) + errno(4)
            /// </summary>
            private static byte[] EncodePollResponse(ushort events, int result, int errno)
            {
                var buf = new byte[10];
                buf[0] = (byte)(events & 0xFF);
                buf[1] = (byte)((events >> 8) & 0xFF);
                buf[2] = (byte)(result & 0xFF);
                buf[3] = (byte)((result >> 8) & 0xFF);
                buf[4] = (byte)((result >> 16) & 0xFF);
                buf[5] = (byte)((result >> 24) & 0xFF);
                buf[6] = (byte)(errno & 0xFF);
                buf[7] = (byte)((errno >> 8) & 0xFF);
                buf[8] = (byte)((errno >> 16) & 0xFF);
                buf[9] = (byte)((errno >> 24) & 0xFF);
                return buf;
            }

            /// <summary>
            /// Encode accept response: addr_length(4) + addr(N) + result(4) + errno(4)
            /// </summary>
            private static byte[] EncodeAcceptResponse(IPEndPoint ep, int result, int errno)
            {
                byte[] addrBytes = null;
                uint addrLen = 0;
                if(ep != null)
                {
                    addrBytes = EncodeSockAddr(ep);
                    addrLen = (uint)addrBytes.Length;
                }
                var buf = new byte[4 + addrLen + 4 + 4];
                int offset = 0;
                // addr_length
                buf[offset++] = (byte)(addrLen & 0xFF);
                buf[offset++] = (byte)((addrLen >> 8) & 0xFF);
                buf[offset++] = (byte)((addrLen >> 16) & 0xFF);
                buf[offset++] = (byte)((addrLen >> 24) & 0xFF);
                // addr
                if(addrBytes != null)
                {
                    Array.Copy(addrBytes, 0, buf, offset, addrBytes.Length);
                    offset += addrBytes.Length;
                }
                // result
                buf[offset++] = (byte)(result & 0xFF);
                buf[offset++] = (byte)((result >> 8) & 0xFF);
                buf[offset++] = (byte)((result >> 16) & 0xFF);
                buf[offset++] = (byte)((result >> 24) & 0xFF);
                // errno
                buf[offset++] = (byte)(errno & 0xFF);
                buf[offset++] = (byte)((errno >> 8) & 0xFF);
                buf[offset++] = (byte)((errno >> 16) & 0xFF);
                buf[offset++] = (byte)((errno >> 24) & 0xFF);
                return buf;
            }

            // ── .NET SocketError → POSIX errno mapping ──────────────

            private static int MapSocketError(SocketError err)
            {
                switch(err)
                {
                    case SocketError.WouldBlock: return EAGAIN;
                    case SocketError.InProgress: return EINPROGRESS;
                    case SocketError.ConnectionRefused: return ECONNREFUSED;
                    case SocketError.ConnectionReset: return ECONNRESET;
                    case SocketError.TimedOut: return ETIMEDOUT;
                    case SocketError.NotConnected: return ENOTCONN;
                    case SocketError.AddressAlreadyInUse: return EADDRINUSE;
                    case SocketError.NetworkUnreachable: return ENETUNREACH;
                    case SocketError.HostUnreachable: return EHOSTUNREACH;
                    case SocketError.InvalidArgument: return EINVAL;
                    case SocketError.AddressFamilyNotSupported: return EAFNOSUPPORT;
                    case SocketError.ConnectionAborted: return ECONNRESET;
                    case SocketError.Shutdown: return EPIPE;
                    default: return (int)err; // best effort
                }
            }

            // ── Socket operation handlers ───────────────────────────

            /// <summary>
            /// WiFiSocket (func=8): Create a real host socket.
            /// Request: address_info(4) + domain(4) + type(4) + protocol(4)
            /// Response: integer_and_errno (result = fd)
            /// </summary>
            public byte[] HandleSocket(byte[] payload)
            {
                if(payload == null || payload.Length < 16)
                {
                    parent.Log(LogLevel.Warning, "SocketBridge: Socket request too short");
                    return EncodeIntegerAndErrno(-1, EINVAL);
                }

                // Skip address_info (4 bytes) — not used
                int domain = DecodeInt32(payload, 4);
                int type = DecodeInt32(payload, 8);
                int protocol = DecodeInt32(payload, 12);

                // Map to .NET types
                AddressFamily af;
                switch(domain)
                {
                    case 2: af = AddressFamily.InterNetwork; break;   // AF_INET
                    case 10: af = AddressFamily.InterNetworkV6; break; // AF_INET6
                    default:
                        parent.Log(LogLevel.Warning, "SocketBridge: Unsupported domain {0}", domain);
                        return EncodeIntegerAndErrno(-1, EAFNOSUPPORT);
                }

                SocketType st;
                switch(type)
                {
                    case 1: st = SocketType.Stream; break;   // SOCK_STREAM
                    case 2: st = SocketType.Dgram; break;    // SOCK_DGRAM
                    case 3: st = SocketType.Raw; break;      // SOCK_RAW
                    default:
                        parent.Log(LogLevel.Warning, "SocketBridge: Unsupported socket type {0}", type);
                        return EncodeIntegerAndErrno(-1, EINVAL);
                }

                ProtocolType pt;
                switch(protocol)
                {
                    case 0: pt = ProtocolType.Unspecified; break;
                    case 6: pt = ProtocolType.Tcp; break;    // IPPROTO_TCP
                    case 17: pt = ProtocolType.Udp; break;   // IPPROTO_UDP
                    default: pt = (ProtocolType)protocol; break;
                }

                try
                {
                    var sock = new Socket(af, st, pt);
                    sock.Blocking = false;
                    int fd = nextFd++;
                    sockets[fd] = sock;
                    parent.Log(LogLevel.Info,
                        "SocketBridge: Created socket fd={0} domain={1} type={2} proto={3}",
                        fd, domain, type, protocol);
                    return EncodeIntegerAndErrno(fd, 0);
                }
                catch(SocketException ex)
                {
                    parent.Log(LogLevel.Warning,
                        "SocketBridge: Socket creation failed: {0}", ex.Message);
                    return EncodeIntegerAndErrno(-1, MapSocketError(ex.SocketErrorCode));
                }
            }

            /// <summary>
            /// WiFiClose (func=14): Close a socket.
            /// Request: socket_handle(4)
            /// Response: integer_and_errno
            /// </summary>
            public byte[] HandleClose(byte[] payload)
            {
                if(payload == null || payload.Length < 4)
                    return EncodeIntegerAndErrno(-1, EINVAL);

                int fd = DecodeInt32(payload, 0);
                Socket sock;
                if(!sockets.TryGetValue(fd, out sock))
                {
                    parent.Log(LogLevel.Warning, "SocketBridge: Close on invalid fd={0}", fd);
                    return EncodeIntegerAndErrno(-1, EBADF);
                }

                try { sock.Close(); } catch { }
                sockets.Remove(fd);
                // Remove any poll registrations for this socket
                var stalePolls = new List<uint>();
                foreach(var kvp in pollRegistrations)
                {
                    if(kvp.Value.Fd == fd)
                        stalePolls.Add(kvp.Key);
                }
                foreach(var key in stalePolls)
                    pollRegistrations.Remove(key);
                parent.Log(LogLevel.Debug, "SocketBridge: Closed fd={0}", fd);
                return EncodeIntegerAndErrno(0, 0);
            }

            /// <summary>
            /// WiFiConnect (func=9): TCP connect (non-blocking).
            /// Request: socket_handle(4) + addr_length(4) + addr(N)
            /// Response: integer_and_errno
            /// </summary>
            public byte[] HandleConnect(byte[] payload)
            {
                if(payload == null || payload.Length < 8)
                    return EncodeIntegerAndErrno(-1, EINVAL);

                int fd = DecodeInt32(payload, 0);
                uint addrLen = DecodeUint32(payload, 4);

                Socket sock;
                if(!sockets.TryGetValue(fd, out sock))
                    return EncodeIntegerAndErrno(-1, EBADF);

                if(addrLen == 0 || payload.Length < 8 + addrLen)
                    return EncodeIntegerAndErrno(-1, EINVAL);

                var ep = DecodeSockAddr(payload, 8);
                parent.Log(LogLevel.Info,
                    "SocketBridge: Connect fd={0} to {1}", fd, ep);

                try
                {
                    sock.Connect(ep);
                    // Non-blocking connect completed immediately
                    return EncodeIntegerAndErrno(0, 0);
                }
                catch(SocketException ex)
                {
                    if(ex.SocketErrorCode == SocketError.WouldBlock ||
                       ex.SocketErrorCode == SocketError.InProgress)
                    {
                        // Connect in progress — firmware will poll for completion
                        return EncodeIntegerAndErrno(-1, EINPROGRESS);
                    }
                    parent.Log(LogLevel.Warning,
                        "SocketBridge: Connect failed: {0}", ex.Message);
                    return EncodeIntegerAndErrno(-1, MapSocketError(ex.SocketErrorCode));
                }
            }

            /// <summary>
            /// WiFiSend/WiFiWrite/WiFiSendTo (func=19/10/24): Send data.
            /// Request: socket_handle(4) + buffer_length(4) + buffer(N) + length(4) + flags(4)
            ///        + dest_addr_length(4) + dest_addr(M)
            /// Response: integer_and_errno (result = bytes sent)
            /// </summary>
            public byte[] HandleSend(byte[] payload)
            {
                parent.Log(LogLevel.Info,
                    "SocketBridge: HandleSend called, payload={0} bytes",
                    payload != null ? payload.Length : 0);

                if(payload == null || payload.Length < 8)
                    return EncodeIntegerAndErrno(-1, EINVAL);

                int fd = DecodeInt32(payload, 0);
                uint bufferLength = DecodeUint32(payload, 4);

                Socket sock;
                if(!sockets.TryGetValue(fd, out sock))
                    return EncodeIntegerAndErrno(-1, EBADF);

                int offset = 8;
                byte[] data = null;
                if(bufferLength > 0 && payload.Length >= offset + (int)bufferLength)
                {
                    data = new byte[bufferLength];
                    Array.Copy(payload, offset, data, 0, (int)bufferLength);
                    offset += (int)bufferLength;
                }

                // length(4) + flags(4) follow the buffer
                // int length = (payload.Length >= offset + 4) ? DecodeInt32(payload, offset) : (int)bufferLength;
                // offset += 4;
                int flags = 0;
                if(payload.Length >= offset + 8)
                {
                    // skip length field
                    offset += 4;
                    flags = DecodeInt32(payload, offset);
                    offset += 4;
                }

                // Destination address (for sendto)
                uint destAddrLen = 0;
                IPEndPoint destEp = null;
                if(payload.Length >= offset + 4)
                {
                    destAddrLen = DecodeUint32(payload, offset);
                    offset += 4;
                    if(destAddrLen > 0 && payload.Length >= offset + (int)destAddrLen)
                    {
                        destEp = DecodeSockAddr(payload, offset);
                    }
                }

                if(data == null || data.Length == 0)
                    return EncodeIntegerAndErrno(0, 0);

                try
                {
                    int sent;
                    var socketFlags = (SocketFlags)flags;
                    if(destEp != null)
                    {
                        sent = sock.SendTo(data, socketFlags, destEp);
                    }
                    else
                    {
                        sent = sock.Send(data, socketFlags);
                    }
                    parent.Log(LogLevel.Debug,
                        "SocketBridge: Send fd={0} sent={1}/{2}", fd, sent, data.Length);
                    return EncodeIntegerAndErrno(sent, 0);
                }
                catch(SocketException ex)
                {
                    if(ex.SocketErrorCode == SocketError.WouldBlock)
                        return EncodeIntegerAndErrno(-1, EAGAIN);
                    parent.Log(LogLevel.Warning,
                        "SocketBridge: Send fd={0} failed: {1}", fd, ex.Message);
                    return EncodeIntegerAndErrno(-1, MapSocketError(ex.SocketErrorCode));
                }
            }

            /// <summary>
            /// WiFiRead (func=13): Read data from socket.
            /// Request: socket_handle(4) + count(4)
            /// Response: buffer_length(4) + buffer(N) + result(4) + errno(4)
            /// </summary>
            public byte[] HandleRead(byte[] payload)
            {
                if(payload == null || payload.Length < 8)
                    return EncodeReadResponse(null, -1, EINVAL);

                int fd = DecodeInt32(payload, 0);
                int count = DecodeInt32(payload, 4);

                Socket sock;
                if(!sockets.TryGetValue(fd, out sock))
                    return EncodeReadResponse(null, -1, EBADF);

                if(count <= 0)
                    return EncodeReadResponse(null, 0, 0);

                // Cap at max buffer size
                if(count > MaxReadWriteBuffer)
                    count = MaxReadWriteBuffer;

                try
                {
                    var buf = new byte[count];
                    int received = sock.Receive(buf, 0, count, SocketFlags.None);
                    if(received == 0)
                    {
                        // EOF — peer closed connection
                        parent.Log(LogLevel.Debug, "SocketBridge: Read fd={0} EOF", fd);
                        return EncodeReadResponse(null, 0, 0);
                    }
                    // Trim buffer to actual received size
                    if(received < count)
                    {
                        var trimmed = new byte[received];
                        Array.Copy(buf, 0, trimmed, 0, received);
                        buf = trimmed;
                    }
                    parent.Log(LogLevel.Debug,
                        "SocketBridge: Read fd={0} got {1} bytes", fd, received);
                    return EncodeReadResponse(buf, received, 0);
                }
                catch(SocketException ex)
                {
                    if(ex.SocketErrorCode == SocketError.WouldBlock)
                        return EncodeReadResponse(null, -1, EAGAIN);
                    parent.Log(LogLevel.Warning,
                        "SocketBridge: Read fd={0} failed: {1}", fd, ex.Message);
                    return EncodeReadResponse(null, -1, MapSocketError(ex.SocketErrorCode));
                }
            }

            /// <summary>
            /// WiFiRecv (func=15) / WiFiRecvFrom (func=16): Receive data with optional source address.
            /// Request: socket_handle(4) + length(4) + flags(4) + get_source_address(4)
            /// Response: buffer_length(4) + buffer(N) + result(4) + errno(4)
            ///         + source_addr_length(4) + source_addr(M) + source_addr_len(4)
            /// </summary>
            public byte[] HandleRecvFrom(byte[] payload)
            {
                parent.Log(LogLevel.Info,
                    "SocketBridge: HandleRecvFrom called, payload={0} bytes",
                    payload != null ? payload.Length : 0);
                if(payload == null || payload.Length < 16)
                    return EncodeRecvFromResponse(null, -1, EINVAL, null, false);

                int fd = DecodeInt32(payload, 0);
                int length = DecodeInt32(payload, 4);
                int flags = DecodeInt32(payload, 8);
                int getSourceAddr = DecodeInt32(payload, 12);

                Socket sock;
                if(!sockets.TryGetValue(fd, out sock))
                    return EncodeRecvFromResponse(null, -1, EBADF, null, false);

                if(length <= 0)
                    return EncodeRecvFromResponse(null, 0, 0, null, getSourceAddr != 0);

                if(length > MaxReadWriteBuffer)
                    length = MaxReadWriteBuffer;

                try
                {
                    var buf = new byte[length];
                    EndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);
                    int received;
                    var socketFlags = (SocketFlags)flags;

                    if(getSourceAddr != 0 && sock.SocketType == SocketType.Dgram)
                    {
                        received = sock.ReceiveFrom(buf, 0, length, socketFlags, ref remoteEp);
                    }
                    else
                    {
                        received = sock.Receive(buf, 0, length, socketFlags);
                        remoteEp = null;
                    }

                    if(received == 0 && sock.SocketType == SocketType.Stream)
                    {
                        parent.Log(LogLevel.Debug, "SocketBridge: RecvFrom fd={0} EOF", fd);
                        return EncodeRecvFromResponse(null, 0, 0,
                            remoteEp as IPEndPoint, getSourceAddr != 0);
                    }

                    if(received < length)
                    {
                        var trimmed = new byte[received];
                        Array.Copy(buf, 0, trimmed, 0, received);
                        buf = trimmed;
                    }

                    parent.Log(LogLevel.Debug,
                        "SocketBridge: RecvFrom fd={0} got {1} bytes", fd, received);
                    return EncodeRecvFromResponse(buf, received, 0,
                        remoteEp as IPEndPoint, getSourceAddr != 0);
                }
                catch(SocketException ex)
                {
                    if(ex.SocketErrorCode == SocketError.WouldBlock)
                        return EncodeRecvFromResponse(null, -1, EAGAIN, null, getSourceAddr != 0);
                    parent.Log(LogLevel.Warning,
                        "SocketBridge: RecvFrom fd={0} failed: {1}", fd, ex.Message);
                    return EncodeRecvFromResponse(null, -1,
                        MapSocketError(ex.SocketErrorCode), null, getSourceAddr != 0);
                }
            }

            /// <summary>
            /// WiFiPoll (func=17): Check socket readability/writability.
            /// Request: socket_handle(4) + events(2) + timeout(4) + setup(4) + setup_message_id(4)
            /// Response: returned_events(2) + result(4) + errno(4)
            /// </summary>
            /// <summary>
            /// WiFiPoll (func=17): Poll socket for readability/writability.
            /// Request: socket_handle(4) + events(2) + timeout(4) + setup(4) + setup_message_id(4)
            /// Response: returned_events(2) + result(4) + errno(4)
            ///
            /// Firmware uses two-phase poll:
            ///   setup=1: Register interest — store in pollRegistrations, return ACK.
            ///            The ESP32 will send an interrupt_poll_response event (func=18)
            ///            when the socket becomes ready.
            ///   setup=0: Teardown — remove registration, do point-in-time check.
            /// </summary>
            public byte[] HandlePoll(byte[] payload)
            {
                if(payload == null || payload.Length < 6)
                    return EncodePollResponse(0, -1, EINVAL);

                int fd = DecodeInt32(payload, 0);
                ushort events = DecodeUint16(payload, 4);
                int setup = (payload.Length >= 10) ? DecodeInt32(payload, 6) : 0;
                uint setupMsgId = (payload.Length >= 14) ? DecodeUint32(payload, 10) : 0;

                Socket sock;
                if(!sockets.TryGetValue(fd, out sock))
                    return EncodePollResponse(POLLNVAL, -1, EBADF);

                if(setup == 1)
                {
                    // Register poll interest — async notification via interrupt_poll_response
                    pollRegistrations[setupMsgId] = new PollRegistration
                    {
                        Fd = fd,
                        Events = events,
                        SetupMessageId = setupMsgId
                    };
                    parent.Log(LogLevel.Debug,
                        "SocketBridge: Poll setup fd={0} events=0x{1:X4} msgId=0x{2:X8}",
                        fd, events, setupMsgId);
                    // Return success (ACK) — async events will follow
                    return EncodeIntegerAndErrno(0, 0);
                }

                // Teardown (setup=0) or direct query — remove registration and do point-in-time check
                if(setupMsgId != 0)
                    pollRegistrations.Remove(setupMsgId);

                ushort revents = 0;
                try
                {
                    if((events & POLLIN) != 0 && sock.Poll(0, SelectMode.SelectRead))
                        revents |= POLLIN;
                    if((events & POLLOUT) != 0 && sock.Poll(0, SelectMode.SelectWrite))
                        revents |= POLLOUT;
                    if(sock.Poll(0, SelectMode.SelectError))
                        revents |= POLLERR;
                }
                catch(SocketException ex)
                {
                    parent.Log(LogLevel.Debug,
                        "SocketBridge: Poll fd={0} error: {1}", fd, ex.Message);
                    return EncodePollResponse(POLLERR, -1, MapSocketError(ex.SocketErrorCode));
                }
                catch(ObjectDisposedException)
                {
                    return EncodePollResponse(POLLNVAL, -1, EBADF);
                }

                parent.Log(LogLevel.Debug,
                    "SocketBridge: Poll fd={0} events=0x{1:X4} revents=0x{2:X4}",
                    fd, events, revents);
                return EncodePollResponse(revents, 0, 0);
            }

            /// <summary>
            /// Check all registered poll interests and queue interrupt_poll_response
            /// events for sockets that have matching events ready. Called during
            /// ESPCP transport/send_response polling.
            ///
            /// interrupt_poll_response wire format (18 bytes):
            /// socket_handle(4) + result(4) + errno(4) + returned_events(2) + setup_message_id(4)
            /// </summary>
            public void CheckPollRegistrations()
            {
                if(pollRegistrations.Count == 0)
                    return;

                // Collect completed registrations to avoid modifying dict during iteration
                var completed = new List<uint>();

                foreach(var kvp in pollRegistrations)
                {
                    var reg = kvp.Value;
                    Socket sock;
                    if(!sockets.TryGetValue(reg.Fd, out sock))
                    {
                        // Socket closed — fire with POLLNVAL
                        var payload = EncodeInterruptPollResponse(reg.Fd, -1, EBADF, POLLNVAL, reg.SetupMessageId);
                        parent.QueueEvent(InterfaceWiFi, WiFiInterruptPollResponse, StatusOk, payload);
                        completed.Add(kvp.Key);
                        continue;
                    }

                    ushort revents = 0;
                    try
                    {
                        if((reg.Events & POLLIN) != 0 && sock.Poll(0, SelectMode.SelectRead))
                            revents |= POLLIN;
                        if((reg.Events & POLLOUT) != 0 && sock.Poll(0, SelectMode.SelectWrite))
                            revents |= POLLOUT;
                        if(sock.Poll(0, SelectMode.SelectError))
                            revents |= POLLERR;
                    }
                    catch(SocketException)
                    {
                        revents = POLLERR;
                    }
                    catch(ObjectDisposedException)
                    {
                        revents = POLLNVAL;
                    }

                    if(revents != 0)
                    {
                        var payload = EncodeInterruptPollResponse(reg.Fd, 0, 0, revents, reg.SetupMessageId);
                        parent.QueueEvent(InterfaceWiFi, WiFiInterruptPollResponse, StatusOk, payload);
                        completed.Add(kvp.Key);
                        parent.Log(LogLevel.Debug,
                            "SocketBridge: Poll notify fd={0} revents=0x{1:X4} msgId=0x{2:X8}",
                            reg.Fd, revents, reg.SetupMessageId);
                    }
                }

                foreach(var key in completed)
                    pollRegistrations.Remove(key);
            }

            /// <summary>
            /// Encode interrupt_poll_response payload (18 bytes):
            /// socket_handle(4) + result(4) + errno(4) + returned_events(2) + setup_message_id(4)
            /// </summary>
            private static byte[] EncodeInterruptPollResponse(int socketHandle, int result,
                int errno, ushort returnedEvents, uint setupMessageId)
            {
                var buf = new byte[18];
                int offset = 0;
                // socket_handle
                buf[offset++] = (byte)(socketHandle & 0xFF);
                buf[offset++] = (byte)((socketHandle >> 8) & 0xFF);
                buf[offset++] = (byte)((socketHandle >> 16) & 0xFF);
                buf[offset++] = (byte)((socketHandle >> 24) & 0xFF);
                // result
                buf[offset++] = (byte)(result & 0xFF);
                buf[offset++] = (byte)((result >> 8) & 0xFF);
                buf[offset++] = (byte)((result >> 16) & 0xFF);
                buf[offset++] = (byte)((result >> 24) & 0xFF);
                // errno
                buf[offset++] = (byte)(errno & 0xFF);
                buf[offset++] = (byte)((errno >> 8) & 0xFF);
                buf[offset++] = (byte)((errno >> 16) & 0xFF);
                buf[offset++] = (byte)((errno >> 24) & 0xFF);
                // returned_events
                buf[offset++] = (byte)(returnedEvents & 0xFF);
                buf[offset++] = (byte)((returnedEvents >> 8) & 0xFF);
                // setup_message_id
                buf[offset++] = (byte)(setupMessageId & 0xFF);
                buf[offset++] = (byte)((setupMessageId >> 8) & 0xFF);
                buf[offset++] = (byte)((setupMessageId >> 16) & 0xFF);
                buf[offset++] = (byte)((setupMessageId >> 24) & 0xFF);
                return buf;
            }

            /// <summary>
            /// WiFiBind (func=20): Bind socket to address.
            /// Request: socket_handle(4) + addr_length(4) + addr(N)
            /// Response: integer_and_errno
            /// </summary>
            public byte[] HandleBind(byte[] payload)
            {
                if(payload == null || payload.Length < 8)
                    return EncodeIntegerAndErrno(-1, EINVAL);

                int fd = DecodeInt32(payload, 0);
                uint addrLen = DecodeUint32(payload, 4);

                Socket sock;
                if(!sockets.TryGetValue(fd, out sock))
                    return EncodeIntegerAndErrno(-1, EBADF);

                if(addrLen == 0 || payload.Length < 8 + (int)addrLen)
                    return EncodeIntegerAndErrno(-1, EINVAL);

                var ep = DecodeSockAddr(payload, 8);
                parent.Log(LogLevel.Info, "SocketBridge: Bind fd={0} to {1}", fd, ep);

                try
                {
                    sock.Bind(ep);
                    return EncodeIntegerAndErrno(0, 0);
                }
                catch(SocketException ex)
                {
                    parent.Log(LogLevel.Warning,
                        "SocketBridge: Bind fd={0} failed: {1}", fd, ex.Message);
                    return EncodeIntegerAndErrno(-1, MapSocketError(ex.SocketErrorCode));
                }
            }

            /// <summary>
            /// WiFiListen (func=21): Start listening.
            /// Request: socket_handle(4) + back_log(4)
            /// Response: integer_and_errno
            /// </summary>
            public byte[] HandleListen(byte[] payload)
            {
                if(payload == null || payload.Length < 8)
                    return EncodeIntegerAndErrno(-1, EINVAL);

                int fd = DecodeInt32(payload, 0);
                int backlog = DecodeInt32(payload, 4);

                Socket sock;
                if(!sockets.TryGetValue(fd, out sock))
                    return EncodeIntegerAndErrno(-1, EBADF);

                parent.Log(LogLevel.Info,
                    "SocketBridge: Listen fd={0} backlog={1}", fd, backlog);

                try
                {
                    sock.Listen(backlog);
                    return EncodeIntegerAndErrno(0, 0);
                }
                catch(SocketException ex)
                {
                    parent.Log(LogLevel.Warning,
                        "SocketBridge: Listen fd={0} failed: {1}", fd, ex.Message);
                    return EncodeIntegerAndErrno(-1, MapSocketError(ex.SocketErrorCode));
                }
            }

            /// <summary>
            /// WiFiAccept (func=22): Accept incoming connection (non-blocking).
            /// Request: socket_handle(4) + addr_length(4) + addr(N)
            /// Response: addr_length(4) + addr(N) + result(4) + errno(4)
            /// </summary>
            public byte[] HandleAccept(byte[] payload)
            {
                if(payload == null || payload.Length < 4)
                    return EncodeAcceptResponse(null, -1, EINVAL);

                int fd = DecodeInt32(payload, 0);

                Socket sock;
                if(!sockets.TryGetValue(fd, out sock))
                    return EncodeAcceptResponse(null, -1, EBADF);

                try
                {
                    var newSock = sock.Accept();
                    newSock.Blocking = false;
                    int newFd = nextFd++;
                    sockets[newFd] = newSock;
                    var remoteEp = newSock.RemoteEndPoint as IPEndPoint;
                    parent.Log(LogLevel.Info,
                        "SocketBridge: Accept fd={0} new_fd={1} from {2}",
                        fd, newFd, remoteEp);
                    return EncodeAcceptResponse(remoteEp, newFd, 0);
                }
                catch(SocketException ex)
                {
                    if(ex.SocketErrorCode == SocketError.WouldBlock)
                        return EncodeAcceptResponse(null, -1, EAGAIN);
                    parent.Log(LogLevel.Warning,
                        "SocketBridge: Accept fd={0} failed: {1}", fd, ex.Message);
                    return EncodeAcceptResponse(null, -1, MapSocketError(ex.SocketErrorCode));
                }
            }

            /// <summary>
            /// WiFiSetSockOpt (func=11): Stub — accept and ignore.
            /// </summary>
            public byte[] HandleSetSockOpt(byte[] payload)
            {
                if(payload != null && payload.Length >= 4)
                {
                    int fd = DecodeInt32(payload, 0);
                    parent.Log(LogLevel.Debug,
                        "SocketBridge: SetSockOpt fd={0} (stub, accepted)", fd);
                }
                return EncodeIntegerAndErrno(0, 0);
            }

            /// <summary>
            /// WiFiGetSockOpt (func=12): Stub — return success with empty data.
            /// </summary>
            public byte[] HandleGetSockOpt(byte[] payload)
            {
                if(payload != null && payload.Length >= 4)
                {
                    int fd = DecodeInt32(payload, 0);
                    parent.Log(LogLevel.Debug,
                        "SocketBridge: GetSockOpt fd={0} (stub)", fd);
                }
                return EncodeIntegerAndErrno(0, 0);
            }

            /// <summary>
            /// WiFiIoctl (func=39): Stub — accept and ignore.
            /// </summary>
            public byte[] HandleIoctl(byte[] payload)
            {
                parent.Log(LogLevel.Debug, "SocketBridge: Ioctl (stub, accepted)");
                return EncodeIntegerAndErrno(0, 0);
            }
        }

        // ── State ───────────────────────────────────────────────────────

        private readonly byte[] rxBuffer = new byte[MaxSpiFrameSize];
        private readonly byte[] txBuffer = new byte[MaxSpiFrameSize];
        private int rxIndex;
        private int txIndex;
        private int txLength;
        private bool hasResponse;
        private bool booted;
        private byte[] lastResponseBuffer;
        private int lastResponseLength;
        private byte[] displacedResponse;
        private int displacedResponseLength;
        private byte[] displacedLastResponse;
        private int displacedLastResponseLength;


        private long frameCount;
        private uint nextMessageId = 1;
        private SocketBridge socketBridge;
        private readonly Queue<byte[]> pendingEvents = new Queue<byte[]>();
    }
}
