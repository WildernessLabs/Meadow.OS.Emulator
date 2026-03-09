# Plan: Emulator Support for Production Meadow OS (Legacy NuttX)

## Track ID: `renode-legacy-os`
## Status: `ready`
## Repo: `Meadow.OS.Emulator`

---

## 1 — Build Script for Legacy Repo

- [ ] Create `build-legacy.sh` based on `build-emulator.sh`
- [ ] Point `MEADOW_OS_DIR` to `../Meadow` instead of `../Meadow.OS`
- [ ] Use legacy config path: `stm32f777zit6-meadow/mono` (slash separator)
- [ ] Adapt kconfig-tweak calls for legacy NuttX config option names
- [ ] Handle legacy shared header path (`nuttx/include/meadow/hcom_nuttx_shared.h`)
- [ ] Handle legacy toolchain path (`Meadow/toolchain/macos/`)
- [ ] Output artifacts to `build/legacy/` (separate from `build/`)
- [ ] Verify `make olddefconfig` works with legacy NuttX version

## 2 — Verify Emulator Config Tweaks on Legacy

- [ ] Compare legacy `mono/defconfig` with new `meadow/defconfig` — identify any option name differences
- [ ] Verify USB-related config options exist in legacy (CONFIG_CDCACM, CONFIG_USBDEV, etc.)
- [ ] Verify UART4 config options work in legacy NuttX
- [ ] Verify CONFIG_HCOM_COMMS_DEVICE_NAME exists in legacy codebase
- [ ] Test build completes successfully

## 3 — Renode Launch Scripts for Legacy

- [ ] Create `scripts/meadow-legacy.resc` — interactive mode
- [ ] Create `scripts/meadow-legacy-headless.resc` — headless mode
- [ ] Load ELF/bin from `build/legacy/` paths
- [ ] Same `.repl` platform definition (same SoC)
- [ ] HCOM socket terminal on port 4242 (same as new)

## 4 — IronPython Hook Extraction for Legacy

- [ ] Build legacy NuttX and extract ELF
- [ ] Find equivalent hook addresses for:
  - SVC-with-PRIMASK fix (7 addresses in new build)
  - GPIO version detection fix
  - QUADSPI TCF skip
  - QUADSPI DMA bypass
  - LSI calibration skip
- [ ] Create `scripts/pydev/hooks_legacy.py` or parameterize existing hooks
- [ ] Some hooks may not be needed if legacy code paths differ

## 5 — Build/Launch Selector

- [ ] Update `run.sh` to accept a `--legacy` flag (or `--os legacy|new`)
- [ ] Route to correct build artifacts and Renode scripts
- [ ] Document usage in README or inline help
- [ ] Example: `./run.sh --legacy` vs `./run.sh` (default = new)

## 6 — End-to-End Verification

- [ ] Build legacy OS with `build-legacy.sh`
- [ ] Boot in Renode with `meadow-legacy-headless.resc`
- [ ] Verify boot reaches idle (or as far as possible)
- [ ] Test HCOM via `hcom_test.py` on port 4242
- [ ] Test Meadow.CLI `device info -s socket://localhost:4242`
- [ ] Compare behavior with new OS emulator

---

## Implementation Notes

- The `.repl` platform file should be shared — both repos target identical hardware
- Hook addresses WILL be different — the legacy binary has different symbol layouts
- The legacy NuttX may have different driver implementations that need different workarounds
- Start with build + boot, discover what hooks are needed empirically
- The legacy `build.sh` has useful reference for version injection, test config, etc.

## Decision Log

| Date | Decision | Rationale |
|------|----------|-----------|
| 2026-03-09 | Separate build script, not parameterized | Keeps legacy/new paths clean, avoids brittle conditionals |
| 2026-03-09 | Separate Renode scripts | Hook addresses differ per binary, can't share |
| 2026-03-09 | Shared `.repl` platform | Same SoC/board — no hardware differences |
| 2026-03-09 | `build/legacy/` output dir | Clear separation of artifacts |
