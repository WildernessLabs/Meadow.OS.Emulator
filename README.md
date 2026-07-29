# Meadow.OS Emulator

Full hardware emulation of Meadow.OS using [Renode](https://renode.io). Supports two modes:

- **Single-machine** — STM32F777 (NuttX) with stubbed ESP32 coprocessor (EspcpStub)
- **Dual-machine** — STM32F777 (NuttX) + real ESP32 (Meadow firmware) with bidirectional SPI bridge

Both modes support Meadow.CLI connection via TCP socket for `device info`, app deployment, and all standard HCOM commands.

## Current Status

The legacy Meadow firmware boots with Mono JIT, loads .NET assemblies from LittleFS, and runs Meadow apps end-to-end. In dual-machine mode, ESPCP frames flow bidirectionally between STM32 and ESP32 — the firmware processes incoming frames and responds with device info ("MeadowF7").

## Quick Start

```bash
./setup.sh                                 # Check & install all prerequisites
./build-meadow.os-emulated.sh             # Build NuttX firmware (uses emulator.config.yaml)
./run.sh                                   # Start emulator (uses emulator.config.yaml)
```

Connect Meadow.CLI (requires building from source — see [Using Meadow.CLI](#using-meadowcli)):
```bash
dotnet run --project ../Meadow.CLI/Source/Meadow.CLI -- config route socket://localhost:4242
dotnet run --project ../Meadow.CLI/Source/Meadow.CLI -- device info
```

## Configuration

The emulator reads settings from `emulator.config.yaml` at the repo root. Pre-built
configs for common setups are in `configs/`:

| Config | OS | Runtime | Board | Coprocessor |
|--------|----|---------|-------|-------------|
| `configs/legacy.yaml` | Legacy NuttX | Legacy Mono 6.9 | F7FeatherV2 | Stub (EspcpStub) |
| `configs/meadow-os.yaml` | Meadow.OS rebase | — | F7FeatherV2 | Stub |
| `configs/legacy-os_modern-dotnet.yaml` | Legacy NuttX | .NET 10 Mono | F7FeatherV2 | Stub |

Switch configurations:
```bash
cp configs/meadow-os.yaml emulator.config.yaml    # Switch to Meadow.OS rebase
./run.sh                                            # Uses new config

./run.sh --config configs/legacy.yaml              # One-off override
```

### Building for .NET 10 Mono

The `legacy-os_modern-dotnet` config builds from the same legacy NuttX source but outputs
to `build/dotnet10/` and launches the `.NET 10` Renode script with monovm hosting API support:

```bash
./build-meadow.os-emulated.sh --config configs/legacy-os_modern-dotnet.yaml
./run.sh --config configs/legacy-os_modern-dotnet.yaml
```

This requires the `runtime/` sibling repo with the .NET 10 Mono runtime built for NuttX
(`runtime/src/mono/build-nuttx.sh`). The NuttX firmware itself is identical to the legacy
build — the .NET 10 runtime swap happens via the Renode script.

Each config has commented-out alternatives for every setting:
```yaml
os: legacy
# os: meadow-os

board: F7FeatherV2
# board: F7CoreComputeV2

coprocessor: stub
# coprocessor: dual-machine
```

CLI args override config values: `--legacy`, `--esp32`, `--port=N`.

## Running

```bash
./run.sh                                   # Uses emulator.config.yaml
./run.sh --config configs/meadow-os.yaml   # One-off config override
./run.sh --legacy --esp32                  # CLI overrides (dual-machine mode)
./run.sh --port=5555                       # Custom monitor port
./run.sh meadow-legacy-debug              # Specific script (GDB mode, port 3333)
./run.sh --help                            # Show all options
```

`run.sh` auto-detects the custom Renode fork in `../renode/`, falling back to PATH or `/Applications/Renode.app`.

On startup:
```
=== Starting Renode with scripts/meadow-dual.resc ===
Monitor: telnet localhost 1234
HCOM:    socket://localhost:4242
ESP32:   dual-machine mode (real coprocessor)
         meadow config route socket://localhost:4242
```

## ESP32 Coprocessor (Dual-Machine Mode)

The `--esp32` flag enables full ESP32 coprocessor emulation alongside the STM32. Both CPUs run simultaneously in the same Renode session, connected via SPI bridge:

```
STM32 (NuttX)                          ESP32 (Meadow firmware)
  ESPCP driver                           SpiTransportProvider
    │                                      │ ProcessMessages loop
  SpiBridge (C#)  ──── SPI frames ──→   ESP32_SPI (C#)
    on SPI2          ←──────────────      on SPI3
    │                                      │
  HCOM (UART4)                           DPORT interrupt matrix → CPU
    TCP 4242
```

**What works:**
- STM32 sends ESPCP polling frames → ESP32 firmware receives and processes them
- Firmware responds with device info (System events: "MeadowF7", "emulator")
- Firmware sends Response frames (type=0x20) with payloads back to STM32
- SPI ISR cycles continuously: prepare_data → user_start → arm → inject → respond
- Meadow.CLI `device info` returns correct hardware info through the full stack

**ESP32 firmware prerequisites:**
- Pre-built `MeadowComms.elf` — build from the `Meadow-ESP32` sibling repo:
  ```bash
  cd ../Meadow-ESP32
  source ~/esp/esp-idf-v4.2.4/export.sh   # ESP-IDF toolchain
  ./build.sh
  ```
  The ELF is output to `Meadow-ESP32/build/MeadowComms.elf`. The `Meadow-ESP32` sibling repo location can be overridden with `MEADOW_EMULATOR_ESP_DIR`; alternatively, set `ESP32_FW_PATH` to point directly to an existing ELF.
- ESP32 ROM binary at `roms/esp32-v3-rom.bin` (see `roms/README.md`)
- ESP-IDF 4.2.4 toolchain (only needed for building, not running)

**Custom Renode fork required:** The dual-machine mode requires 15+ tlib patches for Xtensa window register handling, interrupt delivery, and TCG code generation. These are on the `meadow-emulator-fixes` branch of the WildernessLabs tlib fork. See the [plan](conductor/tracks/renode-esp32-dual/plan.md) for details.

### How It Works

The ESP32 runs the real Meadow firmware (`MeadowComms.elf`) — the same binary that runs on physical hardware. The SPI bridge peripheral (`SpiBridge.cs`) on the STM32 side captures ESPCP frames from NuttX and queues them. The `ESP32_SPI.cs` peripheral on the ESP32 side injects queued frames into the firmware's DMA buffers and fires the SPI interrupt. The firmware's ISR processes the frame through its standard `ProcessMessages` loop and generates a response, which flows back through the bridge.

Key C# peripherals:
- `peripherals/SpiBridge.cs` — ISPIPeripheral with cross-machine frame queues
- `peripherals/ESP32_SPI.cs` — SPI3 register map, DMA descriptor chains, IRQ generation
- `peripherals/ESP32_DPORT.cs` — 69-source interrupt matrix with level-OR routing

## Using Meadow.CLI

The emulator connects via TCP socket instead of USB serial. This requires the `feature/TCP_Socket_Connection_for_EMU` branch of Meadow.CLI, built from source. The `../Meadow.CLI` path below is the default; override it with `MEADOW_EMULATOR_CLI_DIR` (and `../Meadow.Samples` with `MEADOW_EMULATOR_SAMPLES_DIR`):

```bash
# Build the CLI from source (one-time setup):
cd ../Meadow.CLI
git checkout feature/TCP_Socket_Connection_for_EMU
cd Source
dotnet build Meadow.CLI.sln

# Configure the socket route (persists across sessions):
dotnet run --project Meadow.CLI -- config route socket://localhost:4242
```

Then with the emulator running:
```bash
# Device info
dotnet run --project ../Meadow.CLI/Source/Meadow.CLI -- device info

# Deploy and run an app (builds, trims, deploys, enables Mono)
cd "../Meadow.Samples/Source/Meadow F7/Blinky/BlinkyCS"
dotnet run --project /path/to/Meadow.CLI/Source/Meadow.CLI -- app run

# Deploy a single file
dotnet run --project ../Meadow.CLI/Source/Meadow.CLI -- file write -f path/to/file.dll
```

> **Important:** The globally-installed `meadow` CLI tool (`dotnet tool install`) does NOT support socket connections. You must build from the branch above. The `SocketConnection` class handles raw HCOM/COBS over TCP, which is how Renode exposes the UART.

### App Deployment

The `app run` command builds, trims, deploys, and starts a Meadow app:

1. Builds the project (Release config)
2. Trims assemblies using the Meadow linker (removes unused BCL code)
3. Deploys trimmed binaries to the emulator via HCOM file transfer
4. Enables Mono runtime and listens for app output

A full BlinkyCS deploy (36 trimmed files) takes ~2.5 minutes under emulation.
File transfers use a UART RX bypass hook that gives ~30x speedup over the
raw emulated UART path (see [KnownIssues §8.6](KnownIssues_and_Errata.md#86-hcom-file-download--uart-rx-bypass-hack)).

The BlinkyCS sample requires `F7FeatherV2` board declaration (the emulator's
default). The sample has both `V1App` and `V2App` classes — Meadow selects
the correct one based on the device's reported hardware version.

## Prerequisites

The `setup.sh` script handles all of this automatically. For manual setup:

- **.NET SDK 8.0+** — `dotnet` must be on PATH
- **ARM toolchain** — `arm-none-eabi-gcc` (install via `brew install --cask gcc-arm-embedded`)
- **GNU Make 4.x+** — macOS ships Make 3.81; install `gmake` via `brew install make`

### Required Sibling Repos

```
wilderness_labs/
├── Meadow.OS.Emulator/    # This repo
├── Meadow/                # Legacy NuttX-based Meadow OS (STM32 firmware source)
├── Meadow.OS/             # New Meadow.OS rebase (updated NuttX, unreleased)
├── runtime/               # .NET 10 Mono runtime (for legacy-os_modern-dotnet config)
├── Meadow-ESP32/          # ESP32 firmware source (for --esp32 mode, ESP-IDF 4.2.4)
├── Meadow.CLI/            # Branch: feature/TCP_Socket_Connection_for_EMU (build from source)
├── Meadow.Samples/        # Sample apps (Blinky, etc.)
└── renode/                # WildernessLabs/renode fork (branch: meadow-emulator-fixes)
```

The paths above are just the auto-detected defaults. Override any of them with an env var,
resolved once by `scripts/resolve-locations.sh` and honored by `setup.sh`, `run.sh`, and both
`build-meadow.*` scripts:

| Env var                        | Default (auto-detected)                          |
|---------------------------------|---------------------------------------------------|
| `MEADOW_EMULATOR_OS_DIR`        | `../Meadow` (legacy) / `../Meadow.OS` (rebase)    |
| `MEADOW_EMULATOR_CLI_DIR`       | `../Meadow.CLI`                                   |
| `MEADOW_EMULATOR_ESP_DIR`       | `../Meadow-ESP32`                                 |
| `MEADOW_EMULATOR_SAMPLES_DIR`   | `../Meadow.Samples`                               |

If unset, each falls back to an auto-detect chain against sibling directory names (e.g.
`MEADOW_EMULATOR_OS_DIR` also checks `../Legacy`/`../Rebase`, and `MEADOW_EMULATOR_ESP_DIR`
also checks `$MEADOW_EMULATOR_OS_DIR/esp32`). `RENODE_PATH` (a Renode fork checkout) and
`ESP32_FW_PATH` (a direct path to a built `MeadowComms.elf`) are separate, pre-existing,
more-specific overrides and are unaffected by these four.

### Custom Renode Fork (required)

The emulator requires the [WildernessLabs/renode](https://github.com/WildernessLabs/renode) fork which includes critical fixes for SVC/PRIMASK handling, STM32 RTC backup registers, and Xtensa window register emulation. **Without these fixes, the STM32 side is ~12,000x slower and ESP32 emulation doesn't work at all.**

`./setup.sh` clones and builds this automatically. Manual setup:

```bash
cd ../renode
git checkout meadow-emulator-fixes
git submodule update --init src/Infrastructure
bash build.sh --host-arch aarch64    # Apple Silicon
# bash build.sh                      # Intel / Linux
```

## Project Structure

```
Meadow.OS.Emulator/
├── build/legacy/             # Build output (legacy OS)
├── build/dotnet10/           # Build output (.NET 10 mono)
├── build-meadow.os-emulated.sh  # Builds NuttX firmware (supports legacy, meadow-os, dotnet10)
├── run.sh                    # Launches Renode (--legacy, --esp32 flags)
├── platforms/
│   ├── stm32f777.repl        # STM32F777 chip-level platform
│   ├── meadow-f7.repl        # Meadow F7 board (chip + SDRAM + QSPI flash)
│   ├── meadow-f7-dual.repl   # Dual-machine variant (SpiBridge on SPI2)
│   └── meadow-esp32.repl     # ESP32 platform (Xtensa + peripherals)
├── peripherals/              # C# extensions (loaded at runtime)
│   ├── SpiBridge.cs          # SPI bridge: cross-machine ESPCP frame queues
│   ├── ESP32_SPI.cs          # ESP32 SPI3: register map, DMA, frame injection
│   ├── ESP32_DPORT.cs        # DPORT interrupt matrix (69 sources)
│   ├── ESP32_RTC_CNTL.cs     # RTC control with reactive status bits
│   ├── ESP32_TimerGroup.cs   # Timer group with calibration ready bit
│   ├── Winbond_W25QXXXJV.cs  # 64MB QSPI NOR flash
│   ├── STM32F7_RCC_Custom.cs # RCC with clock ready bit auto-set (replaces Python)
│   ├── QspiDmaBypass.cs      # QSPI DMA bypass (C# hook)
│   ├── EspcpSpiBypass.cs     # ESPCP SPI DMA bypass (C# hook)
│   ├── MqReceiveIntercept.cs # ESPCP mq_receive interception
│   └── UartRxBypass.cs       # UART RX bypass for fast file deploy (HACK — see §8.6)
├── scripts/
│   ├── meadow-dual.resc            # Dual-machine: STM32 + ESP32
│   ├── meadow-legacy-headless.resc # Legacy OS — headless (recommended)
│   ├── meadow-legacy.resc          # Legacy OS — interactive
│   ├── meadow-legacy-debug.resc    # Legacy OS — with GDB server
│   ├── esp32-meadow-boot-no-retw-hooks.resc  # ESP32-only boot test
│   └── ...
├── roms/                     # ESP32 ROM binary (not redistributable)
├── tools/                    # Build helpers, hook extraction, testing
├── upstream_fixes.md         # Tracker for fixes to upstream Renode
└── conductor/                # Project planning and track documents
```

## Building the Firmware

### STM32 (NuttX) — required for all modes

```bash
./build-meadow.os-emulated.sh                                          # Build using emulator.config.yaml
./build-meadow.os-emulated.sh --config configs/legacy.yaml             # Build legacy OS
./build-meadow.os-emulated.sh --config configs/meadow-os.yaml          # Build Meadow.OS rebase
./build-meadow.os-emulated.sh --config configs/legacy-os_modern-dotnet.yaml  # Build for .NET 10
```

The build script reads `EMU_OS` from the config to select the source tree (`Meadow/` for legacy, `Meadow.OS/` for rebase), applies emulator-specific kconfig tweaks, and extracts hook addresses for Renode. Output directories: `build/legacy/`, `build/`, or `build/dotnet10/`.

### ESP32 — required only for `--esp32` mode

```bash
cd ../Meadow-ESP32
source ~/esp/esp-idf-v4.2.4/export.sh     # Activate ESP-IDF toolchain
./build.sh                                  # Build MeadowComms.elf
```

Requires ESP-IDF 4.2.4 with the Xtensa toolchain. Output: `Meadow-ESP32/build/MeadowComms.elf`. The `run.sh --esp32` script auto-discovers this ELF in the sibling repo.

## GDB Debugging

```bash
./run.sh --legacy meadow-legacy-debug
# In another terminal:
arm-none-eabi-gdb build/legacy/nuttx.elf
(gdb) target remote :3333
(gdb) continue
```

## Renode Customizations

### Custom Platform Definitions

Both MCUs have full platform definitions written from scratch (Renode has no stock STM32F777 or Meadow-specific ESP32 platform):

| Platform file | Description |
|---------------|-------------|
| `platforms/stm32f777.repl` | STM32F777ZIT6 chip-level: Cortex-M7, NVIC, all USARTs, SPI, I2C, DMA, QSPI, RCC, GPIO, timers |
| `platforms/meadow-f7.repl` | Meadow F7 board: chip + 32MB SDRAM (0xC0000000) + 64MB QSPI flash (0x90000000) + UART4 socket |
| `platforms/meadow-f7-dual.repl` | Dual-machine variant: adds SpiBridge on SPI2 for ESP32 communication |
| `platforms/meadow-esp32.repl` | ESP32 platform: Xtensa LX6, DPORT interrupt matrix, SPI3, UART, RTC_CNTL, timer groups |

### Custom C# Peripherals

| Peripheral | Purpose |
|------------|---------|
| `Winbond_W25QXXXJV.cs` | 64MB QSPI NOR flash with quad-SPI command set |
| `STM32F7_RCC_Custom.cs` | RCC with auto-set clock ready bits (LSI/LSE/HSE/PLL) |
| `EspcpStub.cs` | ESP32 coprocessor SPI stub (single-machine mode) |
| `SpiBridge.cs` | Cross-machine SPI frame queues (dual-machine mode) |
| `ESP32_SPI.cs` | ESP32 SPI3 register map, DMA descriptor chains, IRQ generation |
| `ESP32_DPORT.cs` | DPORT interrupt matrix (69 sources, level-OR routing) |
| `ESP32_RTC_CNTL.cs` | RTC control with reactive status bits |
| `ESP32_TimerGroup.cs` | Timer group with calibration ready bit |
| `QspiDmaBypass.cs` | C# hook: QSPI DMA memory copy bypass (22K+ calls during assembly load) |
| `EspcpSpiBypass.cs` | C# hook: ESPCP SPI DMA bypass |
| `SvcPrimaskFix.cs` | C# hook: PRIMASK clearing at SVC (fallback if fork not available) |
| `MqReceiveIntercept.cs` | C# hook: mq_receive interception for ESPCP polling |
| `UartRxBypass.cs` | C# hook: UART RX bypass for fast file deploy (currently disabled — Renode 1.16.1 API change) |
| `FileReadBypass.cs` | C# hook: file read bypass for .NET 10 assembly loading |
| `XtensaHotPathHooks.cs` | C# hooks for ESP32 hot-path workarounds |

### Renode Fork Fixes

The WildernessLabs/renode fork (`meadow-emulator-fixes` branch) contains 17 fixes across two areas. See [upstream_fixes.md](upstream_fixes.md) for PR status.

### STM32/ARM (2 fixes in Infrastructure)

| Fix | Impact |
|-----|--------|
| SVC/PRIMASK handling in NVIC | ARM spec says SVC with PRIMASK=1 should escalate to HardFault, not be silently skipped. Without this fix, per-address hooks on every SVC site are needed (~12,000x slower) |
| RTC backup register count (20→32) | STM32F4_RTC `BackupEnd` only covers 20 registers; STM32F7 has 32 (BKP0R–BKP31R). Meadow uses BKP31R for runtime state persistence |

**Not yet fixed in fork (hook workaround):** STM32F4_RTC crashes with `ArgumentOutOfRangeException` when firmware writes BCD date register with partially-populated fields (intermediate zero month/day creates invalid DateTime). Worked around by skipping `hcom_nx_config_set_time_to_os_build_time` via hook.

### Xtensa/ESP32 (15 fixes in tlib)

| # | Fix | Description |
|---|-----|-------------|
| 1 | arch_tlb_fill error code | Return real MMU cause instead of misidentifying as syscall |
| 2 | WAITI halt | Don't halt CPU (prevents deadlock) |
| 3 | CCOUNT/CCOMPARE | Fix time unit conversion |
| 4 | clock_freq_khz | Set to 240MHz (ESP32 actual speed) |
| 5 | win_helper | Assert→logging (don't crash on window edge cases) |
| 6 | translate_retw | Local TCG temp (fixes register corruption) |
| 7 | set_irq_pending_bit | Call check_interrupts (interrupts were being missed) |
| 8 | OnGPIO | Remove conflicting base.OnGPIO call (in Xtensa.cs) |
| 9 | translate_rfw | Local TCG temp |
| 10 | window_check | n>w early exit (prevents invalid window operations) |
| 11 | test_ill_retw | a0 fixup for WOVF8 scratch register |
| 12 | rfwo | No-phys-sync (restore_owb_no_phys_sync) |
| 13 | translate_callw | Local CALLINC temp |
| 14 | translate_entry | Local temps + rotation in helper + cpu_R reload |
| 15 | Diagnostic logging | Removed debug noise |

See the [ESP32 dual-machine plan](conductor/tracks/renode-esp32-dual/plan.md) for full details and commit hashes.

## Verification Protocol

Three tiers of testing, run in order (see [workflow.md](conductor/workflow.md)):

| Tier | What | How | Time |
|------|------|-----|------|
| 1 | Smoke test | `python3 tools/hcom_test.py` | 5 seconds |
| 2 | CLI device info | `meadow device info` | 10 seconds |
| 3 | Blinky deploy + run | `meadow app run` from BlinkyCS | ~2.5 minutes |

## Known Issues

See [KnownIssues_and_Errata.md](KnownIssues_and_Errata.md) for a full list. Key items:

- **UART RX bypass disabled (§8.6)** — The `UartRxBypass.cs` hook that sped up
  file deployment is currently disabled due to API incompatibility with Renode
  1.16.1. File deploy via Meadow.CLI works but is very slow without the bypass.
- **Flash persistence (§4.4)** — QSPI flash is volatile across Renode sessions.
  A pre-built LittleFS image is loaded at boot so the filesystem is pre-populated,
  and deployed files persist across in-session resets, but everything is lost when
  Renode exits.
