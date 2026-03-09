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
│   └── meadow-f7.repl        # Renode platform description (STM32F777ZIT6)
├── scripts/
│   ├── meadow-os.resc        # Main emulation script (interactive, with UART analyzer)
│   ├── meadow-os-gdb.resc    # GDB-enabled script (starts paused, GDB on port 3333)
│   ├── meadow-os-diag.resc   # Diagnostic script with SVC/context-switch logging hooks
│   ├── meadow-os-test.resc   # Test script with verbose logging
│   ├── meadow-nsh.resc       # NSH-only flat build (no user binary, no workarounds)
│   └── pydev/                # Python peripherals and hooks
│       ├── rcc.py            # RCC clock controller (ready-bit handling)
│       ├── otg_fs.py         # USB OTG FS stub (GRSTCTL/AHBIDL)
│       ├── fix_svc_primask.py    # SVC PRIMASK workaround hook (reference)
│       ├── fix_fpu_frame.py      # FPU frame mismatch correction hook
│       └── flipflop.py           # Simple read-back peripheral (PWR, LPTIM1)
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

`platforms/meadow-f7.repl` defines the full STM32F777ZIT6 hardware:

| Peripheral | Address | Notes |
|---|---|---|
| Flash | 0x08000000 | 2 MB (also mirrored at 0x00000000) |
| DTCM | 0x20000000 | 128 KB |
| SRAM1 | 0x20020000 | 368 KB |
| SRAM2 | 0x2007C000 | 16 KB |
| SDRAM (FMC) | 0xC0000000 | 32 MB external |
| QSPI Flash | 0x90000000 | 32 MB memory-mapped |
| CPU | Cortex-M7 | 462 MIPS, SysTick @ 216 MHz |
| USART1-3, 6 | various | STM32F7 USART |
| SPI1-3 | various | STM32 SPI |
| I2C1-4 | various | STM32F7 I2C |
| DMA1, DMA2 | various | STM32 DMA |
| Timers 1-14 | various | With AF pin mappings |
| Ethernet | 0x40028000 | Synopsys MAC |
| LTDC | 0x40016800 | Display controller |
| SDMMC | 0x40012C00 | SD card |
| RTC, RNG | various | Standard STM32F4 |

## Python Peripherals

### RCC (`rcc.py`)
Custom clock controller that auto-sets ready bits when the corresponding clock enable is written. Handles: HSIRDY, HSERDY, PLLRDY, PLLI2SRDY, PLLSAIRDY, and mirrors clock switch bits (SW) to status (SWS) in RCC_CFGR.

### USB OTG FS (`otg_fs.py`)
Stub for USB OTG FS controller. Keeps AHBIDL bit set in GRSTCTL (AHB bus always idle), auto-clears software reset bits, and implements write-1-to-clear for GINTSTS.

### Flipflop (`flipflop.py`)
Simple read-back peripheral used for PWR_CR1, PWR_CSR1, and LPTIM1 registers that just need to return whatever was last written.

## Workarounds

### SVC-with-PRIMASK Bug (Critical)

**Problem:** On real ARMv7-M hardware, executing `SVC` while `PRIMASK=1` causes priority escalation to HardFault, which NuttX handles intentionally. Renode v1.16.1 silently skips the SVC instruction instead, causing `arm_fullcontextrestore()` to return (it shouldn't), which triggers `PANIC()`.

**Impact:** Without this fix, NuttX crashes during the first context switch (AppBringUp task exit).

**Fix:** IronPython hooks at every `SVC #0` instruction in the kernel binary unconditionally clear PRIMASK before the instruction executes. The saved CPU context preserves the original PRIMASK value, so exception return restores it correctly.

The hooks are defined inline in the `.resc` scripts:
```
set svc_fix "from Antmicro.Renode.Peripherals.CPU import RegisterValue; cpu.PRIMASK = RegisterValue.Create(0, 32)"
cpu AddHook 0x08002240 $svc_fix
cpu AddHook 0x08038cd8 $svc_fix
...
```

**Important:** These addresses are specific to the current kernel binary. If the kernel is rebuilt, the SVC instruction addresses may change and must be re-scanned:
```bash
arm-none-eabi-objdump -d build/nuttx.elf | grep 'svc\s'
```

### FPU Frame Mismatch (Non-critical)

Renode exception entry always uses standard frames (32 bytes, EXC_RETURN bit4=1) even when the FPU is active. NuttX's `arm_exception.S` always adds 104 bytes (extended frame size) when calculating REG_R13. This means the saved stack pointer in context is off by 72 bytes when a standard frame is used. This doesn't cause crashes because MSP restore comes from the hardware frame, but could affect stack pointer verification code.

## NuttX Configuration

The emulator uses `stm32f777zit6-meadow:meadow_os` defconfig with key settings:

- `CONFIG_BUILD_PROTECTED=y` — Protected build with kernel/user separation
- `CONFIG_NUTTX_USERSPACE=0x08100000` — User binary loaded at this address
- `CONFIG_INIT_ENTRYPOINT="hcom_main"` — Initial user-space task
- `CONFIG_SYSLOG_SERIAL_CONSOLE=y` — Syslog output to USART1
- `CONFIG_BOARD_RESET_ON_ASSERT=0` — Don't reset on assertion failures

## Current Boot Status

NuttX boots successfully through:
1. Hardware init (clocks, DMA, GPIO, QUADSPI)
2. Kernel thread creation (hpwork, lpwork)
3. AppBringUp task runs and exits cleanly
4. `hcom_main` starts and attempts HCOM setup

Current stop point — HCOM initialization fails because `/dev/nxupd` doesn't exist (this device is normally provided by the Meadow coprocessor communication channel):
```
stm32_boot.c@300-ERROR: Meadow version could not be determined
hcom_via_nx_access.c@94-/dev/nxupd Failed to open, errno:2
hcom_startup_manager.c@174-setup hcom nx access:-1
```

The system enters a healthy idle state after this (CPU in `wfi`, idle task at head of ready list).

## Next Steps

- Stub or redirect HCOM communication (USB CDC-ACM to UART) for emulator interaction
- Add QSPI flash filesystem support for app deployment
- Implement additional peripherals as needed for specific Meadow.OS features
