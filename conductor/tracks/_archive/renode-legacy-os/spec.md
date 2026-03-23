# Spec: Emulator Support for Production Meadow OS (Legacy NuttX)

## Problem

The emulator currently targets the newer `Meadow.OS` repo (NuttX 12), which is a work in progress — the coprocessor doesn't come up yet. The production/shipping Meadow OS lives in the older `Meadow` repo (NuttX 7.x era). To be useful for real development and testing, the emulator needs to work with the production codebase.

Additionally, there's no mechanism to switch between the two codebases. Developers need to be able to choose which Meadow OS version to build and run in the emulator.

## Repositories

| Repo | Path | NuttX | Config Style | Default Config | Status |
|------|------|-------|-------------|----------------|--------|
| `Meadow.OS` (new) | `../Meadow.OS/` | 12.x | `boards/arm/stm32f7/.../configs/` with `:` separator | `meadow` | WIP — coprocessor not working |
| `Meadow` (legacy) | `../Meadow/` | 7.x | `configs/stm32f777zit6-meadow/` with `/` separator | `mono` | Production/shipping |

## Solution

1. Create a separate build script (`build-legacy.sh`) for the legacy `Meadow` repo
2. Create corresponding launch scripts (`meadow-legacy.resc`, `meadow-legacy-headless.resc`)
3. Both build scripts produce artifacts in `build/` with distinguishing names or subdirectories
4. The same Renode platform definition (`.repl`) and IronPython hooks should work for both — they target the same STM32F777ZIT6 hardware

## Key Differences to Handle

### Build System
- Legacy uses `configs/BOARD/CONFIG/` path with `/` separator in configure.sh
- Legacy default config is `mono` (not `meadow_os`)
- Legacy bundles toolchain in `/toolchain/macos/`
- Legacy shared headers are in `nuttx/include/meadow/` (not `Meadow.OS/include/meadow/`)
- Legacy version header: `nuttx/include/meadow/hcom_nuttx_shared.h`

### kconfig-tweak Adjustments
- Same emulator tweaks apply (disable USB, enable UART4, HCOM to /dev/ttyS1)
- Config option names may differ between NuttX versions — need to verify
- Legacy defconfig may have different defaults for console/syslog

### Renode Scripts
- Same `.repl` platform definition (same SoC, same memory map)
- IronPython hook addresses WILL differ (different binary → different symbol addresses)
- Hook addresses must be extracted from the legacy ELF (via `nm` or `objdump`)
- May need different/fewer hooks depending on what code paths the legacy build takes

## Constraints

- Must not break the existing Meadow.OS (new) build flow
- Build artifacts should be clearly separated to avoid confusion
- Renode scripts should make it obvious which OS version they're running
