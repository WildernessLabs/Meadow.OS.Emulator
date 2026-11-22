# Meadow.OS Emulator

Full hardware emulation of Meadow.OS using [Renode](https://renode.io). Supports two modes:

- **Single-machine** — STM32F777 (NuttX) with stubbed ESP32 coprocessor (EspcpStub)
- **Dual-machine** — STM32F777 (NuttX) + real ESP32 (Meadow firmware) with bidirectional SPI bridge
- **esp-emu** (Legacy) — STM32F777 (NuttX) + the real Meadow firmware's ESP32-S3 build running in Espressif's [esp-emu](https://github.com/espressif/esp-emulator), joined over SPI2, UART5 and EN

Both modes support Meadow.CLI connection via TCP socket for `device info`, app deployment, and all standard HCOM commands.

## Current Status

The legacy Meadow firmware boots with Mono JIT, loads .NET assemblies from LittleFS, and runs Meadow apps end-to-end. In dual-machine mode, ESPCP frames flow bidirectionally between STM32 and ESP32 — the firmware processes incoming frames and responds with device info ("MeadowF7").

**Meadow.OS (Rebase, NuttX 12)** boots as an F7CoreComputeV2, answers Meadow.CLI over the socket, accepts app deployments, starts Mono (.NET 10) and runs managed apps. Rebase's `tests/managed/MiniTests` gives the same result as on a board: 733 ran, 2 skipped, 1 known failure. See [Meadow.OS (Rebase)](#meadowos-rebase).

## Quick Start

```bash
./setup.sh                                 # Check & install all prerequisites
./build-meadow.os-emulated.sh             # Build NuttX firmware (uses emulator.config.yaml)
./run.sh                                   # Start emulator (uses emulator.config.yaml)
```

Connect Meadow.CLI (release 3.0.3 or later; see [Using Meadow.CLI](#using-meadowcli)):
```bash
meadow device info -s socket://localhost:4242
```

## Configuration

The emulator reads settings from `emulator.config.yaml` at the repo root. Pre-built
configs for common setups are in `configs/`:

| Config | OS | Runtime | Board | Coprocessor |
|--------|----|---------|-------|-------------|
| `configs/legacy.yaml` | Legacy NuttX | Legacy Mono 6.9 | F7FeatherV2 | Stub (EspcpStub) |
| `configs/legacy-ccm.yaml` | Legacy NuttX (meadow-3.0, `SupportEmulator` branch) | .NET 10 Mono (built by the OS) | F7CoreComputeV2 | Stub |
| `configs/meadow-os.yaml` | Meadow.OS rebase | .NET 10 Mono (built by the OS) | F7CoreComputeV2 | Stub |

Switch configurations:
```bash
cp configs/meadow-os.yaml emulator.config.yaml    # Switch to Meadow.OS rebase
./run.sh                                            # Uses new config

./run.sh --config configs/legacy.yaml              # One-off override
```

### .NET 10 Mono

`configs/legacy-ccm.yaml` (Legacy meadow-3.0) and `configs/meadow-os.yaml` both
run .NET 10 Mono, which each OS's `build.sh --emulator` builds from the `runtime/`
sibling repo.  The older `legacy-os_modern-dotnet` config, which tried to link a
separately built .NET 10 Mono into the old Legacy firmware, has been retired; a
config that still sets `build.runtime` is refused with a pointer to
`legacy-ccm.yaml`.

Each config has commented-out alternatives for every setting:
```yaml
os: legacy
# os: meadow-os

board: F7FeatherV2
# board: F7CoreComputeV2

coprocessor: stub
# coprocessor: dual-machine
# coprocessor: esp-emu
```

CLI args override config values: `--legacy`, `--esp32`, `--esp-emu`, `--port=N`.

## Running

```bash
./run.sh                                   # Uses emulator.config.yaml
./run.sh --config=configs/meadow-os.yaml   # One-off config override (--config FILE also works)
./run.sh --legacy --esp32                  # CLI overrides (dual-machine mode)
./run.sh --legacy --esp-emu                # ESP in esp-emu (see below)
./run.sh --port=5555                       # Custom monitor port
./run.sh --rebase --mount=app.bin           # Boot with a file system image (tools/make-lfs-image.sh)
./run.sh --gdb                             # GDB server on port 3333; the CPU waits at reset
./run.sh --legacy meadow-legacy            # Specific script from scripts/
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

### Booting with a pre-built file system

The emulated QSPI flash starts empty on every Renode start, so an app normally
has to be deployed with Meadow.CLI each time.  Instead, an app can be baked into
a LittleFS image of the file system partition and loaded at boot, with no
deploy.  This works for both OSes:

```bash
# Build the app once (creates its publish folder), then make the image.  Give the
# helper the same --config / --legacy / --rebase as run.sh, so the format matches.
tools/make-lfs-image.sh --rebase <app>/bin/Release/net10.0/publish fs-images/app.bin

# Boot with it; Mono starts the app straight away.  Watch its output with:
./run.sh --rebase --mount=fs-images/app.bin
meadow listen -s socket://localhost:4242
```

| OS | Image | Builder (compiled into `build/tools/` on first use) |
|----|-------|-----------------------------------------------------|
| Legacy (meadow-3.0) | LittleFS v1, 4 KB blocks, 256 byte read/prog | `tools/build_lfs_v1_image.c` + the Legacy checkout's `nuttx/fs/littlefs/` |
| Meadow.OS (Rebase) | LittleFS v2, 4 KB blocks, 1 KB read/prog | `tools/build_lfs_v2_image.c` + the Meadow.OS checkout's `nuttx/fs/littlefs/littlefs/` (unpacked by its first NuttX build) |

Keep images in `fs-images/`, which git ignores.  Both are the whole 61,865,984-byte partition at QSPI offset `0x500000`
(15,104 blocks), mounted as `/meadow0`.  The geometry is what each firmware
passes to `lfs_mount`; an image that does not match is not mountable, and the
firmware formats over it.

- `tools/make-lfs-image.sh` takes the same files Meadow.CLI's deploy would
  (managed assemblies and config files; no native host files, `.pdb` or XML docs
  unless `--pdbs` / `--xmldocs`) and puts them at the root of `/meadow0`.
  `--list` shows the files without building.  The OS comes from
  `--config=FILE` (its `os:`), `--legacy` or `--rebase`, as for `run.sh`;
  the default is `emulator.config.yaml`.
- `run.sh --mount` refuses an image the firmware would not mount: larger than
  the partition, no LittleFS superblock, or the other OS's LittleFS version.
  Without `--mount` the flash starts empty as before.  The image is loaded once
  before the machine starts, never on a reset, so a later Meadow.CLI deploy is
  not overwritten.
- Example: Rebase's `tests/managed/MiniTests` from an image runs to
  `TESTRUN END` in about 85-90 s from Renode start on either OS (733 ran,
  2 skipped, 1 known failure), against a few minutes with a Meadow.CLI deploy.

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

## ESP32 Coprocessor in esp-emu (Legacy)

`--esp-emu` (or `coprocessor: esp-emu`) runs the real Meadow-ESP32 firmware in
Espressif's esp-emu, which emulates the chip (radio, timers, SPI, UART) rather
than relying on hand-written peripherals and hooks. esp-emu has no original
ESP32, so the firmware is its ESP32-S3 emulator build. See [UseEspEmu.md](UseEspEmu.md)
for the design and test results.

```
STM32 (NuttX, Renode)                    ESP32-S3 (Meadow firmware, esp-emu)
  espcp_send_data_over_spi ─ EspcpSpiBypass
    │                                      │
  EspEmuBridge (C#) on SPI2 ══ --cosim-tcp ══► spi_slave on SPI3 (CS GPIO 10)
  UART5 (espcp_uart_monitor) ◄═ --uart-tcp ══ UART0: +++RST/RDY/SIR/MW, log
  PF7 (ESP reset) ─────────── EnDrive ──────► EN
                     clock held in 100 µs lockstep
```

Requirements: esp-emu 1.2.0 or later on `PATH` (or `ESP_EMU=path`), and the
firmware built with `cd $MEADOW_EMULATOR_ESP_DIR && ./build.sh --emulator --build`
(`build-esp32s3/merged_flash.bin`; `ESPEMU_FW_PATH` overrides). `run.sh` starts
esp-emu first (log in `build/legacy/esp-emu.log`) and stops it on exit. The
ESP's own UART output is copied to `/tmp/renode-esp-uart.txt`.

The bridge holds the ESP in reset until the OS releases PF7, and waits for the
ESP's `+++SIR` before each exchange, as the OS's SPI lock does on a board (the
shared Legacy hooks skip that lock for the EspcpStub). Meadow.CLI `device info`
reports the coprocessor's firmware version.

Wi-Fi: esp-emu provides a soft AP, `myssid` with password `mypassword`, on a
user-mode (NAT) network, `10.0.2.0/24`, with no host setup. An app that calls
`Connect("myssid", "mypassword")` joins it and reaches the internet through the
host, for both HTTP and HTTPS. Override with `ESPEMU_WIFI_SSID`,
`ESPEMU_WIFI_PASSWORD` and `ESPEMU_NET`, which is passed to esp-emu's `--net`.

## Using Meadow.CLI

The emulator connects via TCP socket instead of USB serial: Renode exposes the
HCOM UART as `socket://localhost:4242`.  Released Meadow.CLI supports socket
routes from **3.0.3**, so the normal global tool works; no source build is
needed.  `./setup.sh` installs it, or updates an older one.

```bash
# Install, or update to the latest release (3.0.3 or later):
dotnet tool install -g WildernessLabs.Meadow.CLI
dotnet tool update -g WildernessLabs.Meadow.CLI
meadow --version                                  # 3.0.3 or later

# Either pass the route on each command (leaves the saved route for a real board alone):
meadow device info -s socket://localhost:4242

# or save it as the default route (persists across sessions):
meadow config route socket://localhost:4242
```

Then with the emulator running (add `-s socket://localhost:4242` unless the
route is saved):
```bash
# Device info
meadow device info

# Deploy and run an app (builds, trims, deploys, enables Mono)
cd "../Meadow.Samples/Source/Meadow F7/Blinky/BlinkyCS"
meadow app run

# Deploy a single file
meadow file write -f path/to/file.dll

# Watch the app's output (for example after booting with run.sh --mount)
meadow listen
```

> **Note:** Older tools (2.x, and 3.0.x before 3.0.3) do not know `socket://`
> routes. The `SocketConnection` class (Meadow.CLI PR #649) handles raw
> HCOM/COBS over TCP, which is how Renode exposes the UART. To work on the CLI
> itself, build it from a `../Meadow.CLI` checkout (override the path with
> `MEADOW_EMULATOR_CLI_DIR`) and run it with
> `dotnet run --project ../Meadow.CLI/Source/Meadow.CLI -- <args>`.

### App Deployment

The `app run` command builds, trims, deploys, and starts a Meadow app:

1. Builds the project (Release config)
2. Trims assemblies using the Meadow linker (removes unused BCL code)
3. Deploys trimmed binaries to the emulator via HCOM file transfer
4. Enables Mono runtime and listens for app output

A full BlinkyCS deploy (36 trimmed files) takes ~2.5 minutes under emulation.
File transfers use a UART RX bypass hook that gives ~30x speedup over the
raw emulated UART path (see [KnownIssues §8.6](KnownIssues_and_Errata.md#86-hcom-file-download--uart-rx-bypass-hack)).

The BlinkyCS sample requires `F7FeatherV2` board declaration (the default for
the legacy configs). The sample has both `V1App` and `V2App` classes — Meadow selects
the correct one based on the device's reported hardware version. An app only
runs if it targets the board the emulator reports (`board:` in the config):
`configs/meadow-os.yaml` emulates an `F7CoreComputeV2`.

## Meadow.OS (Rebase)

The Meadow.OS repository builds the emulator image itself with `build.sh --emulator`
(on `main` since WildernessLabs/Meadow.OS#953): HCOM on UART4 with receive flow
control, no USB, the syslog on USART1 and no bootloader. Its
`Documentation/Emulator.md` describes the OS side.

```bash
# Build (MEADOW_EMULATOR_OS_DIR only if the OS checkout is not ../Meadow.OS or ../Rebase)
MEADOW_EMULATOR_OS_DIR=../Rebase MEADOW_RUNTIME_DIR=../runtime \
  ./build-meadow.os-emulated.sh --config configs/meadow-os.yaml
./run.sh --rebase                      # console: /tmp/renode-uart-meadow.txt

# Talk to it and run Mono's mini tests (the 3.0 CLI, see Using Meadow.CLI)
meadow device info -s socket://localhost:4242
cd ../Rebase && MEADOW_RUNTIME_DIR=../runtime MEADOW_CORE_DIR=<meadow-3.0>/Meadow.Core \
  meadow app run tests/managed/MiniTests -s socket://localhost:4242
```

- **Rebuild the OS and rerun the wrapper together.** Hook addresses are taken from
  the ELFs, so they are only valid for the build they came from. After a normal
  hardware build in the same OS checkout, build with `build.sh --emulator --clean`.
- **Memory layout** is the same as Legacy: vectors at `0x08040000`, user code at
  `0x08040200`, kernel at `0x08140000`. The wrapper loads split binaries, and
  `nuttx_mono.bin` (the `.mono*` sections from `0xC0000000`, signature first) goes
  into QSPI and SDRAM.
- **Hooks that differ from Legacy** (all generated by `tools/extract-hook-addresses.py`,
  chosen by `EMU_OS`): the QSPI DMA bypass parses the NuttX 12 `struct qspi_meminfo_s`
  (layout 1); `qspi_waitstatusflags_timed()` returns at once; the hardware-version hook
  writes `_meadowVersionKnown` / `_meadowVer` and returns the version in R0;
  `wdog_skip` is on `hcom_host_watchdog_worker()`; `mono_ver_skip` and `mono_str_fix`
  are not used (the firmware reads the real Mono signature from QSPI, and
  `mono_str_fix` writes Legacy struct offsets).
- **Deploy speed:** a 225 KB file takes a few seconds; MiniTests (about 70 trimmed
  assemblies) deploys and runs in a few minutes. No UART RX bypass is needed.

## Prerequisites

The `setup.sh` script handles all of this automatically. For manual setup:

- **.NET SDK 8.0+** — `dotnet` must be on PATH
- **Meadow.CLI 3.0.3+** — `dotnet tool install -g WildernessLabs.Meadow.CLI` (socket routes for the emulator)
- **ARM toolchain** — `arm-none-eabi-gcc` (install via `brew install --cask gcc-arm-embedded`)
- **GNU Make 4.x+** — macOS ships Make 3.81; install `gmake` via `brew install make`

### Required Sibling Repos

```
wilderness_labs/
├── Meadow.OS.Emulator/    # This repo
├── Meadow/                # Legacy NuttX-based Meadow OS (STM32 firmware source)
├── Meadow.OS/             # New Meadow.OS rebase (updated NuttX, unreleased)
├── runtime/               # .NET 10 Mono runtime (built by Legacy meadow-3.0 and Meadow.OS)
├── Meadow-ESP32/          # ESP32 firmware source (for --esp32 mode, ESP-IDF 4.2.4)
├── Meadow.CLI/            # Optional: only to work on the CLI (use the 3.0.3+ global tool)
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
├── build-meadow.os-emulated.sh  # Builds NuttX firmware (legacy, meadow-os)
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
│   ├── meadow-os.resc              # Meadow.OS (Rebase)
│   ├── meadow-dual.resc            # Dual-machine: STM32 + ESP32
│   ├── meadow-legacy-headless.resc # Legacy OS — headless (recommended)
│   ├── meadow-legacy.resc          # Legacy OS — interactive
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
./build-meadow.os-emulated.sh --config configs/legacy-ccm.yaml         # Build Legacy meadow-3.0 (CCM)
./build-meadow.os-emulated.sh --config configs/meadow-os.yaml --clean  # Clean rebuild (passes --clean to the OS build.sh)
```

The build script reads `EMU_OS` from the config to select the source tree (`Meadow/` for legacy, `Meadow.OS/` for rebase), delegates the build to that tree's `build.sh --emulator` (which applies the emulator-specific kconfig tweaks), splits the images for Renode and extracts hook addresses. Output directories: `build/legacy/` or `build/`.

### ESP32 — required only for `--esp32` mode

```bash
cd ../Meadow-ESP32
source ~/esp/esp-idf-v4.2.4/export.sh     # Activate ESP-IDF toolchain
./build.sh                                  # Build MeadowComms.elf
```

Requires ESP-IDF 4.2.4 with the Xtensa toolchain. Output: `Meadow-ESP32/build/MeadowComms.elf`. The `run.sh --esp32` script auto-discovers this ELF in the sibling repo.

## GDB Debugging

`run.sh --gdb` starts Renode's GDB server on port 3333 (`--gdb=PORT` for
another port; a non-zero `ports.gdb` in the config does the same without the
flag).  The CPU waits at the reset entry point before running any firmware, so
breakpoints can be set first; GDB's `continue` starts it.

```bash
./run.sh --config=configs/meadow-os.yaml --gdb       # or configs/legacy-ccm.yaml
# In another terminal (build/legacy/nuttx.elf for Legacy):
arm-none-eabi-gdb build/nuttx.elf
(gdb) target remote :3333
(gdb) break lfs_mount
(gdb) continue
```

- Works with `meadow-os.resc` and the single-machine Legacy scripts
  (`meadow-legacy-headless`, `meadow-legacy`), which
  include the `gdb.resc` that `run.sh` writes on every run.  `run.sh` refuses
  `--gdb` for scripts without it, such as `meadow-dual`.
- After a `detach` the firmware carries on as normal (Meadow.CLI answers).
- A `~/.gdbinit` dashboard can interfere with batch use (`gdb -batch -ex ...`);
  add `-nx` to skip it.

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

The WildernessLabs/renode fork (`meadow-emulator-fixes` branch) contains 18 fixes across two areas. See [upstream_fixes.md](upstream_fixes.md) for PR status.

### STM32/ARM (3 fixes in Infrastructure)

| Fix | Impact |
|-----|--------|
| SVC/PRIMASK handling in NVIC | ARM spec says SVC with PRIMASK=1 should escalate to HardFault, not be silently skipped. Without this fix, per-address hooks on every SVC site are needed (~12,000x slower) |
| RTC backup register count (20→32) | STM32F4_RTC `BackupEnd` only covers 20 registers; STM32F7 has 32 (BKP0R–BKP31R). Meadow uses BKP31R for runtime state persistence |
| BackendTerminal batched write (`da6e499cb`) | The batched UART write path wrote zero bytes for the rest of a batch, and delivered the real bytes later out of order, if the UART reported Full part of the way through. It now writes only the bytes it takes from the buffer. See [KnownIssues §9.7](KnownIssues_and_Errata.md#97-renode-fork-backendterminalwritebuffertouart-zero-fill) |

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
  1.16.1. File deploy via Meadow.CLI works but is very slow without the bypass
  on Legacy. Meadow.OS does not need it (UART4 flow control and an hcom fix,
  §9.5): a 225 KB file takes a few seconds.
- **Meadow.OS (§9)** — the shared Legacy hooks are filtered for Meadow.OS by
  `EMU_OS`; check what any other Legacy hook writes before enabling it there.
- **Flash persistence (§4.4)** — QSPI flash is volatile across Renode sessions.
  A pre-built LittleFS image is loaded at boot so the filesystem is pre-populated,
  and deployed files persist across in-session resets, but everything is lost when
  Renode exits.
