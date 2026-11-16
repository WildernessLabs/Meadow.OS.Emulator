# Move emulator build support into Legacy/build.sh and Rebase/build.sh

## Context

Today the emulator repo hand-rolls its own NuttX build sequence in
`build-meadow.os_legacy-emulated.sh` and `build-meadow.os-emulated.sh` — reimplementing steps
(`configure.sh`, `make context`/`pass2`/`pass1deps`/`pass1`, mbedtls, version-placeholder
injection) that already exist in `Legacy/build.sh` and `Rebase/build.sh`. This duplication has
already produced real bugs:

- The emulator's version-injection `SHARED_HEADER` path for meadow-os builds
  (`$MEADOW_SRC_DIR/Meadow.OS/include/meadow/hcom_nuttx_shared.h`) is wrong — Rebase's actual
  header lives at `F7MeadowOS/include/meadow/hcom_nuttx_shared.h`. Because the `grep -q` guard
  silently no-ops on a missing file, meadow-os builds have been getting placeholder version
  strings baked in without any error.
- `build-meadow.os_legacy-emulated.sh` requires the user to manually pre-build Mono
  (`cd Legacy && ./build-mono.sh`) and hard-errors if it's missing, even though `Legacy/build.sh`
  already builds Mono itself as a normal part of its flow.

Since you own `Legacy` and `Rebase`, the proposal is to add a `--emulator` flag to both
repos' `build.sh`, move the emulator-only kconfig tweaks into that flag, skip the bootloader
build under it, and let the emulator repo's build scripts shrink to thin wrappers: call
`build.sh --emulator`, then do only the Renode-specific packaging (ELF splitting, hook-address
extraction) that has no business living in a production OS build script.

**This document was written as a plan for review.** The status sections below record what was
done; the 2026-11-14 update supersedes them for Rebase.

> **Status update, 2026-11-14 (Rebase).** The July `SupportEmulator` branch was 1205 commits
> behind Rebase `main`, so its change was redone on the current `main` as branch
> **`SupportEmulatorV2`** (the original branch is left as it was). On that branch:
>
> - `build.sh --emulator` applies the tweaks through `enable_emulator()` in
>   `scripts/configuration_tweaks.sh` (the same pattern as `--nsh` / `--dualcdc`) rather than
>   inline `kconfig-tweak` calls, and no longer runs `olddefconfig`. It also sets
>   `SYSLOG_CONSOLE`/`SYSLOG_REGISTER` (needed in NuttX 12) and UART4 receive flow control.
> - **Part 3 item 6 is now verified:** `./run.sh --rebase` boots in the WildernessLabs Renode
>   fork, Meadow.CLI `device info` answers over `socket://localhost:4242`, apps deploy, and Mono
>   runs Rebase's `tests/managed/MiniTests` (733 ran, 2 skipped, 1 known failure, the same as on
>   a board).
> - Getting there needed further emulator changes (memory layout, Mono image, NuttX 12 QSPI
>   struct, hook filtering by `EMU_OS`, CCM board); see `README.md` "Meadow.OS (Rebase)" and
>   `KnownIssues_and_Errata.md` §9. The Rebase side is documented in its
>   `Documentation/Emulator.md`.
> - Legacy (Parts 4-5) was not revisited.

## Rollout

- All `Legacy`/`Rebase` changes land on a new branch named **`SupportEmulator`** in each repo —
  not on their current default branches.
- **Rebase first.** Implement and verify the `Rebase/build.sh` change and the emulator wrapper's
  meadow-os path end-to-end before touching `Legacy`. Legacy support is Part 4, done afterward.
- `--emulator` never selects or changes the defconfig, and never forces a reconfigure. It only
  toggles "skip bootloader" + "apply emulator kconfig tweaks on top of whatever `.config`
  already exists." The existing `if [ ! -r .config ]` bootstrap-configure guard in both scripts
  is untouched — configure only ever runs once, on a fresh checkout, exactly as it does today.
- The defconfig actually in use needs no new naming: it's `Rebase`'s own default
  (`CONFIG=meadow_os`) and `Legacy`'s own default (`CONFIG=mono`) — both already match what the
  emulator needs, so the wrapper doesn't need to pass `--config=` at all.

## Part 1 — `Rebase/build.sh`

- Add `EMULATOR=false` and a `--emulator)` case arm (same pattern as existing `-m|--mono`),
  plus a `--help` line.
- Guard the existing bootloader call (line ~421):
  ```bash
  if ! $EMULATOR; then
      $scriptdir/build-bootloader.sh $BOOTLOADER_OPTIONS
      if [ $? -ne 0 ]; then exit 1; fi
  fi
  ```
- After the existing NuttX configure step (line ~216), add:
  ```bash
  if $EMULATOR; then
      echo "--- Applying emulator config tweaks ---"
      kconfig-tweak --file $NUTTX_CONFIG_FILE --disable CONFIG_CDCACM
      kconfig-tweak --file $NUTTX_CONFIG_FILE --disable CONFIG_BOARDCTL_USBDEVCTRL
      kconfig-tweak --file $NUTTX_CONFIG_FILE --disable CONFIG_STM32F7_OTGFS
      kconfig-tweak --file $NUTTX_CONFIG_FILE --disable CONFIG_USBDEV
      kconfig-tweak --file $NUTTX_CONFIG_FILE --enable CONFIG_STM32F7_UART4
      kconfig-tweak --file $NUTTX_CONFIG_FILE --set-str CONFIG_HCOM_COMMS_DEVICE_NAME /dev/ttyS1
      kconfig-tweak --file $NUTTX_CONFIG_FILE --set-val CONFIG_UART4_BAUD 115200
      kconfig-tweak --file $NUTTX_CONFIG_FILE --set-val CONFIG_UART4_BITS 8
      kconfig-tweak --file $NUTTX_CONFIG_FILE --set-val CONFIG_UART4_PARITY 0
      kconfig-tweak --file $NUTTX_CONFIG_FILE --set-val CONFIG_UART4_2STOP 0
      kconfig-tweak --file $NUTTX_CONFIG_FILE --set-val CONFIG_UART4_RXBUFSIZE 512
      kconfig-tweak --file $NUTTX_CONFIG_FILE --set-val CONFIG_UART4_TXBUFSIZE 512
      kconfig-tweak --file $NUTTX_CONFIG_FILE --enable CONFIG_DEV_CONSOLE
      kconfig-tweak --file $NUTTX_CONFIG_FILE --enable CONFIG_SYSLOG_SERIAL_CONSOLE
      kconfig-tweak --file $NUTTX_CONFIG_FILE --disable CONFIG_RAMLOG_SYSLOG
      kconfig-tweak --file $NUTTX_CONFIG_FILE --set-val CONFIG_BOARD_RESET_ON_ASSERT 0
      make -C $scriptdir/nuttx olddefconfig 2>/dev/null || true
  fi
  ```
  No defconfig-name branching (e.g. an `_emu` suffix) — that was a leftover artifact from earlier
  work and does not apply. `--emulator` applies unconditionally on top of the existing
  `meadow_os` config.
- No Mono step to touch — Rebase's `build.sh` doesn't build Mono today regardless of mode.
- `generate_build_info`/`restore_versioned_files` already target the correct
  `F7MeadowOS/include/meadow/hcom_nuttx_shared.h` — using it directly fixes the emulator's
  existing wrong-path bug as a side effect.
- `make ... nuttx.elf` (line 454) already exists as an explicit target — unchanged.

## Part 2 — Verify Rebase changes standalone

Before touching the emulator repo, validate directly against `Rebase`:

1. `Rebase/build.sh --emulator` (bare, no `--config`) — confirm no bootloader build runs, the
   emulator kconfig tweaks land in `.config`, and `nuttx.elf`/`nuttx_user.elf` are produced.
2. Diff the resulting `.config` against what the current emulator wrapper produces today via its
   own kconfig-tweak calls for meadow-os — should be equivalent (same tweaked keys/values).
3. Confirm `hcom_nuttx_shared.h`'s version placeholders are actually replaced now (previously
   silently skipped by the emulator's wrong `SHARED_HEADER` path).

**Status: done — full `./build.sh --emulator` run completed with exit code 0.** All four
checks above passed. Getting there required two additional fixes beyond the original Part 1
scope, both real pre-existing gaps in `Rebase` that this build path had apparently never been
exercised against before:

- `nuttx/boards/arm/stm32f7/stm32f777zit6-meadow/src/stm32_boot.c`: `board_init_usbdev()` was
  defined unconditionally but its only caller was guarded by `#if defined(CONFIG_CDCACM)`.
  Disabling `CONFIG_CDCACM` (required for the emulator) left it "defined but unused," a hard
  error under `-Werror=unused-function` on this toolchain. Fixed by guarding the definition to
  match its caller.
- `build.sh`'s "Package Meadow.OS" step (merging in the bootloader binary via `srec_cat`) was
  not guarded by `$EMULATOR` in the original Part 1 edit and failed looking for the
  never-built `Meadow.BL.bin`. Fixed by skipping that whole block under `--emulator` — none of
  `Meadow.OS.Update.bin`/`Meadow.OS.bin`/`Meadow.OS.Runtime.bin` are used by the emulator, which
  reads `nuttx.elf`/`nuttx_user.elf`/`nuttx.bin`/`nuttx_user.bin` directly.

Also hit (not a code bug): stale `.o` files from a prior non-emulator build were older than the
regenerated `config.h` and caused a link-time "undefined reference" until cleaned — a plain
incremental-build hazard, resolved with `--clean`, not a source change. **Worth checking for the
same three issues in Part 4 (Legacy)** — the unused-function pattern and stale-object risk are
generic enough to plausibly recur there too.

## Part 3 — Emulator wrapper: meadow-os path

Update `build-meadow.os-emulated.sh`'s meadow-os branch to drop its own configure/kconfig-tweak/
version-injection logic and become:

1. Resolve `MEADOW_EMULATOR_OS_DIR` (unchanged, via `scripts/resolve-locations.sh`).
2. Version-guard the sibling repo before calling into it:
   ```bash
   if ! grep -q -- '--emulator' "$MEADOW_EMULATOR_OS_DIR/build.sh"; then
       echo "ERROR: $MEADOW_EMULATOR_OS_DIR/build.sh does not support --emulator."
       echo "       Update your checkout to the SupportEmulator branch (or later, once merged)."
       exit 1
   fi
   ```
3. `MEADOW_ADDITIONAL_MAKE_OPTIONS` forwarded as-is — `build.sh` already reads this env var
   itself for `-j` parallelism, so nothing to translate.
4. Call `"$MEADOW_EMULATOR_OS_DIR/build.sh" --emulator` — bare, no `--config`. It relies entirely
   on Rebase's own default (`CONFIG=meadow_os`), which already matches what the emulator needs.
5. Renode-only post-processing, reading from `$MEADOW_EMULATOR_OS_DIR/nuttx/{nuttx.elf,
   nuttx_user.elf,nuttx.bin,nuttx_user.bin}` directly: copy ELFs into `build/` for GDB, run
   `tools/extract-hook-addresses.py` for Renode's `.resc` hook includes.
6. End-to-end test: build, then `./run.sh --rebase` and confirm Renode boots the image (HCOM
   connects, `device info` returns via Meadow.CLI).

The legacy branch of `build-meadow.os-emulated.sh` and all of
`build-meadow.os_legacy-emulated.sh` are untouched until Part 4/5.

**Status: done.** `./build-meadow.os-emulated.sh --config configs/meadow-os.yaml` ran end to
end (exit 0): version-guard passed, delegated to `Rebase/build.sh --emulator`, copied
`nuttx.elf`/`nuttx_user.elf`/`nuttx.bin`/`nuttx_user.bin` into `build/`, and hook-address
extraction generated `addresses.resc`/`hooks.resc` (two pre-existing "missing symbol" warnings,
non-fatal, same category the script already tolerates). Items 1-5 fully verified.

Item 6 could only be partially verified: `./run.sh --rebase` correctly reaches Renode
selection, but stops with "Renode not found" — the WildernessLabs Renode fork isn't built in
this environment (a pre-existing local-setup gap unrelated to this change, same as the very
first `setup.sh` run at the start of this work). Actually booting Renode and confirming HCOM/
Meadow.CLI connectivity is unverified and should be done wherever a built Renode fork is
available.

## Part 4 — `Legacy/build.sh` (after Rebase is verified)

- Add `EMULATOR=false` and a `--emulator)` case arm, plus a `--help` line.
- Guard the existing bootloader step:
  ```bash
  if ! $EMULATOR; then
      $scriptdir/build-bootloader.sh $BOOTLOADER_OPTIONS
      if [ $? -ne 0 ]; then exit 1; fi
  fi
  ```
- After the existing test/assert kconfig-tweak block (before `if $CONFIGURE_ONLY`), add:
  ```bash
  if $EMULATOR; then
      echo "--- Applying emulator config tweaks ---"
      kconfig-tweak --file $NUTTX_CONFIG_FILE --disable CONFIG_CDCACM
      kconfig-tweak --file $NUTTX_CONFIG_FILE --disable CONFIG_BOARDCTL_USBDEVCTRL
      kconfig-tweak --file $NUTTX_CONFIG_FILE --disable CONFIG_USBDEV
      kconfig-tweak --file $NUTTX_CONFIG_FILE --set-str CONFIG_HCOM_COMMS_DEVICE_NAME /dev/ttyS1
      kconfig-tweak --file $NUTTX_CONFIG_FILE --enable CONFIG_DEV_CONSOLE
      kconfig-tweak --file $NUTTX_CONFIG_FILE --enable CONFIG_USART1_SERIAL_CONSOLE
      kconfig-tweak --file $NUTTX_CONFIG_FILE --disable CONFIG_RAMLOG_SYSLOG
      kconfig-tweak --file $NUTTX_CONFIG_FILE --enable CONFIG_RAMLOG_CONSOLE
      kconfig-tweak --file $NUTTX_CONFIG_FILE --enable CONFIG_SYSLOG_CONSOLE
      kconfig-tweak --file $NUTTX_CONFIG_FILE --enable CONFIG_SYSLOG_SERIAL_CONSOLE
      kconfig-tweak --file $NUTTX_CONFIG_FILE --enable CONFIG_SYSLOG_WRITE
      kconfig-tweak --file $NUTTX_CONFIG_FILE --enable CONFIG_FS_TMPFS
      kconfig-tweak --file $NUTTX_CONFIG_FILE --set-val CONFIG_FS_TMPFS_BLOCKSIZE 512
      kconfig-tweak --file $NUTTX_CONFIG_FILE --set-val CONFIG_FS_TMPFS_DIRECTORY_ALLOCGUARD 64
      kconfig-tweak --file $NUTTX_CONFIG_FILE --set-val CONFIG_FS_TMPFS_DIRECTORY_FREEGUARD 64
      kconfig-tweak --file $NUTTX_CONFIG_FILE --set-val CONFIG_FS_TMPFS_FILE_ALLOCGUARD 512
      kconfig-tweak --file $NUTTX_CONFIG_FILE --set-val CONFIG_FS_TMPFS_FILE_FREEGUARD 512
      ENABLE_ASSERTS=true   # reuses the existing --enableasserts behavior (BOARD_RESET_ON_ASSERT=0)
  fi
  ```
- No change to the Mono/mbedtls/pass1/pass1deps/pass2 sequence, or to `generate_build_info`/
  `restore_versioned_files` — these already do exactly what the emulator wrapper currently
  reimplements by hand for the legacy header and `user-space.ld`.
- `nuttx.elf`/`nuttx_user.elf` are already left in `$scriptdir/nuttx/` as ordinary build
  artifacts of `pass1`/`pass2` — no new step needed to "produce" them.
- Verify standalone first, same as Part 2 but for Legacy: `Legacy/build.sh --emulator` (bare),
  confirm no bootloader build, Mono builds automatically with no manual pre-step, and
  `nuttx.elf`/`nuttx_user.elf` are produced.

**Status: done.** `MEADOW_ADDITIONAL_MAKE_OPTIONS=-j1 ./build.sh --emulator` completed with exit
code 0 (branch `SupportEmulator`, on top of a `--clean` first pass since `.config` was ~3 weeks
stale). All artifacts produced, no bootloader/packaging, tweaks confirmed landed
(`CONFIG_CDCACM`/`CONFIG_RAMLOG_SYSLOG` disabled, `CONFIG_RAMLOG_CONSOLE`/`HCOM_COMMS_DEVICE_NAME`/
`BOARD_RESET_ON_ASSERT=0` set via the reused `--enableasserts` mechanism). Also found and fixed:

- **Ordering bug in this plan itself**: the original wording had `ENABLE_ASSERTS=true` inside the
  new `--emulator` block placed *after* the existing `if $ENABLE_ASSERTS` check — too late to take
  effect. Fixed by setting `ENABLE_ASSERTS=true` directly in the `--emulator)` case arm instead
  (guaranteed to run before that check), and dropping the now-redundant `BOARD_RESET_ON_ASSERT`
  line from the new tweak block.
- `nuttx/arch/arm/src/common/up_initialize.c`: missing `#include <nuttx/syslog/ramlog.h>` before
  calling `ramlog_consoleinit()` — a hard error (`-Wimplicit-function-declaration`) once
  `CONFIG_RAMLOG_CONSOLE` is selected, on this toolchain. Same gap exists in every other NuttX
  arch port (x86/mips/risc-v checked) — a latent upstream issue, not something specific to this
  board. Fixed by adding the include.
- `nuttx/configs/stm32f777zit6-meadow/src/hcom_nx/hcom_nx_startup_mgr.c`: an unguarded call to
  `hcom_nx_trace_insure_correct_config()` — its prototype is declared only under
  `#if defined(CONFIG_RAMLOG_SYSLOG)`, and disabling that (required for the emulator) left the
  call with no visible declaration. Fixed by wrapping the call in the same guard, matching how
  the sibling call in `hcom_nx_upd.c` already handles it.

## Part 5 — Emulator wrapper: legacy path

Update the legacy branch of `build-meadow.os-emulated.sh` and all of
`build-meadow.os_legacy-emulated.sh` the same way as Part 3: version-guard, call
`"$MEADOW_EMULATOR_OS_DIR/build.sh" --emulator` (bare), then keep only the Renode-only
post-processing (the `objcopy` split into vectors/kernel/user/mono-SDRAM binaries, ELF copies,
hook-address extraction). Delete the manual Mono pre-check (`ERROR: Mono runtime libraries not
found...`) entirely — `Legacy/build.sh` now builds Mono itself.

End-to-end test: build, then `./run.sh --legacy` and confirm Renode boots the image.

> **Retired (2026-11-15):** the `legacy-os_modern-dotnet` config, its scripts
> (`meadow-dotnet10-headless.resc`, `meadow-dotnet10-gdb.resc`) and its wrapper and `run.sh`
> branches were removed. `configs/legacy-ccm.yaml` (Legacy meadow-3.0, whose `build.sh` builds
> .NET 10 Mono itself) replaces it. The notes below are kept as history.

**Out of scope / unchanged:** the `.NET 10 Mono` (`legacy-os_modern-dotnet`) runtime path is
unique to the emulator (built from the separate `runtime/` sibling repo via
`runtime/src/mono/build-nuttx.sh`) and has no equivalent in `Legacy/build.sh`'s `-m|--mono` flag
(old Mono). That linking step (`MONO_AOBJS=...`) stays entirely in the emulator wrapper, applied
after `build.sh --emulator` runs.

**Status: done.** Both `build-meadow.os_legacy-emulated.sh` (rewritten) and
`build-meadow.os-emulated.sh`'s legacy branch now delegate to `Legacy/build.sh --emulator`,
version-guarded the same way as Part 3. Since both branches ended up doing the exact same thing
(delegate, then the same artifact post-processing), the `if [ "$EMU_OS" = "legacy" ]`/`else`
split in `build-meadow.os-emulated.sh` collapsed to one unconditional delegation block — along
with removing now-dead variables (`CONFIGURE_SEP`, `SHARED_HEADER`, `VERSION_SOURCE`, `CONFIG`,
`$MAKE` detection) left over from the deleted hand-rolled build logic. The Mono-validation
section's legacy old-mono pre-check was deleted per plan; the meadow-os `.NET 10` check was left
in place (harmless, and already unreachable in practice before this change — see finding below).

Verified: `./build-meadow.os_legacy-emulated.sh` and `./build-meadow.os-emulated.sh --config
configs/legacy.yaml` both completed with exit 0 — delegation succeeded, split binaries
(`nuttx_vectors.bin`/`nuttx_kernel.bin`/`nuttx_user.bin`/`nuttx_mono.bin`) produced, hook-address
extraction resolved all symbols cleanly (cleaner than the Rebase run — no missing-symbol
warnings). `./run.sh --legacy` reaches the same "Renode not found" environment limitation as
Part 3 — unverified for the same reason (no Renode fork built here).

**Finding (not acted on, out of scope):** the `.NET 10 Mono` runtime check in the Mono-validation
section was already dead code before this change, independent of delegation — `configs/
legacy-os_modern-dotnet.yaml` sets `os: legacy`, which always routed to the *old*-Mono check
branch, never the `.NET 10` branch; and even when `EMU_OS != legacy`, the legacy build steps never
referenced `$MONO_MAKE_VARS` in the first place. This combination was non-functional
pre-existing, not something this change broke.

## Part 6 — Verify build.sh still works for production (non-emulator) hardware builds

It is essential that the build systems for both code bases still work on F7 hardware. Since
every Part 1/4 change is gated behind `$EMULATOR` (bootloader guard, packaging guard, kconfig
tweaks), running `build.sh` **without** `--emulator` should be completely unaffected — this is
the regression test proving that.

- Build Rebase with `build.sh --clean --force` (no `--emulator`) and verify: no errors, bootloader
  builds, packaging (`Meadow.OS.bin`/`Meadow.OS.Update.bin`/`Meadow.OS.Runtime.bin`) succeeds.
- Build Legacy with `build.sh --clean --force` (no `--emulator`) the same way, once Part 4 lands.

**Status: Rebase done.** `build.sh --clean --force` (no `--emulator`) completed with exit code 0
from a full `distclean`: bootloader rebuilt (`bootloader/Debug/Meadow.BL.bin`), all three
packaged images produced (`Meadow.OS.bin`, `Meadow.OS.Update.bin`, `Meadow.OS.Runtime.bin`),
working tree clean except the two intentional Part 1/2 edits (`build.sh`, `stm32_boot.c`).
Confirms the `$EMULATOR`-gated changes don't affect the default/production path.

**Status: Legacy done.** Same check on `Legacy` (`MEADOW_ADDITIONAL_MAKE_OPTIONS=-j1 ./build.sh
--clean --force`, no `--emulator`) completed with exit code 0 from a full `distclean`:
bootloader rebuilt, all three packaged images produced, working tree clean except the three
intentional Part 4/5 edits (`build.sh`, `up_initialize.c`, `hcom_nx_startup_mgr.c`) plus the
pre-existing unrelated WiFi-country-code work-in-progress on this checkout (untouched
throughout). Both repos now confirmed unaffected in default/production mode.

**Scope: build-only.** No F7 hardware is available in this environment to flash or run on —
verified is "compiles, links, and packages with zero errors," not on-device behavior. Actually
flashing and confirming device behavior is left to whoever has physical hardware.

**Timing:** run the Rebase check now (`Rebase/build.sh` is already changed from Part 1, ahead of
Legacy) rather than waiting until Part 4/5 are done — catches a regression earlier. The Legacy
half of this check happens once Part 4's `Legacy/build.sh` changes land.

## Decisions made during review

1. `--emulator` reuses `ENABLE_ASSERTS=true` in Legacy rather than duplicating the
   `BOARD_RESET_ON_ASSERT` tweak.
2. No `_emu`-suffixed defconfig, now or later — `--emulator` applies tweaks on top of the
   existing `meadow_os` (Rebase) / `mono` (Legacy) defconfig, unconditionally, without
   reconfiguring.
3. Flag name: `--emulator`.
4. Wrapper calls `build.sh --emulator` bare — no `--config=`, no `--configure`.
5. Rollout: `SupportEmulator` branch in both repos, Rebase implemented and verified first,
   Legacy (Part 4/5) afterward.
6. Emulator wrapper version-guards the sibling repo's `build.sh` for `--emulator` support before
   calling it, failing with an explicit upgrade message rather than letting an old checkout hit
   a generic "Unknown option" error.

## Files touched

- `Rebase/build.sh` (branch `SupportEmulator`)
- `Legacy/build.sh` (branch `SupportEmulator`)
- `Meadow.OS.Emulator/build-meadow.os-emulated.sh`
- `Meadow.OS.Emulator/build-meadow.os_legacy-emulated.sh`
- `Meadow.OS.Emulator/README.md` (describe the new delegation model)
