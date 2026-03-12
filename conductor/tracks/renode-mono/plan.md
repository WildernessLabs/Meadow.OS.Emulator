# Plan: Mono Runtime Bring-Up in Emulator

## Track ID: `renode-mono`
## Status: `in-progress` (Phase 3 — Blinky app runs, working on broader app support)
## Repo: `Meadow.OS.Emulator` (sibling to `Meadow.OS/`)

---

## Current State (2026-03-12)

Mono JIT boots and runs a Meadow Blinky app end-to-end in the Renode emulator at near
real-time speed (load ~1x). Key milestones achieved:

- NuttX firmware with real Mono runtime linked and booting
- App deployment via Meadow CLI (`app deploy`) over TCP socket
- Pre-populated LFS image as faster alternative to CLI deploy
- Mono JIT starts, loads assemblies, runs `MeadowApp.Initialize()` + `Run()`
- Blinky app cycles colors in a loop (confirmed via `meadow listen`)
- Emulation runs at ~1x real-time (was 96,000x before SVC fix)

**Key upstream fixes** (tracked in `upstream_fixes.md`):
- U.2: SVC/PRIMASK bug in NVIC — 12,000x speedup
- U.9: STM32F4 RTC backup register count (20→32)

---

## Phase 1: Research & Build (DONE)

### 1.1 — Understand Mono Build System
- [x] Mono cross-compiled for Cortex-M7, linked into NuttX replacing `libmonostub.a`
- [x] Memory layout: Mono runtime at 0xC0000000 (SDRAM), assemblies in QSPI LittleFS

### 1.2 — Build Mono for Emulator
- [x] Build script updated, ar race condition fixed with `-j1`
- [x] `nuttx_mono_aligned.bin` — 32-byte prepend for correct ELF addresses

### 1.3 — SDRAM / Memory Setup
- [x] SDRAM at 0xC0000000, FMC stub for init sequence, QSPI flash at 0x90000000

## Phase 2: Boot Mono (DONE)

### 2.1–2.3 — Mono Init + Assembly Loading (DONE)
- [x] Mono task created (PID 13), `mono_main` reached
- [x] LittleFS v1 image builder (`tools/build_lfs_v1_image.c`) for legacy v1.1 format
- [x] Assemblies loaded: mscorlib.dll, System.dll, System.Core.dll, Meadow.dll (22K QSPI reads, 13MB)
- [x] Version injection from `Meadow/version.txt` (2.5.7) into firmware + Mono signature

### 2.4 — ESPCP CPU Starvation Fix (DONE)
- [x] ESPCP polling thread disabled during Mono init (was starving Mono of CPU)
- [ ] Re-enable ESPCP polling after Mono init (needed for WiFi/BLE)

### 2.5–2.8 — Version Matching, BBR Persistence, CLI Deploy (DONE)
- [x] Mono version string fix: split hook (skip + copy-after) for correct timing
- [x] BBR persistence: patched Renode RTC model (`BackupEnd = 0xCC`) — no more hooks needed
- [x] CLI deploy via TCP socket (`-s socket://localhost:4242`), file transfer working
- [x] Download watchdog bypass for virtual/wall time mismatch
- [x] Stale `DNLD_FAIL_RESEND` fix in Meadow CLI (`dataTransferStarted` guard)

### 2.9 — SVC/PRIMASK Fix in Renode (DONE — MAJOR BREAKTHROUGH)
Root cause of all emulation slowness. SVC hooks broke Renode's translation blocks on every
syscall (thousands/sec during Mono operation), reducing throughput to ~6,600 instructions/sec.

- [x] Patched `NVIC.cs:ShouldRaiseException()` — let SVCall through when PRIMASK is set
- [x] Per ARMv7-M ARM B1.5.14: SVC is synchronous, should escalate to HardFault, not be skipped
- [x] Removed `SvcPrimaskFix.cs` hook from `.resc` script
- [x] **Result**: 12,000x speedup. Load from 96,000x → 5-8x. Mono boots in minutes, not hours.
- [x] Confirmed: firmware uses JIT by default (`MONO_OPTION_JIT` in `hcom_mono_control.c:227`)
- [ ] Upstream fix to Renode (Antmicro) — clear spec compliance bug

### 2.10 — Syslog Semaphore Deadlock Fix (DONE)
- [x] `hcom_logging_syslog()` has sem leak when malloc fails — deadlocks all Mono threads
- [x] Hook to skip function. Firmware bug — should fix upstream.

### 2.11 — Board Version Fix (DONE)
- [x] GPIO hook returns F7FeatherV2 (2) instead of F7CoreComputeV2 (3)
- [x] Blinky app's `MeadowApp<T> where T : F7FeatherBase` now finds matching device

## Phase 3: Application Execution (IN PROGRESS)

### 3.1 — Blinky App (DONE)
- [x] Deployed trimmed Blinky via pre-populated LFS image
- [x] Mono JIT boots, loads assemblies, runs app
- [x] `meadow listen` shows: `Initialize... Run... Cycle colors...`
- [x] App runs continuously at near real-time speed

### 3.2 — GPIO App
- [ ] Verify GPIO state changes visible in Renode (Blinky toggles LED pins)
- [ ] Add GPIO monitoring/logging to validate pin toggles

### 3.3 — WiFi App (depends on ESPCP re-enable)
- [ ] Re-enable ESPCP polling after Mono init
- [ ] Run a Meadow.NET app that connects to WiFi
- [ ] Verify ESPCP messages flow through the stub/bridge

## Phase 4: Speed & Usability (TODO)

### 4.1 — Renode Execution Speed Investigation
With SVC fix, load is ~1x for steady-state Blinky. May degrade for heavier workloads.

- [ ] Profile remaining hooks — which fire most during Mono execution?
- [ ] Test quantum tuning (currently 100μs / 46,200 instructions)
- [ ] Investigate Renode tlib vs QEMU TCG performance gap
- [ ] Consider QEMU as alternative for CPU-intensive workloads

### 4.2 — AOT Compilation (FUTURE)
Pre-compile IL → native ARM at build time. Firmware already whitelists `--aot`, `--llvmonly`,
`--llvmonly-interp` options. Gap is building AOT cross-compiler infrastructure.

- [ ] Find or build Mono AOT cross-compiler for ARMv7
- [ ] Build AOT native images for BCL assemblies
- [ ] Test `--llvmonly-interp` hybrid mode

---

## Hooks (addresses as of 2026-03-11 rebuild)

| Hook | Address | Purpose |
|------|---------|---------|
| `mono_copy_bypass` | 0x0805eb98 | Skip QSPI→SDRAM copy (pre-loaded via LoadBinary) |
| `mono_ver_skip` | 0x08143590 | Skip `refresh_mono_version` (reads unimplemented QSPI MTD) |
| `mono_ver_copy` | 0x08143762 | After OS version populated, copy to mono version fields |
| `mono_str_fix` | 0x080618e8 | Patch deep-copied config's mono version string (+0x80) |
| `lfs_format_skip` | 0x081714ae | Prevent LFS format from wiping pre-populated image |
| `gpio_ver_fix` | 0x08141970 | Force board version to F7FeatherV2 (2) |
| `espcp_spi_bypass` | 0x081480cc | Bypass SPI DMA with direct peripheral calls |
| `espcp_force_init` | 0x08141d50 | Force ESPCP init (set network type to WiFi) |
| `spi_lock_skip` | 0x08148144, 0x08148150 | Skip SPI lock/unlock (DMA bypassed) |
| `lsi_skip` | 0x0814d2c0 | Skip LSI clock calibration thread |
| `syslog_skip` | 0x0805cf78 | Skip `hcom_logging_syslog` (sem leak deadlocks Mono) |

**Removed hooks:**
- BBR hooks — fixed in Renode RTC model (`BackupEnd = 0xCC`)
- SVC hooks (`SvcPrimaskFix.cs`) — fixed in Renode NVIC.cs

## C# Extension Methods

| Hook | File | Purpose |
|------|------|---------|
| `InstallQspiBypass` | `peripherals/QspiDmaBypass.cs` | QSPI DMA bypass — direct memcpy via SystemBus |
| `InstallMqIntercept` | `peripherals/MqReceiveIntercept.cs` | ESPCP polling via mq_receive interception |

## Emulation Speed

| Metric | Before SVC Fix | After SVC Fix (2026-03-12) |
|--------|---------------|---------------------------|
| Load factor | 70,000–96,000x | ~1x (near real-time) |
| Effective instructions/sec | ~6,600 | ~70,000,000 (estimated) |
| Mono boot time | 8–24+ hours | ~2–3 minutes |
| Virtual time per 10 min wall | 0.01–0.07 seconds | ~600 seconds |

**Key lesson**: Hooks on hot paths are catastrophic. Even compiled C# hooks break Renode's
translation blocks. Any future hooks must target cold paths only.

## Known Issues

| Issue | Severity | Notes |
|-------|----------|-------|
| MemFault on `runtime disable` → `runtime enable` | Low | First boot works. MMFAR=0x74737953 ("Syst"). State corruption in disable/enable cycle. |
| Syslog sem leak in firmware | Low | Hooked around. `hcom_logging_syslog` missing `sem_post` on `-ENOMEM` path. Fix upstream. |
| Firmware version string copy bug | Info | `meadow_os_copy_version_strings:121` copies `short_string` to `long_string` field. |
| ESPCP polling disabled | Medium | Needed for WiFi/BLE. Must re-enable after Mono init without CPU starvation. |
| Deploy throughput slow via CLI | Low | Byte-by-byte UART. Pre-populated LFS image is the workaround. |
| Wall time vs virtual time | Architectural | Any firmware timeout guarding external I/O needs a skip hook. |

## Decision Log

| Date | Decision | Rationale |
|------|----------|-----------|
| 2026-03-10 | LFS v1.7.2 for image builder | Firmware uses LFS v1.1 on-disk format; only v1.x library can produce it. |
| 2026-03-11 | C# hooks replace IronPython | 10–100x faster. IronPython hooks were the original "stuck" bottleneck. |
| 2026-03-11 | Patch Renode RTC, not hooks | BBR SRAM hooks worked for kernel functions but NOT for ioctl `putreg32`/`getreg32`. |
| 2026-03-11 | Fork Renode | `bryancostanich/renode`. RTC fix + SVC/PRIMASK fix. Build with `--host-arch aarch64`. |
| 2026-03-11 | Download watchdog bypass | Virtual/wall time mismatch: 3s POSIX timer fires before TCP data arrives. General pattern for all external I/O timeouts. |
| 2026-03-11 | Pre-populated LFS image | Bypass slow HCOM transfer. Build LFS v1 offline, load via `sysbus LoadBinary`. |
| 2026-03-11 | Syslog deadlock workaround | Hook to skip `hcom_logging_syslog`. Firmware bug — sem leak on malloc failure. |
| 2026-03-12 | Board version → F7FeatherV2 | Blinky targets `F7FeatherBase`, needs version=2 not 3. |
| 2026-03-12 | SVC fix in Renode NVIC.cs | Root cause of 96,000x load. 12,000x speedup. Hooks on hot paths are catastrophic. |
| 2026-03-12 | Firmware uses JIT by default | `hcom_mono_control.c:227`. No `--interp` flag. JIT was active the whole time. |

## Repo Clarification

| Repo | Path | Role |
|------|------|------|
| `Meadow` (legacy) | `/Users/bryan/Git_Repos/wilderness_labs/Meadow/` | Legacy NuttX-based Meadow OS — **what we're building from** |
| `Meadow.OS.Emulator` | worktree at `track/renode-mono` | Emulator scripts, hooks, build wrapper |
| `Meadow.CLI` | `/Users/bryan/Git_Repos/wilderness_labs/Meadow.CLI/` | Branch `feature/TCP_Socket_Connection_for_EMU` — TCP socket HCOM |
| `Meadow.Samples` | `/Users/bryan/Git_Repos/wilderness_labs/Meadow.Samples/` | Sample apps (Blinky, etc.) — needs NuGet PackageReferences |
| `renode` (fork) | `/Users/bryan/Git_Repos/bryan_costanich/renode/` | Renode fork with RTC + NVIC fixes. Build with `--host-arch aarch64`. |

## LittleFS Details

Legacy firmware uses LittleFS v1.1 format (disk version `0x00010001`). All v2 tools are incompatible.

Build the v1 image tool:
```bash
git clone --branch v1.7.2 https://github.com/littlefs-project/littlefs.git /tmp/littlefs
cc -o build_lfs_v1_image tools/build_lfs_v1_image.c /tmp/littlefs/lfs.c /tmp/littlefs/lfs_util.c \
  -I/tmp/littlefs -DLFS_NO_DEBUG -DLFS_NO_WARN -DLFS_NO_ERROR -DLFS_NO_ASSERT -O2
./build_lfs_v1_image build/legacy/assemblies build/legacy/littlefs.bin
```
