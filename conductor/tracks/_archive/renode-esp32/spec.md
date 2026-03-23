# Specification: ESP32 Coprocessor Emulation

## Track ID
`renode-esp32`

## Problem Statement
The Meadow F7 board has two chips: STM32F777 (main MCU running NuttX + Meadow.OS) and ESP32 (coprocessor providing WiFi, BLE, mesh networking, NTP, file system services). Without ESP32 emulation, networking and wireless features cannot be tested in the emulator.

## Goal
Emulate the ESP32 coprocessor so Meadow.OS has functional networking (WiFi/BLE) in Renode:
1. **Phase A:** C# ESPCP stub peripheral — responds to STM32 ESPCP messages with mock data
2. **Phase B:** Dual-machine emulation — run real ESP32 firmware alongside STM32

## Inter-Chip Communication Architecture

### Physical Connections (on-board ESP32)
- **SPI2** @ 0x40003800 — primary data bus
- **CS:** PI0 (active-low chip select)
- **Ready:** PI10 (ESP32 → STM32, goes high when ESP32 is ready to receive)
- **Reset:** PF7 (STM32 → ESP32, open-drain output)
- **Boot:** PI10 (shared with Ready; STM32 → ESP32, open-drain)
- **UART5** — secondary channel for ESP32 debug/monitor output

### ESPCP Protocol (v1)
- **Header:** 27 bytes — protocol version (1B), CRC8 (1B), message_type (1B), interface (1B), function (4B), status_code (4B), message_id (4B), packet_offset (2B), packet_length (2B), payload_length (4B), CRC32 (4B)
- **Max SPI frame:** 4092 bytes (ESP32 silicon bug limits this)
- **SPI overhead:** 4 bytes padding (last 4 bytes discarded — ESP32 silicon bug workaround)
- **Max packet payload:** 4061 bytes (4092 - 27 header - 4 overhead)
- **Max message payload:** 8192 bytes (larger payloads split across multiple packets)
- **Message direction:** ID < 0x80000000 = ESP32→STM32; ID >= 0x80000000 = STM32→ESP32

### Message Types
- `0x00` ACK, `0x01` NAK, `0x02` Reset, `0x04` Event
- `0x10` Response, `0x20` Transport, `0x40` Header, `0x80` Data

### Interfaces
- `0` None, `1` WiFi, `2` Bluetooth, `3` Mesh, `4` System, `5` Transport, `6` Wired Ethernet

### Key Functions per Interface
- **System (4):** get_configuration, set_configuration_item, deep_sleep, get_battery_charge_level, file_system_*, logging_*, core_dump_*
- **WiFi (1):** start/stop interface, connect/disconnect AP, scan, set_antenna, full socket API (socket, connect, bind, listen, accept, send, recv, poll, close, getaddrinfo, setsockopt, ioctl)
- **Bluetooth (2):** start, stop, get_handles, server_data_set, client_write_request_event
- **Transport (5):** response_ready, send_response, kill_nuttx_thread, reset_esp32

### STM32 Threading Model
- ESPCP main thread — processes outgoing message queue
- Event handler thread — processes incoming events from ESP32
- UART monitor thread — captures ESP32 debug output via UART5

### Key Source Files
- `Meadow.OS/espcp/espcp_coprocessor.c` — SPI communication, startup, message send/receive
- `Meadow.OS/espcp/espcp_message.h` — header structure, constants
- `Meadow.OS/espcp/espcp_shared_enums.h` — all interface/function/status enums
- `Meadow.OS/espcp/espcp_thread.c` — thread management
- `Meadow.OS/espcp/doc/coprocessor.md` — architecture documentation

### Build Flag
`CONFIG_MEADOW_ESPCP_MANAGER` (currently `not set` in defconfig — safe for other tracks)

## Success Criteria
- [ ] ESPCP boot handshake completes (reset → ready → first message exchange)
- [ ] System configuration exchange works
- [ ] WiFi start → connect → socket operations functional
- [ ] BLE start/stop stubs respond correctly
- [ ] No timeouts or deadlocks in ESPCP message queues

## Decision Matrix: Stub vs. Dual-Machine

| Capability | Phase A (Stub) | Phase B (Dual-Machine) |
|-----------|----------------|------------------------|
| ESPCP boot handshake | Emulated in C# | Real ESP32 firmware |
| WiFi connect/scan | Mock responses | Real (if ESP32 WiFi modeled) |
| Socket API | Mock FDs, no real network | Real ESP32 stack |
| BLE | Stub start/stop | Real (if BLE modeled) |
| Test STM32 ESPCP code | Full coverage | Full coverage |
| Test ESP32 firmware | Not running | Real firmware |
| Setup complexity | Low-Medium | High |
| Real network access | Possible via host bridge | Possible via Renode TAP |
| Recommended for | Development, CI testing | Integration testing, firmware validation |
