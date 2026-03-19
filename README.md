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
./build-meadow.os_legacy-emulated.sh       # Build NuttX firmware
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

| Config | OS | Board | Coprocessor |
|--------|----|-------|-------------|
| `configs/legacy.yaml` | Legacy NuttX | F7FeatherV2 | Stub (EspcpStub) |
| `configs/meadow-os.yaml` | Meadow.OS rebase | F7FeatherV2 | Stub |

Switch configurations:
```bash
cp configs/meadow-os.yaml emulator.config.yaml    # Switch to Meadow.OS rebase
./run.sh                                            # Uses new config

./run.sh --config configs/legacy.yaml              # One-off override
```

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
  The ELF is output to `Meadow-ESP32/build/MeadowComms.elf`. Alternatively, set `ESP32_FW_PATH` to point to an existing ELF.
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

The emulator connects via TCP socket instead of USB serial. This requires the `feature/TCP_Socket_Connection_for_EMU` branch of Meadow.CLI, built from source:

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
├── Meadow-ESP32/          # ESP32 firmware source (for --esp32 mode, ESP-IDF 4.2.4)
├── Meadow.CLI/            # Branch: feature/TCP_Socket_Connection_for_EMU (build from source)
├── Meadow.Samples/        # Sample apps (Blinky, etc.)
└── renode/                # WildernessLabs/renode fork (branch: meadow-emulator-fixes)
```

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
├── build-meadow.os_legacy-emulated.sh  # Builds legacy Meadow with emulator tweaks
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
./build-meadow.os_legacy-emulated.sh       # Build NuttX legacy OS
```

Builds from the sibling `Meadow/` repo with emulator kconfig tweaks. Requires ARM GCC 10.3. Outputs split binaries to `build/legacy/`.

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

## Renode Fork Fixes

The WildernessLabs/renode fork (`meadow-emulator-fixes` branch) contains fixes across two areas:

### STM32/ARM (2 fixes)

| Fix | Impact |
|-----|--------|
| SVC/PRIMASK handling in NVIC | Without this, per-address hooks on every SVC site are needed (~12,000x slower) |
| RTC backup register count (20→32) | Meadow uses BKP31R for runtime state persistence |

### Xtensa/ESP32 (15 fixes in tlib)

Fixes for TCG code generation (`tcg_const_i32` → local temps in retw, rfw, callw, entry), window overflow handling (rfwo no-phys-sync, window_check n>w early exit, test_ill_retw a0 fixup), interrupt delivery, and CCOUNT/WAITI timing. See the [plan](conductor/tracks/renode-esp32-dual/plan.md) for the full fix table.

## Verification Protocol

Three tiers of testing, run in order (see [workflow.md](conductor/workflow.md)):

| Tier | What | How | Time |
|------|------|-----|------|
| 1 | Smoke test | `python3 tools/hcom_test.py` | 5 seconds |
| 2 | CLI device info | `meadow device info` | 10 seconds |
| 3 | Blinky deploy + run | `meadow app run` from BlinkyCS | ~2.5 minutes |

## Known Issues

See [KnownIssues_and_Errata.md](KnownIssues_and_Errata.md) for a full list. Key items:

- **UART RX bypass (§8.6)** — File deploy uses a hack to bypass per-byte UART
  interrupt overhead. Works but ~4/36 files need CLI retry due to edge cases.
  See KnownIssues for full details and proper fix paths.
- **Flash persistence (§4.4)** — QSPI flash is volatile. Deployed files are
  lost when the emulator restarts.
- **ESPCP polling disabled by default** — needed for WiFi/BLE. The dual-machine
  script enables it.
