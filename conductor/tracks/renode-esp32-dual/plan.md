# Plan: Dual-Machine ESP32 Emulation

## Track ID: `renode-esp32-dual`
## Status: `in-progress` (Phase 1.1 ✅, Phase 1.2 ✅, Phase 1.3 in-progress — arch_tlb_fill bug fixed in tlib, retw workarounds under re-evaluation)
## Repo: `Meadow.OS.Emulator` (sibling to `Meadow.OS/`)

---

## Phase 1: ESP32 Boot Spike

**Goal:** Determine if Renode's Xtensa/ESP32 support can boot anything at all.

### 1.1 — Minimal ESP32 Platform Definition
- [x] Create `platforms/meadow-esp32.repl` with Xtensa CPU (`cpuType: "esp32"`)
- [x] Add memory regions: IRAM (0x40070000-0x4009FFFF), DRAM (0x3FFAE000-0x3FFFFFFF), RTC (0x50000000)
- [x] Add flash-mapped regions: DROM (0x3F400000), IROM (0x400C2000)
- [x] Add ESP32_UART for debug output (UART0 @ 0x3FF40000)
- [x] Reference Renode's `xtensa-sample-controller.repl` as starting point
- [x] Add peripheral register stubs (GPIO, SPI0-3, timers, DPORT, eFuse, IO_MUX, etc.)
- [x] Add semihosting UART as fallback debug output
- [x] Create boot test scripts: `scripts/esp32-boot-test.resc` + `scripts/esp32-boot-test-headless.resc`

### 1.2 — Boot Test: Bare-Metal Hello World ✅
- [x] Build a minimal Xtensa hello-world (bare-metal, `tests/esp32-hello/`)
  - ESP-IDF toolchain installed: `xtensa-esp32-elf-gcc 8.4.0` (esp-2020r3) at `~/.espressif/tools/`
  - Bare-metal C + assembly startup (windowed ABI init required)
- [x] Load into Renode ESP32 machine
- [x] Verify CPU executes instructions (NOP stepping: PC 0x40080000 → 0x40080009 ✅)
- [x] Verify UART accepts data (direct write to FIFO ✅)
- [x] Test CPU→UART path with ESP32-compiled code
  - `main()` writes "Hello from ESP32 Xtensa on Renode!\n" to UART0 FIFO ✅
  - 35 FIFO writes confirmed (exactly matches string length)
  - Windowed ABI works after proper WINDOWBASE/WINDOWSTART/PS init
- [x] Document what peripherals/registers cause crashes or hangs
  - No crashes with stub memory regions — all peripheral accesses silently absorbed
  - `entry` instruction crashes if window registers not initialized (fixed in startup)

### 1.3 — Boot Test: ESP-IDF Application
- [x] Fix ESP-IDF Python venv (gevent → gdbgui removed from requirements, setuptools downgraded)
- [x] Build Meadow ESP32 firmware (MeadowComms.elf — 17MB ELF, 1.3MB bin)
  - Required: CMake 3.28 (CMake 4.x incompatible), stub `build_info.h`, IRAM +1KB patch
  - IRAM overflow by 652 bytes with stock settings — patched `esp32_out.ld` to `len = 0x20400`
- [x] Load ELF into Renode — all sections map correctly:
  - DROM: 228KB at 0x3F400020, DRAM: 65KB at 0x3FFBDB60
  - IRAM: 131KB at 0x40080000, IROM: 924KB at 0x400D0020
  - RTC FAST: 100 bytes at 0x3FF80000
- [x] Created window init trampoline at 0x4009F000 (inits WINDOWBASE/START/PS, call0 → entry point)
- [x] Firmware executes without crashes or bus errors (no WARNING/ERROR in 3s of execution)
- [x] Loaded real ESP32 ROM binary (esp32-v3-rom.bin from Espressif's QEMU repo)
  - ROM provides exception vectors, window handlers, ets_printf, cache control, etc.
  - 445KB at 0x40000000, documented in `roms/README.md`
- [x] Created custom C# peripherals for reactive status bits:
  - `ESP32_RTC_CNTL.cs` — auto-sets TIME_VALID when TIME_UPDATE polled
  - `ESP32_TimerGroup.cs` — auto-sets RTC_CALI_RDY for clock calibration
  - `ESP32_DPORT.cs` — auto-sets CACHE_FLUSH_DONE for cache operations
- [x] Extended DRAM regions to cover firmware heap/stack usage (0x3FDC0000-0x3FFADFFF)
- [x] Binary patches in boot script for Renode Xtensa limitations:
  - FPU user registers (rur/wur f64r_lo/hi/f64s) — Renode doesn't implement, patched with ret.n
  - External register read (rer) — Renode doesn't support, replaced with movi a2, 0
  - ets_delay_us — patched to return immediately (CCOUNT-based delays stall emulator)
- [x] Pre-initialized g_ticks_per_us (240 for 240MHz — normally set by ROM bootloader)
- [x] Hook to set app_cpu_started flag (BSS clearing zeros pre-set values)
- [x] Firmware boot reaches `start_cpu0_default` (heap init, clock init, cache flush all pass)
- [x] Fixed malloc crash — patched malloc entry to `j heap_caps_malloc_default` (eliminated call level)
- [x] Fixed s32c1i (atomic CAS) — Renode doesn't support, patched 62 instances to l32i (safe for single-core)
- [x] Fixed __assert_func — patched to return immediately (spinlock assertions fail due to l32i patches)
- [x] **VECBASE fix (critical)**: Changed from 0x40000000 (ROM) to 0x40080000 (IRAM) where the firmware's
  window overflow/underflow handlers are. This was the root cause of ALL window-related crashes.
  Renode DOES support window overflow/underflow exceptions — they just need correct VECBASE.
  - WindowOverflow4/8/12 handlers fire correctly at VECBASE+0x00/+0x80/+0x100
  - WindowUnderflow4/8/12 handlers fire correctly at VECBASE+0x40/+0xC0/+0x140
  - This eliminates the 8-level call depth limit that required many workaround patches
- [x] Fixed esp_crosscore_int_send_yield — patched to `j vPortYield` (direct yield instead of
  triggering a software interrupt via DPORT_CPU_INTR_FROM_CPU_0_REG which requires interrupt hardware)
- [x] FreeRTOS scheduler starts (vTaskStartScheduler milestone reached)
- [x] Multiple tasks dispatch and yield via vPortYield → _frxt_dispatch (context switch works)
- [x] Fixed corrupted FreeRTOS lists — Renode's `entry` instruction occasionally gives
  vListInitialise wrong register values (off by 8 bytes). Added scan-and-repair hook at
  vTaskStartScheduler that detects the corruption pattern and fixes ~16 lists at boot.
- [x] Non-windowed replacements for hot list functions — vListInsertEnd (0x4009F060),
  vListInsert (0x4009F090), uxListRemove (0x4009F120). No entry/retw, use jx return.
  Avoids retw bug for the most frequently called functions.
- [x] Patched vTaskSwitchContext exit with inline trampoline (0x40092C33, 27 bytes) —
  manually adjusts WINDOWBASE by -1 (call4), sets WINDOWSTART, jumps to _frxt_dispatch return.
- [x] Demoted 6 IPC tasks from priority 24 to priority 0 at first dispatch (Python hook).
  IPC tasks monopolize single-core emulation since there's no second core to service them.
- [x] Timer tick simulation via Python hook at _frxt_dispatch (0x40091360):
  - Increments xTickCount every dispatch
  - Walks pxDelayedTaskList for tasks ready to wake (xItemValue <= xTickCount)
  - Moves woken tasks to appropriate pxReadyTasksLists[priority]
  - Updates xNextTaskUnblockTime and uxTopReadyPriority
- [x] **Renode retw/retw.n bug identified and confirmed**: Renode's retw reads A0 from physical
  AR[0] instead of AR[WINDOWBASE*4]. This means retw returns to the wrong address whenever
  WINDOWBASE != 0 (i.e., after any windowed call). This is a fundamental Renode Xtensa bug
  that affects ALL retw/retw.n instructions system-wide.
  - ~247 retw (3-byte) and ~803 retw.n (2-byte) in IRAM alone; ~6598 retw.n total
  - Fix for _frxt_dispatch resume: trampoline at 0x4009F040 copies correct A0 to AR[0]
    via temporary WINDOWBASE=0, then executes retw.n (Renode reads AR[0] → correct value)
  - Fix for xQueueGenericReceive/xQueueGenericSend: deferred `j 0x4009F040` patches applied
    after scheduler starts (can't apply during init since these functions are used in queue setup)
  - vTaskSwitchContext: handled by inline trampoline (manual WB adjust + j, no retw)
  - **Problem: can't patch 2-byte retw.n with 3-byte j instruction**
- [x] **arch_tlb_fill bug fixed (U.8)**: Root cause of "SYSCALL on s32i.n" was `arch_tlb_fill`
  returning `TRANSLATE_FAIL` (==1==`SYSCALL_CAUSE`) instead of the real MMU cause code. Fixed
  in tlib (`bryancostanich/renode`, branch `fix/xtensa-retw`, commit `2520925`). This caused
  cascading PC corruption via the syscall handler adding 3 to EPC1 for non-SYSCALL instructions.
- [ ] **Re-evaluate retw workarounds**: Code analysis shows retw implementation in tlib is
  correct (`cpu_R[0]` = `env->regs[0]` = logical A0, properly synced via window mechanism).
  The "retw bug" may have been a misdiagnosis — symptoms caused by arch_tlb_fill's fake
  SYSCALL exceptions corrupting EPC1. Need to test with retw workarounds removed.
- [ ] **Simplify boot script**: Remove retw trampolines, non-windowed list functions, and
  deferred retw patches if retw works correctly after arch_tlb_fill fix.
- [ ] Map 0x60000000 (UART FIFO alternate bus address) as a peripheral to capture debug output
- [ ] Catalog all remaining hardware accesses that fail

## Phase 2: SPI Bridge Peripheral

**Goal:** Connect SPI buses across two Renode machines.

### 2.1 — Design SPI Bridge
- [ ] C# peripheral implementing ISPIPeripheral on both sides
- [ ] Machine A (STM32): appears as SPI slave on SPI2 (replaces EspcpStub)
- [ ] Machine B (ESP32): appears as SPI master or hooks into SPI slave driver
- [ ] Frame buffering: capture full MOSI frame, forward to other machine, return MISO
- [ ] GPIO forwarding: Ready, MessageWaiting, Reset signals between machines

### 2.2 — Implement and Test Bridge
- [ ] Create `peripherals/SpiBridge.cs`
- [ ] Test with loopback (single machine, bridge to self)
- [ ] Test with two machines (STM32 ↔ ESP32)
- [ ] Verify frame integrity (CRC checks pass on both sides)

## Phase 3: ESP32 Peripheral Stubs

**Goal:** Stub hardware that ESP-IDF requires but Renode can't emulate.

### 3.1 — WiFi Hardware Stub
- [ ] Identify WiFi hardware registers accessed during `esp_wifi_init`/`esp_wifi_start`
- [ ] Create pydev or C# stub that returns success for WiFi hardware init
- [ ] OR: build ESP32 firmware variant with compile-time WiFi mock

### 3.2 — BLE Hardware Stub
- [ ] Same approach as WiFi — stub Bluetooth controller registers
- [ ] OR: disable BLE in ESP32 build for initial bring-up

### 3.3 — Flash/NVS Stub
- [ ] Stub SPI flash controller for NVS reads/writes
- [ ] Provide initial NVS partition data (device name, MAC, config)

## Phase 4: Integration

**Goal:** Both machines running real firmware, communicating via ESPCP.

### 4.1 — Two-Machine Script
- [ ] Create `scripts/meadow-dual.resc` (both machines + bridge + GPIO wiring)
- [ ] STM32 side: same as current `meadow-legacy.resc` but without EspcpStub
- [ ] ESP32 side: new machine with ESP32 firmware + peripheral stubs

### 4.2 — Validation
- [ ] ESPCP boot handshake completes (STM32 ↔ real ESP32 firmware)
- [ ] System config exchange works
- [ ] WiFi start/connect works (with stubbed radio)
- [ ] Compare SPI traces with stub approach to verify protocol fidelity
- [ ] HCOM commands work alongside dual-machine ESPCP

---

## Research Findings

### Renode Xtensa/ESP32 Support
- Xtensa CPU types available: `sample_controller`, `esp32`, `esp32s2`, `esp32s3`
- `ESP32_UART` peripheral exists in Renode (256-byte register range, FIFO-based TX/RX)
- **ESP32_UART is the ONLY dedicated ESP32 peripheral** — everything else must be stubbed with MappedMemory
- No pre-built ESP32 board definition — must create from scratch
- No pre-built ESP32 test binaries on Antmicro's download server
- BLE medium support exists (`CreateBLEMedium`)
- **`esp32` cpuType has different ISA encoding than `sample_controller`** — Zephyr sample_controller binaries produce "unrecognized opcode" on esp32 cpuType. Must use ESP32-compiled binaries.
- Renode reference files: `platforms/cpus/xtensa-sample-controller.repl`, `scripts/single-node/xtensa.resc`, `tests/platforms/xtensa.robot`
- Platform loads cleanly with 30+ peripheral stub regions — no registration conflicts

### Meadow ESP32 Firmware
- Standard ESP32, ESP-IDF 4.2.4
- SPI slave on VSPI_HOST (SPI3), Mode 3
- Pins: CS=GPIO5, CLK=GPIO18, MOSI=GPIO23, MISO=GPIO19
- Handshake: GPIO0 (SPI Ready), GPIO3 (Message Waiting)
- Components: SpiTransportProvider, MessageDispatcher, WiFi/BLE/System handlers
- Source: `Meadow.OS/esp32/`, build: `Meadow.OS/build-esp32.sh`
- Partition table: NVS(0x9000), PHY(0xF000), App(0x10000), CoreDump(0x3B0000), LittleFS(0x3C0000)

### Inter-Machine SPI
- No SPI hub in Renode (only UART, CAN, LIN hubs)
- Custom C# bridge peripheral required
- Alternative: hook SPI at firmware level (like current DMA bypass) and forward via shared buffer

## Decision Log

| Date       | Decision | Rationale |
|------------|----------|-----------|
| 2026-03-10 | Separate track from renode-esp32 | Phase A (stub) is complete and working. Dual-machine is a distinct, larger effort. |
| 2026-03-10 | Spike-first approach | Unknown how well Renode's Xtensa/ESP32 support works. Validate before investing in bridge/stubs. |
| 2026-03-10 | Stub peripherals as MappedMemory | ESP32_UART is the only real Renode ESP32 peripheral. All others (GPIO, SPI, timers, DPORT, eFuse, etc.) stubbed as MappedMemory to prevent bus errors. Will replace with C# peripherals as needed. |
| 2026-03-10 | ESP-IDF toolchain required for next steps | `esp32` cpuType has distinct ISA from `sample_controller` — can't use generic Xtensa binaries. Need ESP-IDF 4.2.4 toolchain to build hello-world or Meadow firmware. |
| 2026-03-10 | Bare-metal approach for boot spike | Full ESP-IDF build system has Python 3.10 compat issues (gevent). Bare-metal C + asm startup with direct cross-compiler use works for the spike. |
| 2026-03-10 | Windowed ABI requires explicit init | ESP32 Xtensa always uses windowed ABI (no `-mabi=call0`). Must init WINDOWBASE=0, WINDOWSTART=1, PS.WOE=1 before any `entry` instruction. |
| 2026-03-10 | Trampoline for ESP-IDF ELF loading | ESP-IDF ELF entry point (`call_start_cpu0` at 0x40081BBC) uses `entry` instruction. Can't set window registers via Renode API — must use a code trampoline at 0x4009F000. |
| 2026-03-10 | IRAM +1KB patch for emulator build | Meadow firmware overflows IRAM by 652 bytes with toolchain `esp-2020r3`. Patching `esp32_out.ld` to extend `iram0_0_seg` by 1KB works. Root cause likely: slightly different code generation vs production build env. |
| 2026-03-10 | CMake 3.28 required | CMake 4.x removed compatibility with `cmake_minimum_required(VERSION 2.6)` used by ESP-IDF 4.2.4's mbedtls. Downloaded CMake 3.28.6 to `/tmp/`. |
| 2026-03-10 | Use real ESP32 ROM binary | Espressif distributes ROM in their QEMU fork (github.com/espressif/qemu, esp-develop branch). Provides full exception vectors, ROM utility functions. Much better than hand-writing stubs. |
| 2026-03-10 | Custom C# peripherals for status bits | MappedMemory can't auto-set "done" flags. Created ESP32_RTC_CNTL, ESP32_TimerGroup, ESP32_DPORT with reactive reads. Pattern: return stored value OR'd with required status bits. |
| 2026-03-10 | Patch ets_delay_us to NOP | CCOUNT-based delays stall emulator indefinitely. ROM delay function patched with immediate retw.n. Acceptable for boot bring-up; may need refinement for timing-sensitive code. |
| 2026-03-10 | Hook for app_cpu_started | BSS clearing zeros any pre-set values. Used CPU hook at polling loop to set flag dynamically. Single-core emulation needs this to skip APP CPU wait. |
| 2026-03-10 | **VECBASE = 0x40080000 (not 0x40000000)** | The firmware's window overflow/underflow handlers are in IRAM at 0x40080000, not in ROM. With wrong VECBASE, Renode dispatched to ROM code (no handlers) causing "Illegal entry instruction". Correct VECBASE enables proper window exception handling — Renode DOES support s32e, l32e, rfwo, rfwu. This was the single most impactful fix. |
| 2026-03-10 | Patch esp_crosscore_int_send_yield → j vPortYield | ESP-IDF triggers context switches via software interrupt (DPORT_CPU_INTR_FROM_CPU_0_REG). Without interrupt hardware emulation, the ISR never fires. Patching to directly call vPortYield enables tasks to yield and context switch. |
| 2026-03-10 | s32c1i → l32i (62 instances) | Renode's Xtensa doesn't support s32c1i (atomic compare-and-swap). For single-core, CAS always succeeds, so l32i (plain load) is safe — gives correct "old value" for assertion checks. |
| 2026-03-10 | Patch vPortEnter/ExitCritical to NOP | Spinlock operations use s32c1i internally (which we patched). NOPing critical sections is safe for single-core emulation (no contention, no real interrupts). |
| 2026-03-11 | Non-windowed list functions | vListInsertEnd, vListInsert, uxListRemove rewritten as non-windowed functions at 0x4009F060-0x4009F158. No entry/retw — operate in caller's register window, return via jx a8. Avoids retw bug for most-called functions. 18 call8 sites redirected for vListInsertEnd; entry→j patches for vListInsert and uxListRemove. |
| 2026-03-11 | FreeRTOS list corruption scan-and-repair | Renode's entry instruction sometimes passes wrong register values, causing vListInitialise to write data to addr+8 instead of addr. Hook at vTaskStartScheduler scans DRAM for the corruption pattern ([addr+8]==addr && [addr+0]==0xFFFFFFFF) and reinitializes ~16 lists. |
| 2026-03-11 | vTaskSwitchContext inline trampoline | Replaced the exit sequence (l32i + callx8 + retw.n) at 0x40092C33 with 27 bytes of inline assembly: rsr.windowbase, addi -1 (call4), compute WINDOWSTART = 1<<newWB, wsr.windowbase, j _frxt_dispatch+3. Avoids retw entirely. |
| 2026-03-11 | IPC task demotion at first dispatch | 6 IPC/crosscore tasks (ipc0, ipc1, + 4 others) at priority 24 monopolize single-core emulation. Python hook at _frxt_dispatch walks ready list 24, moves all tasks to priority 0 on first dispatch. |
| 2026-03-11 | Timer tick simulation | Without real timer interrupts, xTickCount never advances and blocked tasks never unblock. Python hook at _frxt_dispatch increments xTickCount, walks pxDelayedTaskList, moves ready tasks back to ready lists. |
| 2026-03-11 | Python hook scoping via \_\_builtin\_\_ | `python """..."""` blocks and `cpu AddHook` run in different Python scopes. Share functions between them using `import __builtin__; __builtin__._func = func` in the python block, then `_func()` in hook strings. |
| 2026-03-11 | **Renode retw bug (CRITICAL)** | Confirmed: Renode's retw reads A0 from physical AR[0] instead of AR[WINDOWBASE\*4]. Affects ALL retw/retw.n instructions. Workaround: trampoline at 0x4009F040 saves A0 to EXCSAVE1, sets WB=0, writes A0 to AR[0], restores WB, then retw.n reads correct value from AR[0]. Applied to _frxt_dispatch resume, xQueueGenericReceive, xQueueGenericSend. Long-term fix requires building Renode from source. |
| 2026-03-11 | Deferred retw patches | xQueueGenericReceive/Send retw patches applied AFTER scheduler starts via Python hook (one-shot at first _frxt_dispatch). These functions are used during queue initialization — patching them before scheduler starts crashes early boot. |

| 2026-03-11 | **arch_tlb_fill root cause found and fixed** | `arch_tlb_fill` returned `TRANSLATE_FAIL` (==1==`SYSCALL_CAUSE`) instead of the real MMU cause. Fixed in tlib by returning the actual error code from `get_physical_address`. This was the root cause of the vListInsertEnd/vListInsert PC corruption. The "retw bug" may have been a misdiagnosis caused by the same issue — retw code analysis shows the implementation is correct. Need to re-test without retw workarounds. |
| 2026-03-11 | Re-evaluate all retw workarounds | With arch_tlb_fill fixed, the ~500 lines of retw trampolines and non-windowed list functions in the boot script may be unnecessary. Test incrementally by removing workarounds and checking for regressions. |

## Changes Log

| Date | Files Changed | What & Why |
|------|---------------|------------|
| 2026-03-10 | `platforms/meadow-esp32.repl` | Created ESP32 platform definition — Xtensa CPU (esp32), full memory map (ROM, IRAM, DRAM, RTC, flash cache), ESP32_UART, 20+ peripheral register stubs |
| 2026-03-10 | `scripts/esp32-boot-test.resc` | Interactive boot test script (loads Zephyr sample for visual testing) |
| 2026-03-10 | `scripts/esp32-boot-test-headless.resc` | Headless boot test — validates platform load, CPU instruction execution, UART response |
| 2026-03-10 | `conductor/tracks/renode-esp32-dual/plan.md` | Updated with Phase 1.1 completion, research findings, decision log |
| 2026-03-10 | `tests/esp32-hello/` | Created bare-metal ESP32 hello-world: `hello.c`, `start.S` (windowed ABI init), `esp32-hello.ld` (linker script), `esp32-hello.elf` (compiled binary) |
| 2026-03-10 | ESP-IDF toolchain | Installed xtensa-esp32-elf-gcc 8.4.0 at `~/.espressif/tools/`, cmake+ninja via Homebrew. ESP-IDF v4.2.4 cloned to `~/esp/esp-idf-v4.2.4/` (Python venv fixed, build works) |
| 2026-03-10 | Meadow ESP32 firmware | Built MeadowComms.elf (17MB) + MeadowComms.bin (1.3MB) + bootloader.bin (25KB). Required stub `build_info.h` and IRAM size patch. |
| 2026-03-10 | `/tmp/trampoline.elf` | Window register init trampoline at 0x4009F000 — loaded alongside MeadowComms.elf, PC set to 0x4009F008 |
| 2026-03-10 | `roms/` directory | Downloaded esp32-v3-rom.bin (445KB) + esp32-v3-rom-app.bin from Espressif QEMU. Added .gitignore (bins not redistributable) + README.md with download instructions |
| 2026-03-10 | `peripherals/ESP32_RTC_CNTL.cs` | Custom peripheral: auto-sets RTC_CNTL_TIME_VALID (bit 30 of TIME_UPDATE_REG) on read |
| 2026-03-10 | `peripherals/ESP32_TimerGroup.cs` | Custom peripheral: auto-sets TIMG_RTC_CALI_RDY (bit 15 of RTCCALICFG_REG) on read |
| 2026-03-10 | `peripherals/ESP32_DPORT.cs` | Custom peripheral: auto-sets CACHE_FLUSH_DONE (bit 5) on PRO/APP CACHE_CTRL reads |
| 2026-03-10 | `scripts/esp32-meadow-boot.resc` | New boot script: loads ROM + firmware + trampoline, patches FPU/rer/delay instructions, hooks app_cpu_started |
| 2026-03-10 | `platforms/meadow-esp32.repl` | Expanded ROM to 0x70000, added sram2extLow/High, replaced DPORT/RTC_CNTL/TimerGroup MappedMemory with custom C# peripherals |
| 2026-03-10 | `scripts/esp32-meadow-boot.resc` | Major updates: VECBASE fix (0x40080000), esp_crosscore_int_send_yield→vPortYield patch, 62 s32c1i→l32i patches, vPortEnter/ExitCritical NOPs, malloc→heap_caps_malloc_default jump, __assert_func NOP, flash function NOPs, window overflow/underflow diagnostic hooks, FreeRTOS milestone hooks |
| 2026-03-11 | `scripts/esp32-meadow-boot.resc` | Major updates: non-windowed list functions at 0x4009F060-0x4009F158, vTaskSwitchContext inline trampoline at 0x40092C33, _frxt_dispatch resume trampoline at 0x4009F040, retw→j patches for xQueueGenericReceive/Send (deferred via Python), vTaskSwitchContext dual-core exit bypass, core affinity NOP, FreeRTOS list scan-and-repair hook, Python tick simulation with IPC demotion and delayed task list processing |
