# Plan: Mono Runtime Bring-Up in Emulator

## Track ID: `renode-mono`
## Status: `in-progress` (Phase 2.6 — need to switch Mono to interpreter mode)
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

### 2.5 — SVC Hook Performance (IN PROGRESS)
- [x] Diagnosed: Mono appeared "stuck" after assembly loading — was actually throttled by IronPython SVC hook
- [x] dispatch_syscall SVC at 0x08163b5a fires for every kernel→user transition
- [x] IronPython SVC hook: ~3ms overhead × hundreds/sec = system nearly frozen
- [x] Created `peripherals/SvcPrimaskFix.cs` — compiled C# SVC PRIMASK fix
- [x] Created `peripherals/MqReceiveIntercept.cs` — compiled C# mq_receive intercept
- [x] Mono JIT now making progress: 25K SVCs in 477s, I-cache invalidations confirm JIT active
- [x] **NEW BLOCKER**: Renode instruction emulation speed — 0.147s virtual time in 10 min real time
- [ ] Estimated 16+ hours for Mono to complete JIT boot (15s emulated time needed)

### 2.6 — Switch to Interpreter Mode (NEXT)
Mono was running interpreted before JIT was enabled. Interpreter skips the native code
generation phase entirely (no JIT compilation = no billions of ARM instructions for codegen).
This should cut Mono boot from 16+ hours to minutes.

- [ ] Find how Mono interpreter is enabled in the Meadow build (config flag, runtime arg, or build option)
- [ ] Check if `mono_main_driver` accepts `--interpreter` or if there's a build-time `MONO_USE_INTERPRETER`
- [ ] Check Meadow Mono build scripts for interpreter vs JIT config
- [ ] Enable interpreter mode and test boot time
- [ ] Verify assembly loading still works in interpreter mode
- [ ] Verify UART/HCOM output from interpreted Mono

### 2.7 — AOT Compilation (FUTURE)
Ahead-of-time compilation pre-compiles IL → native ARM at build time. Longer-term goal that
pairs well with the interpreter: AOT for the hot path, interpreter as fallback for anything
that can't be AOT'd (generics, reflection, dynamic code).

**Context**: Older Mono AOT has significant limitations — no dynamic generics, limited
reflection support, no `Reflection.Emit`. Newer .NET/Mono versions fix much of this.
The plan is to eventually move to newer .NET, which has better AOT + interpreter hybrid support.

- [ ] Investigate Meadow Mono build config for AOT cross-compiler support
- [ ] Determine if `mono --aot=full --aot-options=mtriple=armv7-...` works with Meadow's Mono fork
- [ ] Identify which assemblies are AOT-safe vs need interpreter fallback
- [ ] Build AOT native images for BCL assemblies (mscorlib, System, System.Core)
- [ ] Deploy AOT images alongside IL assemblies in LittleFS
- [ ] Test AOT + interpreter hybrid boot

### 2.8 — Overnight JIT Validation Run (OPTIONAL)
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

## Hooks Required for Mono Startup

| Hook | Address | Purpose |
|------|---------|---------|
| `mono_copy_bypass` | 0x0805ebf0 | Skip QSPI→SDRAM copy (pre-loaded via LoadBinary) |
| `mono_ver_bypass` | 0x08143590 | Copy fw version to mono version fields + string ptrs |
| `mono_str_fix` | 0x08061940 | Patch deep-copied config's mono version string (+0x80) |
| `asm_scan_bypass` | 0x08061a74 | Skip .NET assembly DLL presence check |
| `lfs_format_skip` | 0x08171df6 | Prevent LFS format from wiping pre-populated image |

## C# Performance Hooks

| Hook | File | Purpose |
|------|------|---------|
| `InstallQspiBypass` | `peripherals/QspiDmaBypass.cs` | QSPI DMA bypass — direct memcpy via SystemBus |
| `InstallSvcFix` | `peripherals/SvcPrimaskFix.cs` | Clear PRIMASK before SVC instructions |
| `InstallMqIntercept` | `peripherals/MqReceiveIntercept.cs` | ESPCP polling via mq_receive interception |

## Performance Analysis

| Component | Overhead | Impact |
|-----------|----------|--------|
| ~~qspi_dma_bypass (IronPython)~~ | ~~0.5-1ms/read~~ | **FIXED**: C# `QspiDmaBypass` (~0.005-0.01ms/read) |
| ~~svc_fix (IronPython)~~ | ~~3ms/call~~ | **FIXED**: C# `SvcPrimaskFix` (negligible) |
| ~~mq_intercept (IronPython)~~ | ~~0.1ms/call~~ | **FIXED**: C# `MqReceiveIntercept` (negligible) |
| espcp_spi_bypass (IronPython) | ~0.5ms/call | SPI exchange per poll cycle (disabled during Mono init) |
| ESPCP polling loop | consumes all CPU | starves Mono thread (FIXED: disabled) |
| **Renode ARM emulation** | **~250K instr/sec** | **CURRENT BLOCKER**: Mono JIT needs billions of instructions |

## Emulation Speed Measurements

| Metric | Value |
|--------|-------|
| Virtual time after 10 min | 0.147 seconds |
| Emulation ratio | ~0.00025x real-time |
| SVC rate (declining) | 188 → 52 hits/sec over 8 min |
| QSPI reads (assembly load) | 22K calls, 13MB, 4 seconds real time |
| Estimated Mono boot time | 16+ hours (at current emulation speed) |
| Real hardware Mono boot | ~15-30 seconds |

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
| 2026-03-11 | No Renode state save | Rejected machine state snapshots — fragile across versions, not a real solution. |
| 2026-03-11 | Overnight JIT run optional | Low priority correctness check only. Not blocking interpreter work. |
