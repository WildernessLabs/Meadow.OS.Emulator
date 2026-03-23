# Specification: Emulator Configuration

## Track ID
`emu-config`

## Problem Statement
The emulator's build and launch configuration is scattered across CLI arguments
(`run.sh --legacy --esp32`), hardcoded values in `.resc` scripts (board version,
memory addresses, port numbers), and implicit conventions (sibling directory
layout, binary paths). This makes it difficult to:

1. Switch between board types (F7FeatherV2 vs F7CoreComputeV2)
2. Toggle coprocessor mode (stubbed vs dual-machine ESP32)
3. Switch between OS variants (legacy vs new Meadow.OS rebase)
4. Reproduce a specific configuration across machines
5. Understand what the emulator is configured to do at a glance

There is no single source of truth for "what am I emulating right now?"

## Goal
Create a YAML configuration file (`emulator.config.yaml`) that is the single
source of truth for all build and launch settings. The build and run scripts
read this file instead of relying on CLI flags and hardcoded values.

Users should be able to:
- Edit one file to change what they're emulating
- Override config values via CLI args (config file is the default, CLI wins)
- Check the config file into version control for reproducibility

## Target Config Structure

```yaml
# emulator.config.yaml — Meadow.OS Emulator configuration

# Which OS variant to build and run
os: legacy                    # legacy | meadow-os

# Board to emulate
board: F7FeatherV2            # F7FeatherV2 | F7CoreComputeV2

# ESP32 coprocessor mode
coprocessor: stub             # stub | dual-machine

# Build configuration
build:
  defconfig: mono             # NuttX defconfig name (legacy: mono | meadow-os: meadow)

# Network ports
ports:
  hcom: 4242                  # HCOM TCP socket
  monitor: 1234               # Renode monitor telnet
  esp32ctrl: 4243             # ESP32 control channel
  gdb: 3333                   # GDB server (0 = disabled)

# Debug options
debug:
  console_log: /tmp/renode-uart-legacy.txt   # UART console output file
  headless: true              # true = no GUI analyzer windows
```

## Scope
- Create the YAML config file with sensible defaults
- Update `run.sh` to read config (with CLI overrides)
- Update build scripts to read config
- Update `.resc` script selection based on config
- Map board type to the correct GPIO version hook value
- Map coprocessor mode to script selection (headless vs dual)

## Out of Scope
- Dynamic `.repl` platform generation (keep static platform files)
- Per-peripheral configuration (hook addresses remain auto-extracted from ELF)
- Renode monitor scripting changes (hooks stay in `.resc` files)
- New board support (just parameterize existing F7FeatherV2 path; add boards later)

## Dependencies
- `workaround-cleanup` track (mostly complete — hooks and scripts are stable)
- `run.sh` and build scripts must remain functional during migration

## Success Criteria
- [ ] `emulator.config.yaml` exists with documented defaults
- [ ] `run.sh` reads config; `--legacy`, `--esp32` still work as overrides
- [ ] Build script reads `os` and `board` from config
- [ ] Changing `board: F7CoreComputeV2` in config produces correct GPIO version hook value
- [ ] Changing `coprocessor: dual-machine` selects the dual-machine `.resc` script
- [ ] Changing `os: meadow-os` selects the new OS build + script

## Decisions
- **Checked in, not gitignored.** All config files are committed to the repo so
  cloning gives a working setup out of the box.
- **Commented alternatives.** Each setting shows alternate values as commented
  lines that users can uncomment to switch:
  ```yaml
  os: legacy
  # os: meadow-os

  board: F7FeatherV2
  # board: F7CoreComputeV2
  ```
- **Pre-built config files for common setups.** Each represents a real use case:
  ```
  configs/
  ├── legacy.yaml              # Legacy OS, F7FeatherV2, stub ESP32 (default)
  ├── legacy-dual.yaml         # Legacy OS, F7FeatherV2, real ESP32 coprocessor
  ├── legacy-corecompute.yaml  # Legacy OS, F7CoreComputeV2, stub ESP32
  ├── meadow-os.yaml           # New rebase OS, F7FeatherV2, stub ESP32
  └── meadow-os-dual.yaml      # New rebase OS, F7FeatherV2, real ESP32
  ```
- **Default config via symlink or convention.** `emulator.config.yaml` at the
  repo root is the active config. Users can either edit it directly or copy
  one of the pre-built configs:
  ```bash
  cp configs/legacy-dual.yaml emulator.config.yaml
  ./run.sh                                            # uses emulator.config.yaml
  ./run.sh --config configs/meadow-os.yaml            # one-off override
  ```
- **CLI args still override.** `--legacy`, `--esp32`, `--port=N` override
  whatever the config file says.
