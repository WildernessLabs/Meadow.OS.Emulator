# Plan: Mono Runtime Bring-Up in Emulator

## Track ID: `renode-mono`
## Status: `planned`
## Repo: `Meadow.OS.Emulator` (sibling to `Meadow.OS/`)

---

## Phase 1: Research & Build

### 1.1 — Understand Mono Build System
- [ ] Find Mono cross-compilation scripts in Meadow.OS repo
- [ ] Identify Mono version used by Meadow
- [ ] Understand build dependencies (toolchain, BCL source, etc.)
- [ ] Document memory layout: where Mono runtime lives, where assemblies go

### 1.2 — Build Mono for Emulator
- [ ] Build cross-compiled Mono runtime targeting Cortex-M7
- [ ] Update `build-meadow.os_legacy-emulated.sh` to include Mono (replace stub)
- [ ] Verify NuttX links with real Mono instead of `libmonostub.a`
- [ ] Resolve any build issues (missing symbols, config flags, etc.)

### 1.3 — SDRAM / Memory Setup
- [ ] Determine if Mono requires SDRAM (0xC0000000 region)
- [ ] Add SDRAM emulation to Renode platform if needed
- [ ] Verify memory protection settings allow Mono to access required regions
- [ ] Check FPU requirements (Mono may use floating-point heavily)

## Phase 2: Boot Mono

### 2.1 — Enable Mono in Build
- [ ] Remove `CONFIG_EXAMPLES_MONO=n` override from emulator build
- [ ] Remove `mono_main_stub` from build script
- [ ] Rebuild and load firmware

### 2.2 — Debug Mono Init
- [ ] Trace `mono_main` execution
- [ ] Identify crashes or hangs during Mono initialization
- [ ] Create hooks/stubs for any hardware Mono accesses that Renode can't emulate
- [ ] Get Mono to print runtime version or initialization success

### 2.3 — Assembly Loading
- [ ] Determine how assemblies are deployed (QSPI flash via LittleFS?)
- [ ] Provide a test assembly (simple Meadow.NET app)
- [ ] Flash or inject the assembly into the emulated file system
- [ ] Verify Mono finds and loads the assembly

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

## Open Questions
1. Does the Meadow Mono build require specific patches to upstream Mono?
2. Is there a pre-built Mono runtime binary we can use instead of building from source?
3. What's the SDRAM configuration (size, timing) for the STM32F777ZIT6 on Meadow?
4. How are .NET assemblies deployed in production? (HCOM file transfer? OTA?)
5. Does Mono need the FPU extended frame fix (Renode FPU frame mismatch)?

## Decision Log

| Date       | Decision | Rationale |
|------------|----------|-----------|
| 2026-03-10 | Separate track | Mono is a major milestone that unlocks WiFi/network/BLE testing. Deserves focused effort. |
| 2026-03-10 | Research-first approach | Mono build system is complex and may have undocumented dependencies. Understand before building. |
