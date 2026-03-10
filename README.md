# Meadow.OS Emulator

Full hardware emulation of Meadow.OS (NuttX RTOS on STM32F777ZIT6) using the [Renode](https://renode.io) framework.

## Prerequisites

- **Renode v1.16.1+** — Install from [renode.io/download](https://renode.io/download)
  - macOS: Install the `.dmg`, which places Renode in `/Applications/Renode.app`
  - Linux: Renode binary on `PATH`
- **Mono** (macOS only) — Renode on macOS runs via Mono runtime
- **ARM toolchain** — `arm-none-eabi-gcc` (install via `brew install --cask gcc-arm-embedded`)
- **GNU Make 4.x+** — macOS ships Make 3.81; install `gmake` via `brew install make`
- **Meadow.OS source** — Must be checked out as a sibling directory (`../Meadow.OS/`)
- **Meadow (legacy) source** — For legacy OS emulation, checked out as `../Meadow/`
- **Meadow.CLI** — For CLI interaction with the emulator, checked out and built (see [Using Meadow.CLI](#using-meadowcli))

## Project Structure

```
Meadow.OS.Emulator/
├── build/                    # Build output (new OS)
├── build/legacy/             # Build output (legacy OS)
├── build-meadow.os-emulated.sh    # Builds new Meadow.OS with emulator defconfig
├── build-meadow.os_legacy-emulated.sh  # Builds legacy Meadow with emulator tweaks
├── run.sh                    # Launches Renode with a given script
├── platforms/
│   ├── stm32f777.repl        # STM32F777 chip-level platform (all on-die peripherals)
│   └── meadow-f7.repl        # Meadow F7 board (uses chip + SDRAM + QSPI flash)
├── peripherals/              # C# peripherals (loaded at runtime via AdHoc Compiler)
│   └── Winbond_W25QXXXJV.cs  # 64MB QSPI flash (GenericSpiFlash subclass)
├── scripts/
│   ├── meadow-os.resc        # Main emulation script (interactive, with UART analyzer)
│   ├── meadow-os-gdb.resc    # GDB-enabled script (starts paused, GDB on port 3333)
│   ├── meadow-os-headless.resc # Headless script (UART to file, no analyzer)
│   ├── meadow-os-diag.resc   # Diagnostic script with SVC/context-switch logging hooks
│   ├── meadow-os-test.resc   # Test script with verbose logging
│   ├── meadow-nsh.resc       # NSH-only flat build (no user binary, no workarounds)
│   ├── meadow-legacy.resc    # Legacy OS — interactive mode
│   ├── meadow-legacy-headless.resc  # Legacy OS — headless mode
│   └── pydev/                # Python peripherals and hook references
│       ├── rcc.py            # RCC clock controller (ready-bit handling)
│       ├── otg_fs.py         # USB OTG FS stub (GRSTCTL/AHBIDL)
│       ├── qspi_dma_bypass.py # QSPI DMA bypass hook (reference/docs)
│       └── flipflop.py       # Simple read-back peripheral (PWR, LPTIM1)
├── tools/
│   ├── hcom_test.py          # HCOM protocol test tool (COBS + socket)
│   └── kconfig-tweak         # Kconfig shim for NuttX build
└── conductor/                # Project planning and track documents
```

## Building

### New OS (Meadow.OS)

```bash
./build-meadow.os-emulated.sh
```

Configures NuttX with `stm32f777zit6-meadow:meadow_os_emu`, builds in protected mode, and copies artifacts to `build/`.

For a flat NSH-only build:
```bash
./build-meadow.os-emulated.sh nsh
```

### Legacy OS (Meadow)

```bash
./build-meadow.os_legacy-emulated.sh
```

Builds from the legacy `Meadow/` repo (NuttX 7.x, `mono` defconfig) with emulator kconfig tweaks applied at build time. Outputs split binaries to `build/legacy/` (vectors, kernel, and user loaded separately to avoid zero-fill issues in the protected-mode memory layout).

Requires the legacy `Meadow/` repo checked out as a sibling directory and ARM GCC 10.3 installed at `/Applications/ARM/bin`.

## Running

```bash
./run.sh                    # Interactive mode (default: new OS)
./run.sh --legacy           # Interactive mode (legacy OS)
./run.sh meadow-os-gdb      # GDB mode (paused, GDB server on port 3333)
./run.sh meadow-os-diag     # Diagnostic mode with SVC/context logging
```

The script auto-detects Renode on `PATH` or in `/Applications/Renode.app` (macOS).

HCOM is exposed as a TCP socket on port 4242 for both OS variants.

### GDB Debugging

1. Start the emulator in GDB mode:
   ```bash
   ./run.sh meadow-os-gdb
   ```

2. In another terminal, connect GDB:
   ```bash
   arm-none-eabi-gdb build/nuttx.elf
   (gdb) target remote :3333
   (gdb) continue
   ```

3. UART output is written to `/tmp/renode-uart.txt` (tail it in another terminal).

## Platform Description

The platform is split into chip-level and board-level definitions:

### Chip: `stm32f777.repl`

All on-die STM32F777 peripherals:

| Category | Peripherals |
|---|---|
| CPU | Cortex-M7, 462 MIPS, SysTick @ 216 MHz, FPU IRQ |
| Memory | Flash 2MB (+ mirror), DTCM 128KB, SRAM1 368KB, SRAM2 16KB, ROM 64KB |
| Serial | USART1-3/6, UART4/5/7/8 (all as STM32F7_USART) |
| SPI | SPI1-6, QUADSPI controller (STM32H7_QuadSPI) |
| I2C | I2C1-4 |
| DMA | DMA1, DMA2 |
| Timers | TIM1-14 with AF pin mappings |
| GPIO | GPIOA-K (11 ports) with correct reset values |
| Analog | ADC1-3 (shared IRQ) |
| Bus | CAN1-3, Ethernet MAC, SDMMC, LTDC, DMA2D |
| System | NVIC, EXTI, SYSCFG, DWT, Flash controller, RCC, PWR, RTC, RNG |

### Board: `meadow-f7.repl`

External hardware on the Meadow F7 board:

| Peripheral | Address | Notes |
|---|---|---|
| SDRAM | 0xC0000000 | 32 MB external (via FMC) |
| QSPI Flash Memory | 0x90000000 | 64 MB memory-mapped region |
| W25QXXXJV | via QUADSPI | Winbond SPI NOR flash (C# peripheral) |

## Python Peripherals

### RCC (`rcc.py`)
Custom clock controller that auto-sets ready bits when the corresponding clock enable is written. Handles: `HSIRDY`, `HSERDY`, `PLLRDY`, `PLLI2SRDY`, `PLLSAIRDY`, `LSIRDY`, `LSERDY`, and mirrors clock switch bits (SW) to status (SWS) in `RCC_CFGR`.

### USB OTG FS (`otg_fs.py`)
Stub for USB OTG FS controller. Keeps AHBIDL bit set in `GRSTCTL` (`AHB` bus always idle), auto-clears software reset bits, and implements write-1-to-clear for `GINTSTS`.

### Flipflop (`flipflop.py`)
Simple read-back peripheral used for `PWR_CR1`, `PWR_CSR1`, and `LPTIM1` registers that just need to return whatever was last written.

## Workarounds

All workarounds use IronPython hooks in the `.resc` scripts. Hook addresses are binary-specific and must be re-scanned after kernel rebuild.

### SVC-with-PRIMASK Bug

Renode v1.16.1 silently skips `SVC` when `PRIMASK=1`. On real ARMv7-M, this escalates to HardFault (which NuttX handles intentionally). The skip causes `arm_fullcontextrestore()` to fall through and `PANIC()`. Fix: hooks at all 7 `SVC #0` addresses clear PRIMASK before execution.

```bash
# Re-scan SVC addresses after kernel rebuild:
arm-none-eabi-objdump -d build/nuttx.elf | grep 'svc\s'
```

### GPIO Board Version Detection

Unconnected GPIO pins with pull-ups read high (0x0F = UNKNOWN), causing version detection to fail. Fix: hook in `meadow_hw_version_find_device_ver` writes 0x01 (CCMv2) to the stack slot where the GPIO result is stored, bypassing the unconnected pins.

### QUADSPI Transfer Complete Flag

Renode's `STM32H7_QuadSPI` never sets TCF (bit 1) in the Status Register after indirect-mode transactions, causing the firmware to poll forever. Fix: hook at `qspi_waitstatusflags` entry skips the function via `cpu.PC = cpu.LR`.

### QUADSPI DMA Bypass

Renode's `STM32H7_QuadSPI` doesn't drive DMA requests, so DMA transfers to/from QSPI flash never complete (800ms watchdog timeout). Fix: hook at `qspi_memory_dma` entry reads `struct qspi_meminfo_s` from R1 and copies data directly between RAM and flash backing memory (0x90000000). A second hook fixes the return value (writes 0 to the stack slot where the caller stores R0).

### LSI Clock Calibration Skip

The power management subsystem spawns a thread (`pwrmgmt_lsi_calc_prep_thread_func`) that measures the LSI oscillator frequency using Timer 5 input capture. Renode doesn't emulate TIM5 capture/compare, causing the thread to busy-wait forever. Fix: hook at the thread entry point returns immediately with success (R0=0).

### FPU Frame Mismatch (Non-critical)

Renode exception entry always uses standard frames (32 bytes) even when the FPU is active. NuttX always calculates with extended frames (104 bytes). The saved stack pointer is off by 72 bytes but doesn't cause crashes since MSP restore comes from the hardware frame.

## NuttX Configuration

The emulator uses `stm32f777zit6-meadow:meadow_os_emu` defconfig with key settings:

- `CONFIG_BUILD_PROTECTED=y` — Protected build with kernel/user separation
- `CONFIG_NUTTX_USERSPACE=0x08100000` — User binary loaded at this address
- `CONFIG_INIT_ENTRYPOINT="hcom_main"` — Initial user-space task
- `CONFIG_SYSLOG_SERIAL_CONSOLE=y` — Syslog output to USART1
- `CONFIG_BOARD_RESET_ON_ASSERT=0` — Don't reset on assertion failures
- `CONFIG_STM32F7_UART4=y` — UART4 enabled for HCOM transport
- `CONFIG_HCOM_COMMS_DEVICE_NAME="/dev/ttyS1"` — HCOM redirected from USB to UART4
- USB CDC-ACM, USBDEV, and OTG FS disabled (not emulatable)

## Current Boot Status

NuttX boots through hardware init, task scheduling, QSPI flash init, MTD partition detection, LittleFS filesystem creation, and reaches a stable idle state.

```
MTD Geo info - total erase sectors 16384, eraseSize 4096 block/page size 256
Runtime       part:0, size: 3145728 ( 3072 Kb), Byte Offset:0x00000000, Block Offset     0
Meadow update part:1, size: 1835008 ( 1792 Kb), Byte Offset:0x00300000, Block Offset 12288
OTA State     part:2, size:    4096 (    4 Kb), Byte Offset:0x004c0000, Block Offset 19456
OS Persisted  part:3, size:    4096 (    4 Kb), Byte Offset:0x004c1000, Block Offset 19472
Assert Data   part:4, size:   32768 (   32 Kb), Byte Offset:0x004c2000, Block Offset 19488
Unused        part:5, size:  221184 (  216 Kb), Byte Offset:0x004ca000, Block Offset 19616
File System   part:6, size:61865984 (60416 Kb), Byte Offset:0x00500000, Block Offset 20480
Created 7 partitions, Total size:67108864 bytes (65536 Kb)
F/S creation complete.
Meadow reset code: 0x00, reset count: 1, power cycle count 1
Mono runtime was not found flashed in external flash.
```

Remaining:
- Mono runtime not found (expected — not flashed in QSPI backing memory)

## HCOM Communication

HCOM is the host-to-device protocol used by Meadow.CLI. In the emulator, HCOM is redirected from USB CDC-ACM to UART4, which is exposed as a TCP socket on port 4242.

```bash
# Test HCOM communication (emulator must be running):
python3 tools/hcom_test.py info       # Get device information
python3 tools/hcom_test.py name       # Get device name
python3 tools/hcom_test.py listen     # Listen for unsolicited messages
```

## Using Meadow.CLI

Meadow.CLI can connect to the emulated device over the HCOM TCP socket instead of USB. This requires the `SocketConnection` support in Meadow.CLI (available on the `feature/TCP_Socket_Connection_for_EMU` branch or later).

### Setup

1. Build Meadow.CLI (one time):
   ```bash
   cd ../Meadow.CLI
   dotnet build
   ```

2. From the build output directory (e.g., `Source/Meadow.CLI/bin/Debug/net8.0/`), configure the socket route:
   ```bash
   dotnet meadow.dll config route socket://localhost:4242
   ```

   This persists the connection setting — you only need to do it once.

3. Make sure the emulator is running, then use Meadow.CLI as normal:
   ```bash
   dotnet meadow.dll device info
   ```

### Example Output

```
Getting device info...
Meadow by Wilderness Labs
Board Information
    Model: F7Micro
    Hardware version: F7CoreComputeV2
    Device name: MeadowF7

Hardware Information
    Processor type: STM32F777IIK6
    ID: 00-00-00-00-00-00-00-00-00-00-00-00
    Serial number: 0000000A0000
    Coprocessor type: ESP32

Firmware Versions
    OS:
    Runtime:
    Coprocessor:
    Protocol: 8
```

Firmware versions are blank because the emulator injects placeholder version numbers (0.0.0.0) and the Mono runtime is not flashed.

### Switching Back to USB

To revert Meadow.CLI to USB for use with a physical device:
```bash
dotnet meadow.dll config route
```

## Next Steps

- **Flash persistence** — Investigate Renode `MappedMemory` file backing for persistent QSPI flash contents across runs; pre-populate with filesystem image if needed
- **ESP32 coprocessor** — Emulate the ESPCP protocol over SPI2 (see `renode-esp32` track)
- **CI integration** — Automated boot tests (see `renode-ci` track)
- **Upstream contributions** — PR STM32F777 platform, SVC bug fix, RCC ready bits, W25Q flash, QuadSPI completeness to Renode (see `renode-upstream` track)
