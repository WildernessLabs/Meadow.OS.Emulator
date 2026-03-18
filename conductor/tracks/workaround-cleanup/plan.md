# Plan: Workaround Cleanup

## Track ID: `workaround-cleanup`
## Status: `in-progress` (7/11 complete, 3 deferred, 1 likely fixed)
## Priority: `high`

---

## W.1 — Remove Dead SvcPrimaskFix Code
- **KnownIssues ref:** §1.1
- **Priority:** Low (no runtime cost, just dead code)
- **What:** Remove `include @peripherals/SvcPrimaskFix.cs` from all 4 `.resc` files
  (`meadow-dual.resc`, `meadow-legacy.resc`, `meadow-legacy-headless.resc`,
  `meadow-legacy-debug.resc`). Optionally delete `peripherals/SvcPrimaskFix.cs`.
- **Why:** The SVC/PRIMASK bug is fixed natively in the Renode fork (U.2, commit
  `d18c1f6fe`). The C# hook is included but `InstallSvcFix` is never called —
  pure dead code.
- **Risk:** None. Hook is not invoked.
- **Status:** COMPLETE
- [x] Remove `include` lines from 4 `.resc` files
- [ ] Delete `peripherals/SvcPrimaskFix.cs` (kept for reference — new OS build still uses IronPython SVC hooks)
- [x] Verify legacy boot still works

## W.2 — Migrate RCC Python Peripheral to C#
- **KnownIssues ref:** §2.1
- **Priority:** High (performance — every RCC register access goes through IronPython)
- **What:** Replace `scripts/pydev/rcc.py` with a compiled C# peripheral that
  auto-sets clock ready bits and mirrors SW→SWS.
- **Why:** RCC is loaded in ALL `.resc` scripts. IronPython peripherals are 10-100x
  slower than C#. Every clock register read/write pays this penalty.
- **Approach:** Write `peripherals/STM32F4_RCC_Extended.cs` extending or wrapping
  Renode's built-in `STM32F4_RCC`. Implement ready-bit auto-set and SW→SWS mirroring
  in C#. Replace `PyDevFromFile` with the C# peripheral in all `.resc` files.
- **Related:** `renode-upstream` U.3 (could eventually upstream this)
- **Status:** COMPLETE
- [x] Audit `rcc.py` for full list of register behaviors
- [x] Implement C# peripheral (`peripherals/STM32F7_RCC_Custom.cs`)
  - `BasicDoubleWordPeripheral` with `IKnownSize` (0x400 bytes)
  - Ready-bit auto-set via `valueProviderCallback` (idiomatic Renode pattern)
  - SW→SWS mirroring in RCC_CFGR
  - All STM32F7 RCC registers 0x00-0x90 with correct reset values
  - Fallback dictionary for undefined offsets
- [x] Replace `PyDevFromFile` in all 8 `.resc` files
- [x] Verify boot on legacy build (HCOM device info verified)

## W.3 — Migrate New OS QSPI Bypass from IronPython to C#
- **KnownIssues ref:** §2.3
- **Priority:** High (performance — 22K calls during assembly load on slow path)
- **What:** `meadow-os.resc` uses an inline IronPython hook for QSPI DMA bypass
  instead of calling `cpu InstallQspiBypass` from the existing `QspiDmaBypass.cs`.
- **Why:** The C# hook already exists and is used by the legacy build. The new OS
  build regressed to IronPython, paying 10-100x overhead on 22K+ calls.
- **Status:** COMPLETE
- [x] Replace inline IronPython QSPI hook in `meadow-os.resc` with `cpu InstallQspiBypass`
- [x] Added `include @peripherals/QspiDmaBypass.cs` to meadow-os.resc
- [ ] Verify new OS build boots and mounts LittleFS (deferred — requires new OS build)

## W.4 — Migrate Legacy ESPCP SPI Bypass from IronPython to C#
- **KnownIssues ref:** §2.4
- **Priority:** Medium (performance — legacy build uses slower IronPython path)
- **What:** Legacy build uses inline IronPython for `espcp_send_data_over_spi` bypass.
  The C# `EspcpSpiBypass.cs` exists and is used in dual-machine mode.
- **Why:** 15K+ calls per 30s on the slow IronPython path.
- **Status:** COMPLETE
- [x] Modify `EspcpSpiBypass.cs` to find either SpiBridge or EspcpStub (was SpiBridge-only)
- [x] Replace IronPython hook in `meadow-legacy-hooks.resc` with `cpu InstallEspcpSpiBypass`
- [x] Add `include @peripherals/EspcpSpiBypass.cs` to legacy `.resc` files
- [x] Verify legacy HCOM device info works with C# hook

## W.5 — Rebuild Firmware with Compiled-In Version
- **KnownIssues ref:** §5.2 (related)
- **Priority:** High (user requirement — no runtime hacks for version info)
- **What:** Run `build-meadow.os_legacy-emulated.sh` to rebuild firmware with version
  2.5.7 from `Meadow/version.txt` compiled into `hcom_nuttx_shared.h`.
- **Why:** Current binary was built before version injection was added to the build
  script. `hcom_nx_config_init` zeroes the version fields, causing blank OSVersion
  and RuntimeVersion in `device info`.
- **Status:** COMPLETE
- **Issues found and fixed:**
  1. **ar race condition:** `pass1deps` ran with `-jN`, causing concurrent apps to
     corrupt `libapps.a` (missing `hcom_startup_manager.o` → undefined `hcom_main`).
     Fix: run `pass1deps` with `-j1`.
  2. **Stale board .o files:** `make distclean` doesn't clean board config subdirectory
     objects (`configs/.../src/hcom_nx/*.o`). Stale objects from a previous build (with
     zeroed version defines) were reused. Fix: `find ... -name "*.o" -delete` before build.
  3. **Git hash overflow:** `git rev-parse --short HEAD` returned 11 hex chars (44 bits),
     exceeding `uint32_t`. Fix: truncate to 8 hex chars with `--short=8`.
  4. **Extract script Thumb bit bug:** `espcp_thread_loop_top` wasn't found because
     LR (with Thumb bit set) was compared against disassembly addresses (even). Fix:
     `if iaddr == (lr & ~1)`.
  5. **Extract script objdump comment format:** ARM GCC 10.3 uses `;` for literal pool
     comments while GCC 15.2 uses `@`. Fix: match both `[@;]` in regex.
- [x] Diagnose build failure
- [x] Fix build issue (5 issues found)
- [x] Rebuild successfully (3/3 consecutive builds pass)
- [x] Verify `device info` shows version 2.5.7.0 via hcom_test.py
- [x] No fw_ver_fix hook needed (version compiled in)

## W.6 — Verify/Deploy Mono Runtime Check Bypass
- **KnownIssues ref:** §3.4
- **Priority:** Medium (documented but never deployed — may not be needed)
- **What:** KnownIssues §3.4 documents a hook at 0x08057bee to bypass the Mono
  runtime state check that gates ESP32 operations. No such hook exists in any
  `.resc` file.
- **Why:** Either the check doesn't actually block operations (in which case remove
  the doc), or it does and the hook needs to be added.
- **Status:** COMPLETE (not needed)
- [x] Tested: ESPCP HCOM commands work without this hook
  - `hcom_mono_ctrl_is_mono_enabled` checks BBR bit 0x800; BBR defaults to 0 → returns 1 (enabled)
  - No bypass needed; ESPCP boot + post-boot polling + HCOM all verified
- [x] KnownIssues §3.4 already updated to reflect this

## W.7 — Clean Up Undeployed FPU Frame Workaround
- **KnownIssues ref:** §1.2
- **Priority:** Low (legacy unaffected, new OS not yet in active use)
- **What:** `scripts/pydev/fix_fpu_frame.py` exists but is never loaded.
- **Why:** Dead file creates confusion — documented as a workaround but not deployed.
- **Status:** COMPLETE (deleted)
- [x] Decision: delete the file — legacy unaffected, new OS not in active use
- [x] Deleted `scripts/pydev/fix_fpu_frame.py`

## W.8 — Investigate Fork Performance Penalty
- **KnownIssues ref:** §8.5
- **Priority:** Medium (45% perf penalty on every run)
- **What:** The Renode fork (.NET 8) runs ~45% slower than installed Mono Renode for
  the same workload. The fork is now the primary runtime.
- **Why:** Every emulation run pays this penalty. Understanding the cause could yield
  a significant speedup across all workloads.
- [ ] Profile fork vs installed Renode under same workload
- [ ] Check tlib compilation flags (optimization level, LTO)
- [ ] Check .NET 8 GC settings (server vs workstation, gen0/gen1 thresholds)
- [ ] Check if NativeAOT publish changes anything

## W.9 — Handle SYSRESETREQ
- **KnownIssues ref:** §1.3
- **Priority:** Medium (no workaround exists — emulator hangs on reset)
- **What:** `NVIC_SystemReset()` (writing SYSRESETREQ to AIRCR) causes the CPU to
  halt with PC=0, SP=0 instead of actually resetting the system. Any HCOM command
  that triggers a system reset (e.g., `device config wifi`) kills the emulation
  session.
- **Why:** Users will hit this as soon as they try configuration commands. Currently
  the only mitigation is "don't do that."
- **Status:** LIKELY FIXED (needs manual verification)
- **Findings:** The Renode fork's NVIC.cs (line 836-861) already handles SYSRESETREQ
  correctly: calls `machine.RequestReset()` which executes the `reset` macro defined
  in the `.resc` script. The reset macro reloads binaries and resets peripherals.
  The "PC=0, SP=0" symptom was likely from stock Renode (not the fork).
- **HCOM commands that trigger reset:** 20 (HOST_RESTART), 21 (ONLY_RESTART), 28 (FLASH_OS_UPDATE)
- **Call path:** HCOM → hcom_nx_common_utils → up_systemreset() at 0x081633A8
- **Hooks survive reset:** Renode's `AddHook` persists across machine resets.
- [x] Identified which HCOM commands trigger SYSRESETREQ
- [x] Fork already handles SYSRESETREQ via NVIC.cs → machine.RequestReset()
- [ ] Manual test: send HCOM restart command and verify emulator recovers

## W.10 — Flash Persistence via File-Backed Memory
- **KnownIssues ref:** §4.4
- **Priority:** Medium (blocks reboot/update testing workflows)
- **What:** The 64MB QSPI flash uses volatile `MappedMemory`. All filesystem changes
  (deployed apps, configuration, firmware updates) are lost when Renode exits.
- **Why:** Cannot test firmware update → reboot → boot-from-new-image flows, or
  configuration persistence. LittleFS auto-formats on every boot instead of
  mounting the previous filesystem.
- **Approach:** Renode supports file-backed memory. Replace the volatile
  `MappedMemory` in `meadow-f7.repl` with a file-backed variant pointing to a
  persistent image file (e.g., `build/legacy/qspi_flash.bin`). Pre-populate with
  the LittleFS v1 image on first run.
- [ ] Test Renode's file-backed `MappedMemory` with a simple read/write cycle
- [ ] Update `meadow-f7.repl` QSPI flash definition
- [ ] Ensure LittleFS image is loaded on first boot, persisted on exit
- [ ] Verify deploy → restart → app-still-present workflow
- [ ] Update KnownIssues §4.4

## W.11 — Automate Hook Addresses for New OS Build
- **KnownIssues ref:** §5.3
- **Priority:** Medium (silent breakage on rebuild)
- **What:** `meadow-os.resc` uses hardcoded hex addresses for all hooks. The legacy
  build has `extract-hook-addresses.py` automation, but no equivalent exists for the
  new OS build.
- **Why:** Any new OS firmware rebuild will silently break all hooks — they'll fire at
  wrong addresses, causing hangs or crashes with no obvious error.
- **Approach:** Extend `extract-hook-addresses.py` (or write a parallel script) to
  extract addresses from the new OS ELF and generate an `addresses.resc` for the new
  OS build. Update `meadow-os.resc` to use `$hook_*` variables instead of hex literals.
- [ ] Identify all hardcoded addresses in `meadow-os.resc`
- [ ] Map each to an ELF symbol or disassembly pattern
- [ ] Extend extraction script or create new one for new OS
- [ ] Convert `meadow-os.resc` to use generated variables
- [ ] Update KnownIssues §5.3

---

## Status Summary

| Item | Status |
|------|--------|
| W.1 | COMPLETE — dead SvcPrimaskFix includes removed |
| W.2 | COMPLETE — RCC migrated from Python to C# |
| W.3 | COMPLETE — New OS QSPI migrated to C# hook |
| W.4 | COMPLETE — Legacy ESPCP SPI migrated to C# hook |
| W.5 | COMPLETE — Firmware rebuilt with version 2.5.7.0 |
| W.6 | COMPLETE — Mono bypass verified not needed |
| W.7 | COMPLETE — Dead FPU frame file deleted |
| W.8 | DEFERRED — Fork performance investigation (needs profiling) |
| W.9 | LIKELY FIXED — Fork already handles SYSRESETREQ (needs manual test) |
| W.10 | DEFERRED — Flash persistence (needs Renode file-backed memory API research) |
| W.11 | DEFERRED — New OS address automation (blocked on new OS build) |

## Decision Log

| Date       | Decision | Rationale |
|------------|----------|-----------|
| 2026-03-17 | Created track from KnownIssues audit | Cross-referenced KnownIssues_and_Errata.md with upstream_fixes.md and actual .resc files; found multiple discrepancies between documented and actual status |
| 2026-03-17 | Added W.9 (SYSRESETREQ), W.10 (flash persistence), W.11 (new OS address automation) | These were gaps in KnownIssues with no existing workaround or no tracking |
| 2026-03-18 | Completed W.1-W.7, deferred W.8/W.10/W.11 | W.8 requires profiling tools, W.10 requires Renode API research, W.11 blocked on new OS build. W.9 likely already fixed in fork. |
