# Plan: Emulator Configuration

## Track ID: `emu-config`
## Status: `complete`
## Priority: `medium`

---

## Phase 1 — Config File & Parser

### E.1 — Define config schema and create default file
- **What:** Create `emulator.config.yaml` at repo root with all settings and
  inline documentation. Create a shell helper (`scripts/read-config.sh`) that
  parses the YAML and exports values as shell variables.
- **Why:** Single source of truth. Shell scripts can't parse YAML natively, so
  we need a thin parser (python3 one-liner or a dedicated helper).
- **Approach:** Use `python3 -c "import yaml; ..."` for parsing — python3 and
  PyYAML are already required for `extract-hook-addresses.py`. The helper
  script sources into `run.sh` and build scripts.
- [x] Create `configs/` directory with pre-built config files:
  - `configs/legacy.yaml` — Legacy OS, F7FeatherV2, dual proc machine
  - `configs/meadow-os.yaml` — New rebase OS, F7FeatherV2, dua proc machine
  Each has commented-out alternatives for every setting.
- [x] Copy `configs/legacy.yaml` to `emulator.config.yaml` at repo root as the
      active default
- [x] Create `scripts/read-config.sh` that exports `EMU_OS`, `EMU_BOARD`,
      `EMU_COPROCESSOR`, `EMU_HCOM_PORT`, `EMU_MONITOR_PORT`, etc.
      Accepts optional config path arg: `source scripts/read-config.sh [path]`
- [x] Verify parser works with each pre-built config

### E.2 — Update run.sh to read config
- **What:** `run.sh` sources `read-config.sh`, uses config values for defaults,
  and lets CLI args override them. Remove hardcoded script selection logic.
- **Why:** `run.sh` currently has `if legacy && esp32 then dual; elif legacy then
  headless; else meadow-os` — this should come from config.
- [x] Source `read-config.sh` at top of `run.sh`
- [x] Map config values to script selection:
  - `os=legacy, coprocessor=stub` → `meadow-legacy-headless`
  - `os=legacy, coprocessor=dual-machine` → `meadow-dual`
  - `os=meadow-os, coprocessor=stub` → `meadow-os-headless`
  - `headless=false` → drop `-headless` suffix
- [x] CLI args (`--legacy`, `--esp32`, `--port=N`) override config values
- [x] `--config=path` arg to specify alternate config file (e.g., `--config emulator.config.dual.yaml`)
- [x] Verify: `./run.sh` with default config matches current `./run.sh --legacy`
- [x] Verify: `./run.sh --config emulator.config.dual.yaml` selects dual-machine mode

### E.3 — Update build scripts to read config
- **What:** Build scripts read `os` and `build.defconfig` from config instead
  of positional args.
- **Why:** Currently `build-meadow.os_legacy-emulated.sh mono` takes defconfig
  as arg. Config file should be the default, with arg as override.
- [x] `build-meadow.os_legacy-emulated.sh` reads config for defconfig name
- [x] `build-meadow.os-emulated.sh` reads config for defconfig name
- [x] CLI arg still overrides config value
- [x] Verify: build with default config produces same output

## Phase 2 — Board Selection

### E.4 — Map board type to GPIO version hook
- **What:** The `gpio_ver_fix` hook in `.resc` scripts hardcodes the board
  version value (2=F7FeatherV2, 3=F7CoreComputeV2). Make this configurable.
- **Why:** Changing boards currently requires editing the `.resc` template,
  regenerating hooks, and hoping you got the right value.
- **Current mapping:**
  - Legacy: 1=F7FeatherV1, 2=F7FeatherV2, 3=F7CoreComputeV2
  - New OS: 0x01=CCMv2 (single value, different scheme)
- **Approach:** `extract-hook-addresses.py` reads config and injects the board
  version into the generated `hooks.resc`. Or: `run.sh` passes a Renode variable
  (`set board_version N`) before including the script.
- [x] Add board version mapping to `read-config.sh`
- [x] Pass board version to Renode via `--execute "set emu_board_version N"`
- [x] Update `meadow-legacy-hooks.resc` to use `$emu_board_version` in gpio_ver_fix
- [x] Update `extract-hook-addresses.py` to emit the variable reference
- [x] Verify: F7FeatherV2 config boots with version=2 in device info
- [x] Verify: F7CoreComputeV2 config boots with version=3

### E.5 — Board-specific platform differences (if any)
- **What:** Determine if F7CoreComputeV2 needs different `.repl` or peripherals.
- **Why:** Different boards may have different pin mappings, memory sizes, etc.
- [x] Audit F7FeatherV2 vs F7CoreComputeV2 hardware differences
- [x] Document which platform aspects differ (if any)
- [x] Create board-specific `.repl` overlays if needed

## Phase 3 — Polish

### E.6 — Config validation and error messages
- **What:** `read-config.sh` validates config values and prints clear errors.
- [x] Validate `os` is `legacy` or `meadow-os`
- [x] Validate `board` is a known board type
- [x] Validate `coprocessor` is `stub` or `dual-machine`
- [x] Validate ports are numeric and in range
- [x] Print human-readable errors with valid options

### E.7 — Documentation
- **What:** Update README.md with config file usage.
- [x] Document config file location and format
- [x] Document CLI override behavior
- [x] Add examples for common configurations

---

## Decision Log

| Date       | Decision | Rationale |
|------------|----------|-----------|
| 2026-03-18 | Created track | User identified need for configurable board selection and unified build/launch config |
| 2026-03-18 | YAML format (not TOML/JSON) | Matches Meadow ecosystem (meadow.config.yaml) and is human-editable with comments |
| 2026-03-18 | Config at repo root | Easy to find, matches convention (like docker-compose.yaml) |
| 2026-03-18 | Shell helper using python3 | python3 + PyYAML already required; avoids adding yq/jq dependency |
| 2026-03-18 | Check in config, commented alternatives | Users get working defaults on clone; uncomment to switch settings |
| 2026-03-18 | Multiple named profiles via `--config` | Quick switching between configs without editing the default file |
| 2026-03-19 | Pre-built config files in `configs/` dir | Common setups (legacy, legacy-dual, meadow-os, etc.) should be one `cp` away, not manual editing |
