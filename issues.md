# Issues Raised This Session

## 1. Sibling-repo location handling was inconsistent and incomplete

- `setup.sh` and `run.sh` each let `--legacy`/`--rebase` take an optional `[DIR]` argument, with
  their own separate copy of a sibling-directory auto-detect fallback chain.
- The two build scripts (`build-meadow.os-emulated.sh`, `build-meadow.os_legacy-emulated.sh`)
  didn't accept `--legacy`/`--rebase` at all and hardcoded `../Meadow` / `../Meadow.OS` directly.
- Two other sibling repos — `Meadow.CLI` and `Meadow.Samples` — had no override mechanism at all;
  `setup.sh` hardcoded `../Meadow.CLI` and `../Meadow.Samples`.
- **Latent bug found:** `setup.sh`'s rebase-mode fallback checked `../Meadow.OS` before
  `../Rebase`, while `run.sh`'s independent copy checked `../Meadow` before `../Rebase` — the two
  scripts could resolve to *different* OS repos on a machine with both `../Meadow` and
  `../Meadow.OS`-style directories present.

**Status: Fixed.** Replaced with four env vars (`MEADOW_EMULATOR_OS_DIR`,
`MEADOW_EMULATOR_CLI_DIR`, `MEADOW_EMULATOR_ESP_DIR`, `MEADOW_EMULATOR_SAMPLES_DIR`) resolved
once by a new shared `scripts/resolve-locations.sh`, sourced by `setup.sh`, `run.sh`, and both
build scripts. `--legacy`/`--rebase` are now pure mode switches — a trailing `DIR` argument now
errors explicitly instead of being silently accepted or discarded. Documented in README.md.

## 2. Wrong build script used against the Rebase (Meadow.OS) repo

- Running `build-meadow.os_legacy-emulated.sh` against the `Rebase` sibling directory failed with:
  ```
  ERROR: Mono runtime libraries not found at .../Rebase/mono/mono/sgen/.libs/
  ```
- Root cause: that script *only* builds the legacy Mono-based protected-mode firmware — it
  unconditionally requires the legacy `mono/mono/sgen/.libs/libmonosgen.a` submodule structure,
  which `Rebase` does not have (it builds Mono differently, via its own `build.sh -m`/`--mono`
  flag, not as a checked-in submodule).
- `build-meadow.os-emulated.sh` (the other build script) already has **no** Mono requirement when
  building for `meadow-os`/Rebase — nothing needed to change there.

**Status: Resolved** — use `build-meadow.os-emulated.sh --config configs/meadow-os.yaml` for
Rebase builds, not `build-meadow.os_legacy-emulated.sh`.

## 3. `configs/meadow-os.yaml` pointed at a nonexistent NuttX defconfig

- `build.defconfig: meadow_os_emu` did not correspond to any directory under
  `Rebase/nuttx/boards/arm/stm32f7/stm32f777zit6-meadow/configs/` — only `meadow_os` exists there
  (alongside `nsh`, `sdram`, `f7corecomp`, `projectlab`, `i2s`, `dualcdcacm`, `usbnsh`).
- Separately, the entire `build:` block in `configs/meadow-os.yaml` had been commented out during
  troubleshooting, which caused `read-config.sh` to silently fall back to its own hardcoded
  default of `defconfig: mono` (a legacy-only defconfig name), producing:
  ```
  Directory for stm32f777zit6-meadow:mono does not exist.
  ```
- Confirmed `Rebase`'s `meadow_os` defconfig has Mono explicitly disabled
  (`# CONFIG_EXAMPLES_MONO=y`, commented, with a note "Leaving here as a reference for when Mono
  is integrated") — so it's the correct, currently Mono-free config for this build.

**Status: Fixed.** `configs/meadow-os.yaml` now has `build.defconfig: meadow_os` (uncommented).
Verified via `read-config.sh` that `EMU_DEFCONFIG` resolves to `meadow_os`, and that directory
exists in `Rebase`. Not yet verified end-to-end (i.e. that the full NuttX build completes past
the configure step) — awaiting the next build attempt.

## 4. `read-config.sh`'s hardcoded default doesn't account for OS mode

- Noted in passing: `read-config.sh`'s Python default for `build.defconfig` is unconditionally
  `"mono"`, regardless of `os:` (legacy vs. meadow-os). If a future config omits `build.defconfig`
  again, the same silent-fallback-to-`mono` failure mode in Issue 3 will recur for meadow-os
  builds.

**Status: Open / not fixed** — not addressed this session. Worth a follow-up: make the Python
default OS-aware, or require `build.defconfig` to be explicit and error if missing rather than
silently defaulting.

## 5. Claude Code auto-update check

- `claude doctor` reported `Last update attempt: failed (install_failed)`.
- Ran `claude update` — reported already up to date at 2.1.220.

**Status: Resolved** (transient; no action needed).
