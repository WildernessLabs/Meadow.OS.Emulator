# Plan: Mono Runtime Bring-Up in Emulator

## Track ID: `renode-mono`
## Status: `in-progress` (Phase 2.14 — SVC fix in Renode, Mono boots in seconds)
## Repo: `Meadow.OS.Emulator` (sibling to `Meadow.OS/`)

---

## Phase 1: Research & Build

### 1.1 — Understand Mono Build System
- [x] Find Mono cross-compilation scripts in Meadow.OS repo
- [x] Identify Mono version used by Meadow
- [x] Understand build dependencies (toolchain, BCL source, etc.)
- [x] Document memory layout: where Mono runtime lives, where assemblies go

### 1.2 — Build Mono for Emulator
- [x] Build cross-compiled Mono runtime targeting Cortex-M7
- [x] Update `build-meadow.os_legacy-emulated.sh` to include Mono (replace stub)
- [x] Verify NuttX links with real Mono instead of `libmonostub.a`
- [x] Resolve build issues (ar race condition with `-j1`, Make.defs integration)

### 1.3 — SDRAM / Memory Setup
- [x] Determine Mono requires SDRAM (0xC0000000 region) — confirmed
- [x] Add SDRAM emulation to Renode platform (already in meadow-f7.repl)
- [x] Add FMC stub (`scripts/pydev/fmc.py`) for SDRAM initialization sequence
- [x] Mono binary loaded at both 0x90000000 (QSPI flash) and 0xC0000000 (SDRAM)
- [x] Fix 32-byte alignment: `nuttx_mono_aligned.bin` (32 zero bytes prepended)
- [ ] Check FPU requirements (Mono may use floating-point heavily)

## Phase 2: Boot Mono

### 2.1 — Enable Mono in Build
- [x] Remove `CONFIG_EXAMPLES_MONO=n` override from emulator build
- [x] Remove `mono_main_stub` from build script
- [x] Rebuild and load firmware (nuttx_user.bin = 941KB, nuttx_mono.bin = 2.2MB)
- [x] Update all 15+ hook addresses for new ELF layout after Mono linking

### 2.2 — Debug Mono Init
- [x] Fix `mono_check_bypass` preventing Mono from starting (returned "disabled")
- [x] Fix mono version string pointer NULL in deep-copied config (`mono_str_fix`)
- [x] Bypass .NET assembly DLL presence check (`asm_scan_bypass`)
- [x] Mono task (PID 13) successfully created via `task_create`
- [x] `mono_main_proxy` → `mono_main` reached
- [x] Fix crash in `mono_set_assemblies_path` — was caused by shifted binary (32-byte offset)
- [x] Solution: `nuttx_mono_aligned.bin` puts code at correct ELF addresses
- [x] Mono now executes real code (JIT compilation visible via I-cache invalidations)

### 2.3 — Assembly Loading
- [x] Discover: legacy firmware uses LittleFS **v1.1** (disk version 0x00010001), NOT v2
- [x] Build `tools/build_lfs_v1_image.c` using littlefs v1.7.2 library
- [x] LFS v1 image mounts successfully on first try (no format needed)
- [x] Assemblies deployed: mscorlib.dll (4.5MB), System.dll, System.Core.dll, Meadow.dll
- [x] Lib path patches: `/usr/local/lib` → `/meadow0` (SDRAM 0xC016D79C, 0xC0175C4E)
- [x] HCOM verified working with v1 image (hcom_test.py GET_DEVICE_INFORMATION)
- [x] **BLOCKER RESOLVED**: Replaced IronPython QSPI hook with C# `QspiDmaBypass` extension method (10-100x faster)
- [x] Optimize QSPI bypass: `peripherals/QspiDmaBypass.cs` — compiled C# hook via `cpu InstallQspiBypass`
- [x] Mono reads 13MB from LittleFS (22K QSPI calls in 4 seconds) — assemblies loaded
- [x] Verify Mono reaches `mono_main_driver` in SDRAM (0xC0022748) — confirmed

### 2.4 — ESPCP CPU Starvation Fix
- [x] Identified: ESPCP polling thread starves Mono of CPU time
- [x] Disabled `espcp_poll_enable` hook during Mono init
- [ ] Re-enable ESPCP polling AFTER Mono initialization completes
- [ ] Consider throttled polling (sleep between polls)

### 2.5 — SVC Hook Performance
- [x] Diagnosed: Mono appeared "stuck" after assembly loading — was actually throttled by IronPython SVC hook
- [x] dispatch_syscall SVC at 0x08163b5a fires for every kernel→user transition
- [x] IronPython SVC hook: ~3ms overhead × hundreds/sec = system nearly frozen
- [x] Created `peripherals/SvcPrimaskFix.cs` — compiled C# SVC PRIMASK fix
- [x] Created `peripherals/MqReceiveIntercept.cs` — compiled C# mq_receive intercept
- [x] Mono JIT now making progress: 25K SVCs in 477s, I-cache invalidations confirm JIT active
- [x] **BLOCKER**: Renode instruction emulation speed — 0.147s virtual time in 10 min real time
- [ ] Estimated 16+ hours for Mono to complete JIT boot (15s emulated time needed)

### 2.6 — Switch to Interpreter Mode
Mono was running interpreted before JIT was enabled. Interpreter skips the native code
generation phase entirely (no JIT compilation = no billions of ARM instructions for codegen).
This should cut Mono boot from 16+ hours to minutes.

- [ ] Find how Mono interpreter is enabled in the Meadow build (config flag, runtime arg, or build option)
- [ ] Check if `mono_main_driver` accepts `--interpreter` or if there's a build-time `MONO_USE_INTERPRETER`
- [ ] Check Meadow Mono build scripts for interpreter vs JIT config
- [ ] Enable interpreter mode and test boot time
- [ ] Verify assembly loading still works in interpreter mode
- [ ] Verify UART/HCOM output from interpreted Mono

### 2.7 — Firmware Rebuild with Version Injection
- [x] Read version from `Meadow/version.txt` (currently `2.5.7`)
- [x] Inject version into `hcom_nuttx_shared.h` via `build-meadow.os_legacy-emulated.sh`
- [x] Inject version into linker script's `.mono_signature` section
- [x] Extract Mono signature from ELF, pad to 32 bytes, prepend to mono binary
- [x] Fix stale object files: NuttX `distclean` doesn't clean `configs/.../src/*.o` — manual deletion needed
- [x] Firmware version 2.5.7.0 now shows in HCOM device info

### 2.8 — Mono Version Match Fix
- [x] Root cause: `hcom_nx_config_refresh_mono_version` (line 3284) called BEFORE OS version populated (lines 3285+)
- [x] Old `mono_ver_bypass` hook copied zeros from unpopulated OS version to Mono version
- [x] Split into `mono_ver_skip` (0x08143590) + `mono_ver_copy` (0x08143762)
- [x] `mono_ver_copy` fires after OS version is fully populated, copies to mono fields
- [x] Version match now passes (no more "Version mismatch" error)

### 2.9 — CLI Deploy Flow + BBR Persistence (DONE)
The Meadow CLI `app deploy` command disables Mono (sets BBR bit 0x800), triggers device restart,
reconnects, then writes files. BBR must persist across resets for this to work.

- [x] Built Meadow CLI from branch `feature/TCP_Socket_Connection_for_EMU`
- [x] CLI connects via TCP socket to port 4242 (`-s socket://localhost:4242`)
- [x] `meadow device info` returns correct device info with version 2.5.7.0
- [x] CLI deploy gets past MONO_DISABLE + reconnect step
- [x] **BLOCKER RESOLVED**: BBR (RTC backup register at 0x400028CC) resets to 0 on device restart
  - Renode's STM32F4_RTC model only implemented 20 backup registers (0x50-0x9C, STM32F4 count)
  - STM32F7 needs 32 registers (0x50-0xCC); Meadow uses BKP31R at offset 0xCC
  - After reset, BBR returned to 0 → Mono re-enabled → "Mono must be disabled for file download"
- [x] Attempted fix 1: Write BBR in reset macro — failed (RTC model ignores offset 0xCC)
- [x] Attempted fix 2: Add MappedMemory at 0x40002850 — caused Renode hang (conflicts with RTC)
- [x] Attempted fix 3: Hook all 8 `hcom_nx_bbreg_*` kernel functions → redirect to SRAM 0x2007FFE0
  - Worked for kernel-level BBR access but NOT for ioctl-level access
  - `hcom_upd_nx_ioctl` uses `putreg32`/`getreg32`/`modifyreg32` directly on 0x400028CC
  - Ioctl hooks attempted but unreliable (RegisterValue vs int type issues, address verification needed)
- [x] Attempted fix 4: Hook ioctl handler BBR cases (SET/GET/UPDATE) at 0x0814cf5e/68/72
  - Required `cpu.SetRegisterUnsafe(15, RegisterValue.Create(addr, 32))` (not `cpu.PC = int`)
  - Still unreliable — hooks may not have been reached
- [x] **FINAL FIX**: Patched Renode's STM32F4_RTC.cs in user's fork (`bryancostanich/renode`)
  - One-line change: `BackupEnd = 0x9C` → `BackupEnd = 0xCC` (line 1186)
  - Extends backup registers from 20 to 32, covering BKP31R at offset 0xCC
  - `softResettable: false` ensures values persist across resets — exactly what BBR needs
  - Both kernel functions and ioctl handlers now hit the real register natively
- [x] Built custom Renode from fork with `--host-arch aarch64` (arm64 tlib + C# rebuild)
  - Initial build produced x86_64 tlib (macOS default); fixed with `--host-arch aarch64` flag
  - Build: `export PATH="/usr/local/share/dotnet:$PATH" && bash build.sh --host-arch aarch64 --skip-fetch`
  - Run: `dotnet output/bin/Release/Renode.dll --port 1234 --disable-xwt -e "include @script.resc"`
- [x] Removed all 8 BBR kernel hooks + 3 ioctl hooks from `.resc` (no longer needed)
- [x] Removed all diagnostic trace hooks from `.resc` (cleanup)
- [x] Verified: `runtime disable` → device restarts with `Mono:Disabled` (BBR persists!)
- [x] Verified: `app deploy` gets past MONO_DISABLE, reconnects, begins file transfer
- [x] File transfer fixed — stale watchdog bug in SocketConnection (see Phase 2.10)
- [x] `file write` succeeds first attempt (was failing 100%, required ~50 retries via app deploy)
- [x] `app deploy` with trimmed binaries completes successfully (33 files, ~8MB)
- [x] `app trim` → `app deploy` → all files transferred to emulated LittleFS
- [x] Removed `littlefs.bin` reload from reset macro (was overwriting deployed files)
- [x] **RESOLVED**: "LFS format-on-boot" was a misdiagnosis (2026-03-11)
  - Investigated: `lfs_format()` is never called — diagnostic hook at 0x081714ae never fires
  - First `lfs_mount()` succeeds every boot (UART: "Mounted partition 0")
  - "mount & format" UART message is a misleading phase label, not confirmation of formatting
  - `lfs_format_skip` hook remains as safety net but is not triggered
  - MappedMemory persists across resets (Reset() is intentionally empty in Renode)
  - "Missing files" error is expected pre-deploy — firmware checks `/meadow0/mscorlib.dll` etc.
  - Original file-loss was caused by reset macro reloading `littlefs.bin` (fixed in commit f80759a)

### 2.10 — File Transfer Fix (DONE)
File writes to the emulated device were failing 100% of the time via `file write` command,
and requiring ~50 retries via `app deploy`. Root cause identified and fixed 2026-03-11.

**Root cause analysis:**
- LittleFS writes go through QSPI flash in indirect write mode (Page Program commands)
- Our `QspiDmaBypass` only handles memory-mapped reads, not indirect writes
- QSPI write path: `lfs_bd_prog` → block device → QSPI Page Program → SPI flash model
- The `qspi_waitstatusflags` skip hook may cause write timing issues
- Single `file write` command returns immediately at 0% with no file appearing on device
- `app deploy` does transfer files (netstandard.dll reached 100%) but extremely slowly
- Emulator runs at 1:1 real-time speed — confirmed via `machine GetTimeSourceInfo` — so
  emulation speed is NOT the bottleneck
- `SocketConnection.WriteFile` sends `InitFileWriteRequest`, waits for `FileWriteAccepted`
  event from firmware's `INIT_DOWNLOAD_OKAY` (0x030E) response
- **ROOT CAUSE FOUND**: Firmware's download watchdog sends `DNLD_FAIL_RESEND` (0x0312) responses
  from previous failed/abandoned file operations. These stale responses arrive in the TCP buffer
  and fire `FileWriteFailed` event, setting `needsRetry=true` BEFORE the data transfer begins.
  The data loop is then skipped (`while (true && !needsRetry)` = false), and WriteFile returns false.
- **FIX**: Added `dataTransferStarted` guard — `FileWriteFailed` events are ignored until the
  data transfer phase actually begins. Stale watchdog responses from prior operations are harmless.
- Also added retry logic (10 attempts) and TimeoutException catch as defense-in-depth
- `file write` now succeeds on first attempt (previously failed 100% of the time)

**Root cause found and fixed** — stale `DNLD_FAIL_RESEND` from firmware watchdog was poisoning file writes.
`dataTransferStarted` guard ignores stale watchdog responses. No workarounds needed.

### 2.11 — App Deploy End-to-End (IN PROGRESS)
Deploy a Meadow app via CLI and have Mono JIT start it.

**Status:** Deploy works (watchdog fixed), but transfer speed is very slow. Investigating throughput.

**What works (2026-03-11):**
- `app trim` produces trimmed binaries (~8MB, 33 files)
- `app deploy` transfers all files via HCOM socket connection
- Download watchdog bypass hook added to `.resc` (see below)
- Removed `littlefs.bin` from reset macro so deployed files survive in QSPI `MappedMemory`
- LFS mounts successfully on every boot — `lfs_format()` is never called (diagnostic hook confirmed)
- MappedMemory persists across Renode resets (Reset() is intentionally empty)

**Download watchdog fix (2026-03-11):**
- **Root cause**: Virtual time vs wall time mismatch. The firmware's 3-second POSIX download
  watchdog timer runs in virtual time, but CLI sends data over TCP (wall time). The emulated CPU
  burns through 3+ virtual seconds in the idle loop while waiting for the next TCP data packet
  (which arrives in milliseconds of wall time). The watchdog fires before data is delivered.
- **Fix**: Hook `hcom_host_watchdog_timeout_expired` at 0x0805f9c4 to skip (return immediately).
  This SIGALRM handler just sets a flag at 0x200461a0 — skipping it prevents false timeouts.
- **Impact**: File writes now succeed. Previously ALL file writes silently failed (CLI reported
  100% but firmware cancelled the download and never wrote to LFS).
- **General issue**: Any firmware timeout guarding external I/O will have this problem.
  See README.md "Wall Time vs. Virtual Time" section.

**Deploy throughput problem (2026-03-11):**
- `app deploy` of 33 files (~8MB total) takes extremely long (10+ minutes wall time)
- Bottleneck: byte-by-byte UART delivery. Each byte requires an interrupt cycle in the emulator.
  On real hardware, HCOM runs over USB at 12+ Mbps. In Renode, each byte goes through:
  TCP → ServerSocketTerminal → UART4 RX interrupt → ISR → byte buffer → HCOM decode
- For `mscorlib.dll` (~2MB), that's ~2 million individual interrupt/ISR cycles
- Potential fixes to investigate:
  - **DMA bypass for UART RX**: Hook the UART read path to inject bulk data directly into firmware buffers
  - **Direct LFS injection**: Bypass HCOM entirely, write files directly to QSPI memory using LFS tooling
  - **Bake a pre-populated LFS image**: Build the LFS image offline with app files already included
  - **Tune Renode quantum/performance**: Increase quantum to reduce sync overhead per byte

**LFS "format-on-boot" investigation (resolved 2026-03-11):**
- "mount & format" UART message is a misleading phase label — firmware logs it before attempting
  mount, regardless of whether format will be needed
- Added diagnostic hook at `lfs_format` (0x081714ae) with logging — never fires during boot
- First `lfs_mount()` succeeds immediately (valid superblock in littlefs.bin)
- Firmware's mount flow: try mount → if LFS_ERR_CORRUPT, format+remount; otherwise return OK
- The `lfs_format_skip` hook remains as defense-in-depth but is not triggered in practice
- "Missing files" error (mscorlib.dll etc.) is expected — firmware checks `/meadow0/` root,
  files only appear there after `app deploy`

**Pre-populated LFS image (2026-03-11):**
- Built LFS v1 image offline with `build_lfs_v1_image` tool (app files + BCL assemblies + meadow.config.yaml)
- Loaded via `sysbus LoadBinary` at 0x90500000 — bypasses HCOM entirely, instant deploy
- Firmware boots, mounts LFS, finds all 38 files, starts Mono successfully
- `device info` shows `Runtime: 2.5.7.0` (was blank before Mono)
- `file list` shows all files including `meadow.log` (created by Mono)
- `meadow.log` contains: "Logging started" + "Config file successfully processed"

**Syslog semaphore deadlock (FIXED 2026-03-11):**
- Mono started successfully but immediately deadlocked on `_f7syslogSem` (0x20046164)
- Root cause: `hcom_logging_syslog()` in `hcom_diag_logging.c` has a semaphore leak bug
  - When `hcom_diag_logging_build_syslog_string()` returns `-ENOMEM` (malloc failure),
    the function returns WITHOUT calling `sem_post(&_f7syslogSem)` (line 409-411)
  - This permanently deadlocks all subsequent syslog calls from any thread
  - In emulator, malloc failures happen due to constrained GC heap (`max-heap-size=16m`)
- Diagnosis: Used GDB via Renode monitor to inspect NuttX TCBs
  - All Mono pthreads (PIDs 9-14) in `g_waitingforsemaphore`
  - PID 13 (Mono main) `waitsem = 0x20046164` = `_f7syslogSem`
  - `_f7syslogSem.semcount = -1` — locked with 1 waiter, no holder running
- Fix: Hook `hcom_logging_syslog` at 0x0805cf78 to skip (`cpu.PC = cpu.LR`)
  - Disables user-space syslog (boot-time kernel syslog via UART unaffected)
  - Mono's managed logging uses `meadow.log` file, not syslog
- Firmware bug should be fixed upstream (add `sem_post` before `-ENOMEM` return)

**Mono interpreter status (2026-03-11, SUPERSEDED):**
- After syslog fix, Mono was actively running (load ~5000-10000x)
- Virtual time advanced very slowly: 0.035s per minute of wall time
- Root cause: NOT Mono interpreter overhead — was SVC hook overhead (see Phase 2.14)

**Board version fix (2026-03-12):**
- App threw `System.Exception: Cannot find an IApp that targets a F7CoreComputeV2 device`
- GPIO board version hook was returning 3 (F7CoreComputeV2), Blinky app targets F7FeatherV2
- Fixed: Changed hook to return 2 (F7FeatherV2). Values: 1=F7FeatherV1, 2=F7FeatherV2, 3=F7CoreComputeV2
- Source: `meadow_hw_version.c` lines 99-101 define the version→name mapping

**SVC PRIMASK fix in Renode source (2026-03-12, MAJOR BREAKTHROUGH):**
- Root cause of ALL emulation slowness: SVC hook overhead, NOT Mono interpretation
- SvcPrimaskFix.cs hooks fired on every syscall. Even compiled C# hooks break Renode's
  translation blocks, forcing re-translation and cross-boundary calls thousands of times/sec
- Measured: 6,600 effective instructions/second (vs QEMU's ~100M+). Load 70,000-96,000x
- **Fix**: Patched `NVIC.cs` in Renode fork (`bryancostanich/renode`)
  - `ShouldRaiseException()` now lets SVCall through when PRIMASK is set
  - Previously only HardFault passed through PRIMASK (line 1535)
  - Added `|| excp == (int)SystemException.SuperVisorCall` to the check
  - Per ARMv7-M ARM B1.5.14, SVC is synchronous and should escalate to HardFault
    when masked, not be silently skipped. Letting it through is functionally equivalent
    to the old PRIMASK-clearing hook.
- **Result**: 12,000x speedup. Load dropped from 96,000x to 5-8x (near real-time)
  - 0.01 virtual sec per 10 wall min → 100 virtual sec per 2 wall min
  - Mono boots in seconds instead of hours
  - SvcPrimaskFix.cs hook removed from `.resc` script
- **Confirmed**: Firmware was using JIT mode (not interpreter) the entire time.
  `mono_main` receives `{"mono", "/meadow0/Meadow.dll"}` — no `--interp` flag.
  The default `run_method` in `hcom_mono_control.c` line 227 is `MONO_OPTION_JIT`.

**MemFault on runtime re-enable (2026-03-11):**
- After `runtime disable` + `runtime enable`, Mono crashes with MemFault
- MMFAR: 0x74737953 = ASCII "Syst" — string being dereferenced as pointer
- PC: 0x0805d31a, LR: 0x0805d313, Task: Mono (PID 13)
- Stack contains "/meadow0/Meadow.dll" and "Mono\0--i" strings
- Only happens on re-enable, NOT on first boot — likely state corruption from disable/enable cycle
- Non-blocking: first boot works fine, and that's the primary use case for emulator

**Renode state save options:**
- `Save @path` — full machine snapshot (CPU, memory, peripherals)
- `sysbus.qspi DumpBinary @backup.bin` — QSPI flash contents only
- Plan: let Mono run overnight, save state once app produces output

**Next steps:**
- [x] Deploy trimmed Blinky app via pre-populated LFS image — working
- [x] `runtime enable` on first boot — Mono starts successfully
- [ ] Let Mono interpreter run overnight — waiting for first app output
- [ ] Save Renode state snapshot once Mono completes initialization
- [ ] Investigate MemFault on runtime re-enable (low priority — first boot works)
- [ ] Investigate deploy throughput — UART byte-by-byte interrupt path is the bottleneck

### 2.14 — SVC Fix in Renode Source (DONE)
Fix the root cause of SVC-with-PRIMASK behavior in Renode's NVIC model, eliminating the
need for per-address SVC hooks entirely.

- [x] Identified root cause in `NVIC.cs:ShouldRaiseException()` — SVCall masked by PRIMASK
- [x] Patched: Added `|| excp == (int)SystemException.SuperVisorCall` to PRIMASK check
- [x] Rebuilt Renode fork with `bash build.sh --host-arch aarch64 --skip-fetch`
- [x] Removed `InstallSvcFix` line and `SvcPrimaskFix.cs` include from `.resc` script
- [x] Verified: NuttX boots, Mono starts, 12,000x speedup confirmed
- [x] Updated hooks table and performance measurements
- [ ] Upstream this fix to Renode (Antmicro) — clear spec compliance bug

### 2.15 — Renode Execution Speed Investigation (TODO)
Even with SVC hooks removed, Renode's instruction throughput is a concern for heavier workloads.
Investigate Renode's tlib execution model and potential optimizations.

**Findings so far:**
- With SVC fix, load is 5-8x for Mono boot — acceptable for current use
- QEMU TCG achieves ~100M-1B instructions/sec; Renode's tlib (derived from old QEMU TCG) is slower
- Remaining hooks (18 total) still break translation blocks, but only on cold paths
- Quantum is 100μs (46,200 instructions) — reasonable, not a bottleneck

**Work items:**
- [ ] Profile remaining hooks — which ones fire most frequently during Mono execution?
- [ ] Investigate Renode tlib vs QEMU TCG differences — what accounts for the gap?
- [ ] Test increasing quantum (e.g., 1ms, 10ms) to reduce sync overhead
- [ ] Test reducing `PerformanceInMips` to see if it improves wall-clock throughput
- [ ] Explore Renode's `ExecutionMode` options (if any) for faster CPU emulation
- [ ] Consider QEMU as alternative for CPU-intensive workloads (peripheral stubs needed)
- [ ] Investigate if Renode supports multi-threaded translation or block caching optimizations

### 2.12 — AOT Compilation (FUTURE)
Ahead-of-time compilation pre-compiles IL → native ARM at build time. Longer-term goal that
pairs well with the interpreter: AOT for the hot path, interpreter as fallback for anything
that can't be AOT'd (generics, reflection, dynamic code).

**Context**: Older Mono AOT has significant limitations — no dynamic generics, limited
reflection support, no `Reflection.Emit`. Newer .NET/Mono versions fix much of this.
The plan is to eventually move to newer .NET, which has better AOT + interpreter hybrid support.

**Investigation findings (2026-03-11):** The Meadow firmware already whitelists AOT-related
Mono runtime options in `hcom_mono_control.c`:
- `--aot` — Ahead-of-time compiled assemblies
- `--llvmonly` — LLVM-only compilation backend
- `--llvmonly-interp` — LLVM with interpreter fallback (hybrid mode, ideal for emulator)
These are passed through `meadow.config.yaml` `MonoControl.Options` field.
The runtime parsing is ready; the gap is building AOT native images for the target assemblies.

- [x] Investigate Meadow Mono build config for AOT cross-compiler support
  - Runtime supports `--aot`, `--llvmonly`, `--llvmonly-interp` options (whitelisted)
  - No AOT cross-compiler build infrastructure in repo yet (Mono binary is pre-built)
  - Mono source/build scripts not in Meadow.OS repo — binary deployed directly
- [ ] Find or build Mono AOT cross-compiler for ARMv7 (needs Mono source with `--enable-llvm`)
- [ ] Determine if `mono --aot=full --aot-options=mtriple=armv7-...` works with Meadow's Mono fork
- [ ] Identify which assemblies are AOT-safe vs need interpreter fallback
- [ ] Build AOT native images for BCL assemblies (mscorlib, System, System.Core)
- [ ] Deploy AOT images alongside IL assemblies in LittleFS
- [ ] Test `--llvmonly-interp` hybrid boot (AOT + interpreter fallback)

### 2.13 — Overnight JIT Validation Run (OPTIONAL)
Let the JIT emulator run for 16+ hours to validate the full JIT path works end-to-end.
Low priority — only useful as a correctness check, not a practical workflow.

- [ ] Run emulator overnight with full JIT
- [ ] Verify Mono completes initialization
- [ ] Capture any errors or hangs beyond the JIT phase

## Phase 3: Application Execution

### 3.1 — Simple App
- [ ] Run a minimal Meadow.NET app (e.g., `Console.WriteLine("Hello from Meadow")`)
- [ ] Verify output appears on syslog/console
- [ ] Test application lifecycle: start, run, complete

### 3.2 — GPIO App
- [ ] Run a Meadow.NET app that toggles GPIO pins
- [ ] Verify GPIO state changes are visible in Renode

### 3.3 — WiFi App (depends on `renode-network`)
- [ ] Run a Meadow.NET app that connects to WiFi
- [ ] Verify ESPCP messages flow through the stub/bridge
- [ ] Verify network operations complete

---

## LittleFS Discovery Details

**Root cause of all previous LFS mount failures:** The legacy firmware uses LittleFS v1.1 format (disk version 0x00010001). The NuttX repo's `lfs.c` was updated to v2.5 AFTER the legacy firmware was compiled. All v2-format images (from `tools/build_littlefs_image.py`, `tools/build_littlefs_image.c`, or littlefs-python) are incompatible.

**Evidence:**
- Firmware traces showed `lfs_mount` returning -EFAULT (-14, mapped from LFS_ERR_CORRUPT -84)
- Captured firmware's `lfs_format` output: superblock has `version=0x00010001`, v1 metadata structure
- All upstream LFS v2 versions (2.0.5 through 2.5.1) produce identical v2 format — none are v1 compatible
- LFS v1 on-disk format: `rev(4) + size(4) + tail[2](8) + entries(type/elen/alen/nlen + data + name) + CRC(4)`

**Solution:** `tools/build_lfs_v1_image.c` uses littlefs v1.7.2 library (checked out at `/tmp/littlefs/` from GitHub). Requires the v1.7.2 source to compile:
```bash
git clone --branch v1.7.2 https://github.com/littlefs-project/littlefs.git /tmp/littlefs
cc -o build_lfs_v1_image tools/build_lfs_v1_image.c /tmp/littlefs/lfs.c /tmp/littlefs/lfs_util.c \
  -I/tmp/littlefs -DLFS_NO_DEBUG -DLFS_NO_WARN -DLFS_NO_ERROR -DLFS_NO_ASSERT -O2
./build_lfs_v1_image build/legacy/assemblies build/legacy/littlefs.bin
```

## Hooks Required for Mono Startup (addresses as of 2026-03-11 rebuild)

| Hook | Address | Purpose |
|------|---------|---------|
| `mono_copy_bypass` | 0x0805eb98 | Skip QSPI→SDRAM copy (pre-loaded via LoadBinary) |
| `mono_ver_skip` | 0x08143590 | Skip `refresh_mono_version` (reads unimplemented QSPI MTD) |
| `mono_ver_copy` | 0x08143762 | After OS version populated, copy to mono version fields |
| `mono_str_fix` | 0x080618e8 | Patch deep-copied config's mono version string (+0x80) |
| `asm_scan_bypass` | 0x08061a1c | Skip .NET assembly DLL presence check (currently disabled) |
| `lfs_format_skip` | 0x081714ae | Prevent LFS format from wiping pre-populated image |
| `gpio_ver_fix` | 0x08141970 | Force board version to F7FeatherV2 (2) |
| `espcp_spi_bypass` | 0x081480cc | Bypass SPI DMA with direct peripheral calls |
| `espcp_force_init` | 0x08141d50 | Force ESPCP init (set network type to WiFi) |
| `spi_lock_skip` | 0x08148144, 0x08148150 | Skip SPI lock/unlock (DMA bypassed) |
| `lsi_skip` | 0x0814d2c0 | Skip LSI clock calibration thread |
| `syslog_skip` | 0x0805cf78 | Skip `hcom_logging_syslog` (sem leak deadlocks Mono) |

**BBR hooks removed** — STM32F4_RTC.cs patched (`BackupEnd = 0xCC`) in `bryancostanich/renode` fork.
Backup registers now handled natively by the RTC model with `softResettable: false`.

## C# Performance Hooks

| Hook | File | Purpose |
|------|------|---------|
| `InstallQspiBypass` | `peripherals/QspiDmaBypass.cs` | QSPI DMA bypass — direct memcpy via SystemBus |
| ~~`InstallSvcFix`~~ | ~~`peripherals/SvcPrimaskFix.cs`~~ | **REMOVED** — Fixed in Renode NVIC.cs (2026-03-12) |
| `InstallMqIntercept` | `peripherals/MqReceiveIntercept.cs` | ESPCP polling via mq_receive interception |

## Performance Analysis

| Component | Overhead | Impact |
|-----------|----------|--------|
| ~~qspi_dma_bypass (IronPython)~~ | ~~0.5-1ms/read~~ | **FIXED**: C# `QspiDmaBypass` (~0.005-0.01ms/read) |
| ~~svc_fix (IronPython)~~ | ~~3ms/call~~ | **FIXED**: C# `SvcPrimaskFix` (negligible) |
| ~~mq_intercept (IronPython)~~ | ~~0.1ms/call~~ | **FIXED**: C# `MqReceiveIntercept` (negligible) |
| espcp_spi_bypass (IronPython) | ~0.5ms/call | SPI exchange per poll cycle (disabled during Mono init) |
| ESPCP polling loop | consumes all CPU | starves Mono thread (FIXED: disabled) |
| **Renode ARM emulation** | **~6,600 instr/sec (with hooks)** | **FIXED**: SVC hooks were the bottleneck, not Renode itself |

## Emulation Speed Measurements

| Metric | Before SVC Fix | After SVC Fix (2026-03-12) |
|--------|---------------|---------------------------|
| Virtual time per 10 min wall | 0.01-0.07 seconds | ~600 seconds |
| Emulation ratio (Mono active) | 0.00001-0.0001x | ~0.15x (near real-time) |
| Effective instructions/sec | ~6,600 | ~70,000,000 (estimated) |
| Load factor | 70,000-96,000x | 5-8x |
| Mono boot time | 8-24+ hours | ~2-3 minutes |
| Real hardware Mono boot | ~15-30 seconds | — |
| QSPI reads (assembly load) | 22K calls, 13MB, 4 seconds real time | unchanged |

## Decision Log

| Date       | Decision | Rationale |
|------------|----------|-----------|
| 2026-03-10 | Separate track | Mono is a major milestone that unlocks WiFi/network/BLE testing. |
| 2026-03-10 | Research-first approach | Mono build system is complex and may have undocumented dependencies. |
| 2026-03-10 | Bypass assembly check | No DLLs deployed to filesystem yet; firmware refuses to start Mono otherwise. |
| 2026-03-10 | Remove mono_check_bypass | Was returning "mono disabled" — same function used for is_mono_enabled check. |
| 2026-03-10 | Use nuttx_mono_aligned.bin | 32-byte prepend puts code at correct ELF addresses (0xC0000020+). |
| 2026-03-10 | LFS v1.7.2 for image builder | Firmware uses LFS v1.1 on-disk format; only v1.x library can produce it. |
| 2026-03-11 | Disable ESPCP polling | CPU starvation — espcp_thread's tight loop starves Mono init thread. |
| 2026-03-11 | C# QSPI hook | `QspiDmaBypass.cs` replaces IronPython hook. 10-100x faster. |
| 2026-03-11 | C# SVC fix | `SvcPrimaskFix.cs` replaces IronPython svc_fix. Was the "stuck" bottleneck. |
| 2026-03-11 | C# mq_intercept | `MqReceiveIntercept.cs` replaces IronPython. Fires on all mq_receive calls. |
| 2026-03-11 | Emulation speed blocker | 0.147s virtual time in 10 min. Need AOT or cache solution for Mono JIT. |
| 2026-03-11 | Interpreter first, AOT later | Mono ran interpreted before JIT was enabled — switch back for emulator. AOT is longer-term; older Mono AOT has limitations (no dynamic generics, limited reflection). Newer .NET/Mono fixes these. Plan: interpreter now, AOT + interpreter hybrid later. |
| 2026-03-11 | Remove littlefs.bin from reset macro | Reset macro was reloading pristine LFS image, wiping deployed files. Removed so QSPI MappedMemory retains deployed data across resets. |
| 2026-03-11 | LFS format-on-boot was misdiagnosis | Investigated: `lfs_format()` never called (diagnostic hook confirmed). First `lfs_mount()` succeeds. "mount & format" UART msg is just a phase label. Original file loss was from reset macro reloading littlefs.bin (already fixed). |
| 2026-03-11 | No Renode state save | Rejected machine state snapshots — fragile across versions, not a real solution. |
| 2026-03-11 | Overnight JIT run optional | Low priority correctness check only. Not blocking interpreter work. |
| 2026-03-11 | Use NuGet refs for samples | Meadow.Samples repo has ProjectReferences to sibling source repos; must change to PackageReference (Meadow.F7 2.5.0, Meadow.Foundation 2.5.0) to build standalone. |
| 2026-03-11 | HCOM download fix (PR #796) | WildernessLabs/Meadow PR #796 (merged 2026-03-07) fixes cirbuf race + semaphore in HCOM file download. Now at HEAD of legacy Meadow repo (`8f0e22bcb83`). Firmware rebuild needed to pick it up. |
| 2026-03-11 | Split mono_ver_bypass | Old single hook fired before OS version was populated → copied zeros. New: skip + copy-after. |
| 2026-03-11 | BBR → SRAM redirect | RTC backup registers not emulated. Hook all 8 bbreg functions to redirect to SRAM 0x2007FFE0. Persists across reset because reset macro doesn't touch SRAM. |
| 2026-03-11 | Renode `--port` mode | `--console` mode exits when stdin hits EOF (backgrounded process). Use `--port 1234` for telnet-based monitor instead. |
| 2026-03-11 | Known firmware bug | `meadow_os_copy_version_strings` line 121: copies `source->short_string` to `destination->long_string` instead of `source->long_string`. Non-blocking but worth tracking. |
| 2026-03-11 | AOT runtime support confirmed | Firmware whitelists `--aot`, `--llvmonly`, `--llvmonly-interp`. No AOT cross-compiler built yet. |
| 2026-03-11 | Started overnight interpreter run | `--interp` via `meadow.config.yaml` in LFS image. Logging to `/tmp/renode-mono-overnight.log`. |
| 2026-03-11 | Patch Renode RTC, not hooks | BBR SRAM hooks worked for kernel `hcom_nx_bbreg_*` functions but NOT for ioctl handler which uses `putreg32`/`getreg32` directly. Fixing the RTC model is the correct solution. |
| 2026-03-11 | Fork Renode for RTC fix | One-line change (`BackupEnd = 0x9C` → `0xCC`) in `STM32F4_RTC.cs`. Fork: `bryancostanich/renode`. |
| 2026-03-12 | Board version → F7FeatherV2 | Blinky app's `MeadowApp<T> where T : F7FeatherBase` needs version=2, not 3 (CCMv2). |
| 2026-03-12 | SVC fix in Renode NVIC.cs | Root cause of 96,000x load: SVC hooks broke translation blocks. Fix in NVIC lets SVCall through PRIMASK. 12,000x speedup. |
| 2026-03-12 | Firmware uses JIT by default | `hcom_mono_control.c` line 227: `run_method = MONO_OPTION_JIT`. No `--interp` flag passed. JIT was active the whole time. |
| 2026-03-12 | Hook overhead is catastrophic | Even compiled C# hooks that fire on hot paths (thousands/sec) destroy Renode performance by breaking translation blocks. Any future hooks must avoid hot paths. |
| 2026-03-11 | `--host-arch aarch64` | macOS Renode build defaults to x86_64 tlib. Must pass `--host-arch aarch64` for Apple Silicon. |
| 2026-03-11 | Run via `dotnet` not binary | Custom build uses `dotnet Renode.dll` (not native single-file `Renode` binary) since tlib is separate. |
| 2026-03-11 | Download watchdog bypass | Virtual time vs wall time mismatch: 3-second POSIX timer fires before TCP data arrives. Hook `hcom_host_watchdog_timeout_expired` (0x0805f9c4) to skip. General pattern for all external I/O timeouts. |
| 2026-03-11 | Wall time vs virtual time documented | Added README section. Any firmware timeout guarding external I/O will have this problem — WiFi, BLE, HCOM, cloud. Each needs a similar hook or architectural fix. |
| 2026-03-11 | WriteFile stale watchdog fix | Root cause: firmware's download watchdog sends `DNLD_FAIL_RESEND` from prior abandoned ops. Fix: `dataTransferStarted` guard ignores `FileWriteFailed` until data phase begins. Also added retry (10x) as defense-in-depth. `file write` now succeeds first attempt. |
| 2026-03-11 | Pre-populated LFS image | Bypass slow HCOM file transfer entirely — build LFS v1 image offline with all app+BCL files, load via `sysbus LoadBinary`. Instant deploy, Mono starts on first boot. |
| 2026-03-11 | MemFault on re-enable | Mono crashes after disable/enable cycle with MMFAR=0x74737953 ("Syst"). First boot works. Likely state corruption in firmware's enable/disable path — not blocking. |
| 2026-03-11 | Syslog deadlock found + fixed | `hcom_logging_syslog` has sem leak when malloc fails: returns without `sem_post(_f7syslogSem)`. All Mono threads deadlock. Hook to skip function entirely. Firmware bug — should fix upstream. |

## Repo Clarification

| Repo | Path | Role |
|------|------|------|
| `Meadow` (legacy) | `/Users/bryan/Git_Repos/wilderness_labs/Meadow/` | Legacy NuttX-based Meadow OS — **what we're building from** |
| `Meadow.OS` (new) | `/Users/bryan/Git_Repos/wilderness_labs/Meadow.OS/` | New rebase — does NOT have proper Mono bringup yet, not used |
| `Meadow.OS.Emulator` | worktree at `track/renode-mono` | Emulator scripts, hooks, build wrapper. Build script uses `../Meadow/` as source |
| `Meadow.CLI` | `/Users/bryan/Git_Repos/wilderness_labs/Meadow.CLI/` | Branch `feature/TCP_Socket_Connection_for_EMU` — TCP socket HCOM for emulator |
| `Meadow.Samples` | `/Users/bryan/Git_Repos/wilderness_labs/Meadow.Samples/` | Sample apps (Blinky, etc.) — needs NuGet PackageReferences to build standalone |
| `renode` (fork) | `/Users/bryan/Git_Repos/bryan_costanich/renode/` | Renode fork with STM32F4_RTC backup register fix + NVIC SVC/PRIMASK fix. Build with `--host-arch aarch64`. |
