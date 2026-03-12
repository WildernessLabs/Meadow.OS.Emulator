# Meadow.OS Emulator

Full hardware emulation of Meadow.OS (NuttX RTOS on STM32F777ZIT6) using the [Renode](https://renode.io) framework. Runs Meadow.NET apps (Mono JIT) at near real-time speed.

## Current Status

The legacy Meadow firmware boots with Mono JIT, loads .NET assemblies from LittleFS, and runs Meadow apps end-to-end. A Blinky sample app has been validated running its full lifecycle (`Initialize` -> `Run` -> color cycling loop).

## Prerequisites

- **.NET SDK 8.0+** — `dotnet` must be on PATH (macOS: `brew install dotnet`)
- **ARM toolchain** — `arm-none-eabi-gcc` (install via `brew install --cask gcc-arm-embedded`)
- **GNU Make 4.x+** — macOS ships Make 3.81; install `gmake` via `brew install make`

### Required Sibling Repos

These repos must be checked out as siblings to `Meadow.OS.Emulator/`:

```
wilderness_labs/
├── Meadow.OS.Emulator/    # This repo
├── Meadow/                # Legacy NuttX-based Meadow OS firmware source
├── Meadow.CLI/            # Branch: feature/TCP_Socket_Connection_for_EMU
├── Meadow.Samples/        # Sample apps (Blinky, etc.)
└── renode/                # WildernessLabs/renode fork (branch: meadow-emulator-fixes)
```

### Building the Custom Renode Fork

The emulator requires the [WildernessLabs/renode](https://github.com/WildernessLabs/renode) fork which includes critical fixes for SVC/PRIMASK handling in the NVIC and STM32F4 RTC backup register support. Without these fixes, emulation is ~12,000x slower.

```bash
cd ../renode
git checkout meadow-emulator-fixes
git submodule update --init src/Infrastructure
export PATH="/usr/local/share/dotnet:$PATH"
bash build.sh --host-arch aarch64 --skip-fetch   # Apple Silicon
# bash build.sh --skip-fetch                     # Intel / Linux
```

## Project Structure

```
Meadow.OS.Emulator/
├── build/legacy/             # Build output (legacy OS)
├── build-meadow.os_legacy-emulated.sh  # Builds legacy Meadow with emulator tweaks
├── run.sh                    # Launches Renode (auto-finds custom fork)
├── platforms/
│   ├── stm32f777.repl        # STM32F777 chip-level platform
│   └── meadow-f7.repl        # Meadow F7 board (chip + SDRAM + QSPI flash)
├── peripherals/              # C# extensions (loaded at runtime)
│   ├── Winbond_W25QXXXJV.cs  # 64MB QSPI NOR flash
│   ├── QspiDmaBypass.cs      # QSPI DMA bypass — direct memcpy via SystemBus
│   └── MqReceiveIntercept.cs  # ESPCP mq_receive interception
├── scripts/
│   ├── meadow-legacy-headless.resc  # Legacy OS — headless (recommended)
│   ├── meadow-legacy.resc          # Legacy OS — interactive
│   ├── meadow-legacy-debug.resc    # Legacy OS — with GDB server
│   └── ...                         # Other scripts (new OS, diag, etc.)
├── tools/
│   ├── build_lfs_v1_image.c  # LittleFS v1 image builder
│   └── hcom_test.py          # HCOM protocol test tool
├── upstream_fixes.md         # Tracker for fixes to upstream Renode
└── conductor/                # Project planning and track documents
```

## Building the Firmware

```bash
./build-meadow.os_legacy-emulated.sh
```

Builds from the legacy `Meadow/` repo with emulator kconfig tweaks. Outputs split binaries to `build/legacy/`. Requires ARM GCC 10.3 at `/Applications/ARM/bin`.

## Running

```bash
./run.sh --legacy                         # Default: headless mode, monitor on port 1234
./run.sh --legacy meadow-legacy           # Interactive mode
./run.sh --legacy meadow-legacy-debug     # GDB mode (port 3333)
./run.sh --legacy --port=5555             # Custom monitor port
```

`run.sh` auto-detects the custom Renode fork in `../renode/`, falling back to PATH or `/Applications/Renode.app`.

On startup, the script prints connection info:
```
=== Starting Renode with scripts/meadow-legacy-headless.resc ===
Monitor: telnet localhost 1234
HCOM:    socket://localhost:4242
```

### Using Meadow.CLI

Connect Meadow.CLI to the emulator via TCP socket:

```bash
# From the Meadow.CLI build output directory:
export PATH="/usr/local/share/dotnet:$PATH"
dotnet meadow.dll listen -s socket://localhost:4242     # Live output
dotnet meadow.dll device info -s socket://localhost:4242
dotnet meadow.dll app deploy -s socket://localhost:4242
```

Or configure the route once:
```bash
dotnet meadow.dll config route socket://localhost:4242
dotnet meadow.dll device info    # Uses saved route
```

### Deploying an App

Two options:

1. **Via Meadow.CLI** (normal workflow):
   ```bash
   dotnet meadow.dll app deploy -s socket://localhost:4242
   ```

2. **Via pre-populated LFS image** (faster, bypasses HCOM transfer):
   ```bash
   ./build_lfs_v1_image build/legacy/assemblies build/legacy/littlefs.bin
   # Image is loaded automatically by the .resc script via sysbus LoadBinary
   ```

### GDB Debugging

```bash
./run.sh --legacy meadow-legacy-debug
# In another terminal:
arm-none-eabi-gdb build/legacy/nuttx.elf
(gdb) target remote :3333
(gdb) continue
```

## Renode Fork Fixes

The WildernessLabs/renode fork (`meadow-emulator-fixes` branch) contains two critical fixes. See `upstream_fixes.md` for full details.

| Fix | File | Impact |
|-----|------|--------|
| SVC/PRIMASK handling | `NVIC.cs` | Renode silently skipped SVCall when PRIMASK was set. Per ARMv7-M ARM B1.5.14, SVC is synchronous and should not be masked. Without this fix, per-address hooks on every SVC site are needed, destroying performance (12,000x slower). |
| RTC backup register count | `STM32F4_RTC.cs` | Extended from 20 to 32 registers (`BackupEnd = 0x9C` -> `0xCC`) to support STM32F7. Meadow uses BKP31R at offset 0xCC for runtime state persistence across resets. |

## Firmware Hooks

The emulator uses address-specific hooks to work around unimplemented peripherals and firmware bugs. Hook addresses are binary-specific and must be re-scanned after firmware rebuild. See the `.resc` script for the current hook list.

Key workarounds:

| Issue | Impact |
|-------|--------|
| QSPI DMA not driven by Renode | LittleFS mount fails without bypass |
| GPIO board version detection | Boot stalls; hook forces F7FeatherV2 |
| ESPCP SPI DMA/locking | ESPCP init hangs without bypass |
| Syslog semaphore leak (firmware bug) | Deadlocks all Mono threads on malloc failure |
| Download watchdog (wall time vs virtual time) | File transfers fail without bypass |
| LSI clock calibration thread | CPU burn without skip |

## Wall Time vs. Virtual Time

Renode runs in virtual time. Any firmware timeout guarding external I/O (HCOM downloads, WiFi, BLE) can misfire because the emulated CPU burns virtual seconds while waiting for data that arrives in wall-time milliseconds. Each such timeout needs a skip hook. See the [plan](conductor/tracks/renode-mono/plan.md) for details.

## Known Issues

See [KnownIssues_and_Errata.md](KnownIssues_and_Errata.md) for a full list. Key items:

- **MemFault on `runtime disable` -> `runtime enable`** — first boot works, re-enable crashes. Non-blocking.
- **ESPCP polling disabled** — needed for WiFi/BLE, must re-enable without CPU starvation.
- **Deploy throughput** — HCOM over UART is byte-by-byte. Pre-populated LFS image is the fast path.
