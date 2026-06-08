# Meadow.OS Emulator Reference

## Overview

The Meadow.OS Emulator is a full hardware emulation system that runs the Meadow operating system using [Renode](https://renode.io). It supports two primary modes: single-machine mode with a stubbed ESP32 coprocessor (EspcpStub) and dual-machine mode with a real ESP32 firmware running alongside the STM32. The emulator enables complete Meadow application development and deployment through Meadow.CLI over TCP socket connections, supporting device information retrieval, app deployment, and standard HCOM commands.

The system is built on top of the legacy NuttX-based Meadow OS firmware or the newer Meadow.OS rebase, with support for both legacy Mono 6.9 runtime and .NET 10 Mono runtime. The emulator provides a complete development environment for Meadow applications without requiring physical hardware.

## Architecture

The emulator architecture consists of two main components running simultaneously in Renode:

1. **STM32F777 (NuttX) side** - Runs the Meadow OS firmware with NuttX kernel
2. **ESP32 side** - Runs the real Meadow firmware in emulated Xtensa environment

Key architectural components:
- **SpiBridge.cs** - Cross-machine SPI frame queues for communication between STM32 and ESP32
- **ESP32_SPI.cs** - ESP32 SPI3 peripheral with register map, DMA descriptor chains, and IRQ generation
- **ESP32_DPORT.cs** - DPORT interrupt matrix with 69 interrupt sources and level-OR routing
- **EspcpStub.cs** - Stub implementation for single-machine mode
- **MqReceiveIntercept.cs** - ESPCP message queue interception for polling

Communication flows through SPI bridges with HCOM (Host Communication) over UART4 in the STM32 side, and TCP socket for Meadow.CLI connections.

## Key Data Structures

### ESPCP SPI Wire Format (27-byte header + payload + 4-byte overhead)
```
Offset  Size  Field           Notes
0x00    1     protocol        Always 0x01
0x01    4     crc32           CRC-32 over entire frame (excl last 4 bytes), zeroed during calc
0x05    2     packet_offset   LE, offset within multi-packet payload assembly
0x07    2     packet_length   LE, bytes of payload in THIS packet
0x09    1     message_type    0x00=ACK, 0x01=NAK, 0x04=Event, 0x10=Response, 0x20=Transport, 0x40=Header(cmd)
0x0A    1     interface       0=None, 1=WiFi, 2=BT, 3=Mesh, 4=System, 5=Transport
0x0B    4     function        LE, interface-specific function code
0x0F    4     status_code     LE, 0=OK
0x13    4     message_id      LE, STM32→ESP32: 0x80000000+, ESP32→STM32: 0x00000001+
0x17    4     payload_length  LE, TOTAL payload size across all packets
0x1B    N     payload         payload data (N = packet_length)
N+0x1B  4     padding/overhead
```
Total frame size: 4-byte aligned, min 8 bytes, max 4092 bytes (MaxSpiFrameSize).

### Extracted Message Struct (firmware in-memory, from espcp_extract_message)
```
Offset  Size  Field
+0x00   1     message_type
+0x01   1     interface
+0x04   4     function
+0x08   4     status_code
+0x0C   4     message_id
+0x10   2     packet_offset
+0x12   2     packet_length
+0x14   4     payload_pointer (into SPI rx buffer)
+0x18   4     payload_length (total, not just this packet)
```

### espcp_configuration (global at 0x20020F6C, current build)
```
+0x00   4     semaphore address (0x200202CC)
+0x04   4     initialized flag (1)
+0x08   4     espcp_state pointer → allocated by espcp_early_init
+0x0C   4     flags
+0x10   4     pointer
+0x14   4     pointer
```

### espcp_state (heap-allocated, pointed to by config+0x08)
```
+0x00   1     (overwritten to 0 by espcp_get_message)
+0x01   1     interface
+0x03   1     thread_running (set to 1 when espcp_thread starts)
+0x04   1     event_handler_running (set to 1 when event handler starts)
+0x14   4     espcp_thread mq_fd (message queue for commands + send_response)
+0x1C   4     event_handler_thread mq_fd (message queue for dispatched events)
+0x30   1     esp_not_responding (1=true, blocks all managed ESP32 commands)
+0x34   4     espcp_send_data_over_spi function pointer (0x081480CD with Thumb)
+0x38   4     SPI buffer size (0x20 = 32 bytes)
+0x3C   4     SPI buffer pointer
+0x44   4     SPI receive buffer (state+0x44)
+0x48   4     SPI encode buffer (state+0x48)
```

### NuttX mqueue structures (for MqReceiveIntercept)
```
mq_des:
  +0x00  4  flink
  +0x04  4  msgq → mqueue_inode_s

mqueue_inode_s:
  +0x00  4  inode
  +0x04  4  msglist.head
  +0x08  4  msglist.tail
  +0x0C  2  maxmsgs (int16)
  +0x0E  2  nmsgs (int16)
  +0x10  2  nwaitnotfull (int16)
  +0x12  2  nwaitnotempty (int16)
  +0x14  4  freelist.head
  +0x18  4  freelist.tail

mqueue_msg_s:
  +0x00  4  next
  +0x04  1  type
  +0x05  1  priority
  +0x06  1  msglen
  +0x07  N  mail[] (message data, typically 4 bytes = a pointer)
```

### Configuration Structure
```yaml
os: legacy
board: F7FeatherV2
coprocessor: stub
build:
  defconfig: mono
  runtime: legacy-os_modern-dotnet
ports:
  hcom: 4242
  monitor: 1234
  esp32ctrl: 4243
  gdb: 0
debug:
  console_log: /tmp/renode-uart-legacy.txt
  headless: true
```

## APIs & Protocols

### HCOM Protocol
- **Transport**: TCP socket (default port 4242)
- **Encoding**: COBS (Consistent Overhead Byte Stuffing) framed messages
- **Message Format**: 
  - Header: 1 byte type + 1 byte length + 2 bytes message_id
  - Payload: variable length data
- **Commands**: Device info, file transfer, app deployment, system control

### ESPCP Protocol — SPI Exchange Sequences (Verified from Disassembly)

**Key principle:** Each call to `espcp_send_data_over_spi(tx, rx, len)` maps to one
SPI bypass hook invocation = one Transmit×N + FinishTransmission cycle.
- tx≠0, rx=0: TX-only (firmware sends command/poll, ignores MISO)
- tx=0, rx≠0: RX-only (firmware reads MISO, sends zeros on MOSI)

**Critical timing:** FinishTransmission processes the incoming frame AFTER Transmit
already ran. A response prepared in FinishTransmission is only available for the
NEXT exchange's Transmit calls. This means responses are always "one exchange behind."

#### 1. Command Send (espcp_send_message, e.g. WiFi Connect)
```
Exchange A: TX-only — firmware sends command frame (type=0x40 Header)
  → EspcpStub.FinishTransmission: decodes, HandleWiFi, PrepareResponse → hasResponse=true
Exchange B: RX-only — firmware reads response (type=0x10 Response)
  → Transmit returns response bytes (hasResponse was set by Exchange A)
  → FinishTransmission: wasRxOnly → consumes hasResponse
```
Result: firmware gets the command response. espcp_send_message checks
response.interface == command.interface and response.message_id == command.message_id.

#### 2. espcp_get_message (poll cycle — reads events/responses from ESP32)
Called when espcp_thread gets a send_response message from MqReceiveIntercept.
```
Exchange C: TX-only — espcp_send_packet sends transport/send_response poll
  → FinishTransmission: HandleTransport → PrepareRawResponse(event) or PrepareAck
Exchange D: RX-only — firmware reads the response/event
  → Transmit returns event/ACK bytes
  → FinishTransmission: consumes
```
Then firmware processes the extracted message:

#### 3. Multi-packet payload assembly (inside espcp_get_message)
After the first extraction, if payload_length > 0:
- `espcp_create_copy_of_message_on_heap(msg, r7=0)` → creates header-only copy (r7=0 skips payload)
- `zalloc(payload_length)` → empty payload buffer
- Enters multi-packet loop: does ANOTHER rx-only exchange to fill the payload
```
Exchange E: RX-only — firmware reads "continuation" data
  → Without re-serve: Transmit returns zeros → extraction fails → NAK retry
  → With re-serve: Transmit returns saved response → extraction succeeds
  → Firmware copies payload via memcpy(payload_buf + packet_offset, data, packet_length)
  → r6 -= packet_length → if r6 == 0, exits loop
```
**CRITICAL:** The first rx-only in espcp_get_message reads ONLY 32 bytes (header-only
buffer). The payload is NOT included. The multi-packet loop then reads the payload
in separate rx-only exchanges. The stub currently sends the full frame (header+payload)
in one response, but the firmware only reads 32 bytes of it. The payload bytes are lost.

For proper multi-packet support, the stub must:
1. First rx-only (32 bytes): return header with payload_length set correctly
2. Multi-packet loop rx-only (sized per chunk): return payload data

The firmware sends an ACK request frame between reads with packet_offset/packet_length
indicating which chunk it wants. But with tx=0 (rx-only), the request is in the
pre-loaded SPI encode buffer which gets cleared by espcp_clear_spi_buffers.

Source: espcp_message_dispatcher.c lines 768-901 in Meadow firmware repo.

#### 4. After multi-packet assembly completes (r6 == 0):
```
fp = extracted_message[0]  (message type from the LAST extracted frame, at address r7)
if fp == 0x04 (Event):
    espcp_add_message_to_queue(config[0x1C], message)  → posts to event handler queue
else:
    Looks up message in g_request_response_msg waiting list by message_id
    Signals the waiting semaphore if found
```

#### 5. ACK-back (after payload assembly):
The firmware sends an ACK/NAK back to the ESP32 for the received message.
```
Exchange F: TX-only — firmware sends ACK (type=0x00 from strb r7, [r4, #0])
  → EspcpStub: decoded as ACK → ignored (no new response prepared)
Exchange G: RX-only — firmware reads "acknowledgment of ACK"
  → Without anything prepared: zeros → extraction fails → retry
  → With ACK prepared: valid ACK → extracted → payload_length=0 → loop exits cleanly
```
Note: r7=0 (espcp_send_packet return) is stored as heap_copy[0], overwriting the type.

#### 6. Event handler dispatch
Events posted to config[0x1C] queue are read by espcp_event_handler_thread:
```
espcp_event_handler_thread:
  loop:
    mq_receive(config[0x1C], &msg_ptr, 4, 0)  → blocks until event arrives
    espcp_dispatch_event(msg_ptr)               → dispatches to managed layer
```

### Key Addresses (current firmware build)
| Symbol | Address | Notes |
|--------|---------|-------|
| espcp_configuration | 0x20020F6C | Global BSS |
| espcp_early_init | 0x08148314 | |
| espcp_late_init | 0x081483B8 | Starts threads |
| espcp_thread | 0x0814E010 | Main SPI poll loop |
| espcp_thread_start | 0x0814E09E | Creates the thread |
| espcp_get_message | 0x08147CF0 | Reads from ESP32 via SPI |
| espcp_send_message | 0x08147BD4 | Sends to ESP32 via SPI |
| espcp_send_packet | 0x08147A6A | TX-only SPI exchange |
| espcp_extract_message | 0x08146B3E | Parses SPI frame → struct |
| espcp_create_copy_of_message_on_heap | 0x081479E2 | r1=0: no payload, r1≠0: with payload |
| espcp_add_message_to_queue | 0x0814E1D4 | Posts to mqueue via mq_send |
| espcp_event_handler_thread | 0x08149C30 | Reads events, calls dispatch |
| espcp_dispatch_event | 0x08149BE8 | Routes to managed callbacks |
| espcp_queue_message | 0x081484E4 | Managed→ESPCP command path |
| espcp_send_data_over_spi | 0x081480CC | SPI DMA (bypassed by hook) |
| espcp_clear_spi_buffers | 0x08147A9C | memset tx+rx bufs to 0 |
| hcom_nx_setup_mgr | 0x08141B54 | Boot init sequence |
| upd_handle_esp32_command | 0x08140CD0 | HCOM→ESP32 command handler |
| g_request_response_msg | 0x20020F54 | Waiting-for-response list |
| poll flag | 0xC02FFC00 | SDRAM gap, enables MqReceiveIntercept polling |
| mono_ready flag | 0xC02FFC04 | SDRAM gap, set by start_mono_main hook |
| mq_receive | 0x08157318 | NuttX kernel, hooked by MqReceiveIntercept |

### SPI Bypass Behavior
`EspcpSpiBypass.cs` intercepts `espcp_send_data_over_spi` at 0x081480CC.
- R0=tx buffer (0=rx-only), R1=rx buffer (0=tx-only), R2=length
- Creates txdata from tx buffer (or zeros if tx=0)
- Calls bridge.Transmit(txdata[i]) for each byte, stores MISO in rxarr
- Calls bridge.FinishTransmission()
- Writes rxarr to rx buffer (if rx≠0)
- Sets PC=LR (return)

**Full-duplex note:** On real hardware, tx=0 means "use the pre-loaded SPI DMA buffer"
(loaded by espcp_encode_message). The bypass sends zeros instead. This is correct for
the normal poll path (espcp_send_packet handles the tx-only separately). For the
multi-packet loop, the firmware calls espcp_clear_spi_buffers AFTER send_packet,
zeroing the encode buffer before the rx-only call — so reading the encode buffer
for "full-duplex" doesn't work.

### SPI Bridge Communication (Dual-Machine Mode)
- **STM32 side**: SpiBridge.cs handles frame queuing and SPI communication
- **ESP32 side**: ESP32_SPI.cs injects frames into firmware DMA buffers
- **Interrupt Handling**: DPORT interrupt matrix routes SPI interrupts to ESP32 firmware

## Conventions

### Naming Patterns
- Peripheral files: `peripherals/{Name}.cs`
- Platform definitions: `platforms/{name}.repl`
- Configuration files: `configs/{name}.yaml`
- Build outputs: `build/{variant}/`

### Constants and Magic Numbers
- **HCOM TCP Port**: 4242 (hardcoded)
- **Monitor Telnet Port**: 1234 (hardcoded)
- **ESP32 UART Control Port**: 4243 (hardcoded)
- **ESP32 ROM Address**: 0x40000000 (PRO CPU ROM)
- **ESP32 ROM Data Address**: 0x3FF90000 (data segment)
- **Message ID Range**: 0x0001-0x7FFF (valid request IDs)
- **SPI Frame Size**: 256 bytes maximum
- **LittleFS Block Size**: 4096 bytes (default)

### Hook Addresses (Current Build)
- `espcp_queue_message`: 0x081484E4
- `net_lockedwait` call site: 0x0814851C
- `espcp_queue_message` success return: 0x08148512
- `espcp_thread` SPI dispatch: 0x0814E048
- `g_request_response_message`: 0x20020F54
- `upd_handle_esp32_command`: 0x08140CD0
- `esp_not_responding` check: 0x08140CEA

### Key File Locations
- `build/legacy/` - Legacy OS firmware output
- `build/dotnet10/` - .NET 10 firmware output
- `roms/esp32-v3-rom.bin` - ESP32 ROM binary
- `peripherals/SpiBridge.cs` - SPI bridge implementation
- `peripherals/ESP32_SPI.cs` - ESP32 SPI peripheral
- `peripherals/ESP32_DPORT.cs` - DPORT interrupt matrix

## File Layout

### Core Project Structure
```
Meadow.OS.Emulator/
├── build/legacy/             # Legacy OS firmware output
├── build/dotnet10/           # .NET 10 firmware output
├── build-meadow.os-emulated.sh  # Firmware build script
├── run.sh                    # Emulator launcher
├── platforms/                # Renode platform definitions
│   ├── stm32f777.repl        # STM32F777 chip-level platform
│   ├── meadow-f7.repl        # Meadow F7 board platform
│   ├── meadow-f7-dual.repl   # Dual-machine variant
│   └── meadow-esp32.repl     # ESP32 platform
├── peripherals/              # C# peripheral implementations
│   ├── SpiBridge.cs          # Cross-machine SPI frame queues
│   ├── ESP32_SPI.cs          # ESP32 SPI3 peripheral
│   ├── ESP32_DPORT.cs        # DPORT interrupt matrix
│   ├── ESP32_RTC_CNTL.cs     # RTC control
│   ├── ESP32_TimerGroup.cs   # Timer group
│   ├── Winbond_W25QXXXJV.cs  # QSPI flash
│   ├── STM32F7_RCC_Custom.cs # RCC with auto-clock ready bits
│   ├── QspiDmaBypass.cs      # QSPI DMA bypass
│   ├── EspcpSpiBypass.cs     # ESPCP SPI DMA bypass
│   ├── MqReceiveIntercept.cs # ESPCP mq_receive interception
│   └── UartRxBypass.cs       # UART RX bypass for file transfer
├── scripts/                  # Renode scripts
│   ├── meadow-dual.resc      # Dual-machine mode
│   ├── meadow-legacy-headless.resc # Legacy headless mode
│   └── ...                   # Other mode scripts
├── roms/                     # ESP32 ROM binaries
├── configs/                  # Configuration files
│   ├── legacy.yaml           # Legacy OS config
│   ├── meadow-os.yaml        # Meadow.OS config
│   └── legacy-os_modern-dotnet.yaml # .NET 10 config
├── tools/                    # Build helpers and utilities
└── upstream_fixes.md         # Renode fork fixes tracker
```

### Build Process Flow
1. `build-meadow.os-emulated.sh` reads `emulator.config.yaml`
2. Selects source tree (`Meadow/` for legacy, `Meadow.OS/` for rebase)
3. Applies emulator-specific kconfig tweaks
4. Extracts hook addresses for Renode
5. Builds firmware to `build/{variant}/` directory

### Configuration Precedence
1. CLI arguments override config file settings
2. Config file settings override default values
3. Available CLI flags: `--legacy`, `--esp32`, `--port=N`

### Memory Layout
- **STM32F777**: 
  - SDRAM: 0xC0000000 (32MB)
  - QSPI Flash: 0x90000000 (64MB)
  - UART4: Socket connection for HCOM
- **ESP32**: 
  - PRO CPU ROM: 0x40000000 (445KB)
  - Data ROM: 0x3FF90000 (64KB)
  - Memory regions: Various Xtensa-specific memory areas

### Debugging and Logging
- **Console logging**: Configurable via `console_log` in config
- **GDB support**: Enabled via `--gdb` flag with `meadow-legacy-debug.resc`
- **Headless mode**: Enabled via `headless: true` in config
- **UART bypass**: File transfer optimization (currently disabled due to Renode API changes)