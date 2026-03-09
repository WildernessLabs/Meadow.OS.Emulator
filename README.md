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

## Project Structure

```
Meadow.OS.Emulator/
├── build/                    # Build output (nuttx.bin, nuttx_user.bin, nuttx.elf)
├── build-emulator.sh         # Builds NuttX with the emulator defconfig
├── run.sh                    # Launches Renode with a given script
├── platforms/
│   ├── stm32f777.repl        # STM32F777 chip-level platform (all on-die peripherals)
│   └── meadow-f7.repl        # Meadow F7 board (uses chip + SDRAM + QSPI flash)
├── peripherals/              # C# peripherals (loaded at runtime via AdHoc Compiler)
│   └── Winbond_W25QXXXJV.cs  # 64MB QSPI flash (GenericSpiFlash subclass)
├── scripts/
│   ├── meadow-os.resc        # Main emulation script (interactive, with UART analyzer)
│   ├── meadow-os-gdb.resc    # GDB-enabled script (starts paused, GDB on port 3333)
│   ├── meadow-os-diag.resc   # Diagnostic script with SVC/context-switch logging hooks
│   ├── meadow-os-test.resc   # Test script with verbose logging
│   ├── meadow-nsh.resc       # NSH-only flat build (no user binary, no workarounds)
│   └── pydev/                # Python peripherals and hook references
│       ├── rcc.py            # RCC clock controller (ready-bit handling)
│       ├── otg_fs.py         # USB OTG FS stub (GRSTCTL/AHBIDL)
│       ├── qspi_dma_bypass.py # QSPI DMA bypass hook (reference/docs)
│       └── flipflop.py       # Simple read-back peripheral (PWR, LPTIM1)
├── tools/
│   └── kconfig-tweak         # Kconfig shim for NuttX build
└── conductor/                # Project planning and track documents
```

## Building

The build script configures and compiles NuttX using the emulator-specific defconfig:

```bash
./build-emulator.sh
```

This will:
1. Configure NuttX with `stm32f777zit6-meadow:meadow_os`
2. Build in protected mode (two-pass: pass2 + mbedTLS + pass1 + final link)
3. Copy `nuttx.bin`, `nuttx_user.bin`, and `nuttx.elf` to `build/`

For a flat NSH-only build:
```bash
./build-emulator.sh nsh
```

## Running

```bash
./run.sh                    # Interactive mode (default: meadow-os)
./run.sh meadow-os-gdb      # GDB mode (paused, GDB server on port 3333)
./run.sh meadow-os-diag     # Diagnostic mode with SVC/context logging
```

The script auto-detects Renode on `PATH` or in `/Applications/Renode.app` (macOS).

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
Custom clock controller that auto-sets ready bits when the corresponding clock enable is written. Handles: `HSIRDY`, `HSERDY`, `PLLRDY`, `PLLI2SRDY`, `PLLSAIRDY`, and mirrors clock switch bits (SW) to status (SWS) in `RCC_CFGR`.

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

### FPU Frame Mismatch (Non-critical)

Renode exception entry always uses standard frames (32 bytes) even when the FPU is active. NuttX always calculates with extended frames (104 bytes). The saved stack pointer is off by 72 bytes but doesn't cause crashes since MSP restore comes from the hardware frame.

## NuttX Configuration

The emulator uses `stm32f777zit6-meadow:meadow_os` defconfig with key settings:

- `CONFIG_BUILD_PROTECTED=y` — Protected build with kernel/user separation
- `CONFIG_NUTTX_USERSPACE=0x08100000` — User binary loaded at this address
- `CONFIG_INIT_ENTRYPOINT="hcom_main"` — Initial user-space task
- `CONFIG_SYSLOG_SERIAL_CONSOLE=y` — Syslog output to USART1
- `CONFIG_BOARD_RESET_ON_ASSERT=0` — Don't reset on assertion failures

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
- `/dev/nxupd` registration status needs verification

## Next Steps

- **`/dev/nxupd` verification** — Confirm HCOM device registration now works with full flash chain
- **HCOM bring-up** — Redirect HCOM from USB CDC-ACM to UART for emulator interaction (see `renode-hcom` track)
- **ESP32 coprocessor** — Emulate the ESPCP protocol over SPI2 (see `renode-esp32` track)
- **CI integration** — Automated boot tests (see `renode-ci` track)
- **Upstream contributions** — PR STM32F777 platform, SVC bug fix, RCC ready bits, W25Q flash, QuadSPI TCF fix to Renode (see `renode-upstream` track)
