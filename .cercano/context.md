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

### ESPCP Frame Format
```
ESP32 frame structure (aligned to 4 bytes):
0x00: uint8_t type (0x01 = request, 0x20 = response)
0x01: uint8_t reserved
0x02: uint16_t message_id (host byte order)
0x04: uint32_t payload_len (host byte order)
0x08: uint8_t payload[payload_len]
```

### Message Queue Structures
```c
struct espcp_message {
    uint32_t message_id;
    uint8_t type;
    uint8_t *payload;
    uint32_t payload_len;
    uint32_t timeout_ms;
    uint8_t *response_buffer;
    uint32_t response_len;
    int response_status;
    struct espcp_message *next;
};
```

### ESP32 Frame Buffer
```
ESP32_SPI peripheral frame buffer layout:
0x0000: uint8_t frame_buffer[256] (SPI data)
0x0100: uint32_t frame_len
0x0104: uint32_t frame_offset
0x0108: uint32_t status_flags
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

### ESPCP Protocol
- **Message Types**: 
  - Type 0x01: Request frames
  - Type 0x20: Response frames
- **Frame Structure**:
  - Type (1 byte)
  - Reserved (1 byte)
  - Message ID (2 bytes, big-endian)
  - Payload length (4 bytes, big-endian)
  - Payload (variable)

### SPI Bridge Communication
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