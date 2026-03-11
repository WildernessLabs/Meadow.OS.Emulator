# Plan: Mono Runtime Bring-Up in Emulator

## Track ID: `renode-mono`
## Status: `in-progress` (Phase 2.2 — Mono task starts, crashes in assembly path setup)
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
- [ ] Fix crash in `mono_set_assemblies_path` (MMFAR: 0xFFFFFFFC, invalid path pointer)
- [ ] Get Mono to print runtime version or initialization success

### 2.3 — Assembly Loading
- [ ] Deploy .NET assemblies to LittleFS filesystem (mscorlib.dll, System.Core.dll, etc.)
- [ ] Provide a test assembly (simple Meadow.NET app)
- [ ] Verify Mono finds and loads assemblies
- [ ] Verify Mono reaches `mono_main_driver` in SDRAM (0xC0022748)

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

## Hooks Required for Mono Startup

| Hook | Address | Purpose |
|------|---------|---------|
| `mono_copy_bypass` | 0x0805ebf0 | Skip QSPI→SDRAM copy (pre-loaded via LoadBinary) |
| `mono_ver_bypass` | 0x08143590 | Copy fw version to mono version fields + string ptrs |
| `mono_str_fix` | 0x08061940 | Patch deep-copied config's mono version string (+0x80) |
| `asm_scan_bypass` | 0x08061a74 | Skip .NET assembly DLL presence check |

Config struct layout:
- +0x30..0x3C = firmware version numbers (4 dwords)
- +0x54 = firmware short version string pointer
- +0x5C..0x68 = mono version numbers (must match firmware)
- +0x80 = mono short version string pointer (must be non-NULL)
- +0x84 = mono long version string pointer

## Current Crash Details

```
up_memfault: PANIC!!! Memory Management Fault:
  CFAULTS: 00000082 MMFAR: fffffffc
  Data access violation
  task: Mono (PID 13)
  PC: c00d42e6 (mono_set_assemblies_path, assembly.c:458)
  LR: 08068c8d (mono_main, mono_main.c:279)
  R5: fffffffc (the invalid address being accessed)
```

## Decision Log

| Date       | Decision | Rationale |
|------------|----------|-----------|
| 2026-03-10 | Separate track | Mono is a major milestone that unlocks WiFi/network/BLE testing. |
| 2026-03-10 | Research-first approach | Mono build system is complex and may have undocumented dependencies. |
| 2026-03-10 | Bypass assembly check | No DLLs deployed to filesystem yet; firmware refuses to start Mono otherwise. |
| 2026-03-10 | Remove mono_check_bypass | Was returning "mono disabled" — same function used for is_mono_enabled check. |
