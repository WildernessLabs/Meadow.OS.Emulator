# Plan: Dual-Machine ESP32 Emulation

## Track ID: `renode-esp32-dual`
## Status: `in-progress` (Phase 1 ✅, retw source fix ✅, SPI ctor works ✅, Phase 2.2 in-progress — WiFi stubs needed)
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
- [x] **Re-evaluate retw workarounds**: Confirmed retw/entry work correctly in tlib. Removed
  all retw workarounds from boot script (~428 lines): vTaskSwitchContext inline trampoline,
  _frxt_dispatch resume trampoline, non-windowed vListInsertEnd/vListInsert/uxListRemove,
  retw trampoline, deferred retw patches. The "retw bug" was a misdiagnosis caused by
  arch_tlb_fill's fake SYSCALL exceptions corrupting EPC1.
- [x] **Simplify boot script**: Removed retw trampolines, non-windowed list functions, and
  deferred retw patches. Boot script reduced from ~936 to ~508 lines.
- [x] **A8=A0 "corruption" investigation — RESOLVED (red herring)**: Extensive TCG register
  allocator investigation confirmed that A8 receiving A0's value (0x40091363) in
  vTaskSwitchContext is NOT a bug. The instruction at PC=0x40092b7e is `addi a8, a0, 0`
  — the firmware intentionally copies A0 to A8 as part of a processor-ID-based array index
  computation (rsr.prid → extui → addi a8,a0,0 → slli → add.n). The phys_regs[20] showing
  a different value is expected: phys_regs are only synced at sync_windowbase, while
  env->regs[8] is updated in real-time by TCG ops. No TCG register allocator bug exists.
- [x] **Upstream Renode Xtensa issue search**: Exhaustive search of renode/renode,
  renode/renode-infrastructure, and antmicro/tlib GitHub repos found NO reported issues or
  PRs for Xtensa register window bugs, RETW corruption, or ESP32 FreeRTOS crashes. The
  Xtensa emulation was ported from QEMU with minimal bug fixes since Dec 2021. ESP32 support
  is explicitly "core only" without full SoC/peripherals. Any bugs we find are likely novel.
- [x] **Clean up debug instrumentation in tlib**: Reverted all debug logging from A8 investigation
  across 8 files: translate.c (TB chaining disable, debug_check_a8 helper, opcode logging),
  op_helper.c (debug_check_a8 function ~43 lines), helper.h (declaration), cpu-exec.c
  (TBEXEC/IRQ_CORRUPT checks), exc_helper.c (DEBUG_IRQ logging), win_helper.c (old_r8 tracking),
  softmmu_template.h (load/store instrumentation), mmu_helper.c (stale comment). tlib rebuilt
  and .NET assembly updated. Preserved committed fixes: assert→logging (2574b54), WAITI
  (4271279), CCOUNT (8a1549f), clock freq (46768e0), arch_tlb_fill (2520925).
- [x] **FreeRTOS list corruption — RESOLVED as false positive**: The 16 "corrupted lists" detected
  by the scan-and-repair hook were NOT actually corrupted:
  - 15 were valid binary semaphore Queue_t objects where pcWriteTo==Q (normal for uxItemSize=0
    semaphores; Queue_t header is 80 bytes with pcHead=pcTail=Q+80, pcWriteTo=Q when empty)
  - 1 was xSuspendedTaskList where the scan hit the xListEnd mini-item within the List_t
  - The scan-and-repair code was DESTROYING valid data structures, not fixing corruption
  - Removed the destructive fixup code from the boot script
- [x] **pxIndex DRAM corruption found and mitigated**: Renode's window-spill mechanism occasionally
  writes IRAM code addresses (e.g., 0x40091363 = _frxt_dispatch return addr) into FreeRTOS
  list pxIndex fields. When _wake_delayed tried to insert a task using the corrupt pxIndex,
  it wrote to IRAM (ri+8), corrupting code at _frxt_dispatch and causing IllegalInstruction
  crashes. Root cause: register window overflow saves A0 (containing return address) to a
  stack location that overlaps with a TCB's xStateListItem.pxPrevious field; when
  uxListRemove runs and pxList->pxIndex == pxItemToRemove, it sets pxIndex to the corrupt
  pxPrevious value. Mitigation: tick simulation validates all 25 ready-list pxIndex values
  every tick, resetting any outside DRAM range (0x3FF00000-0x40000000) to &xListEnd.
- [x] **Overflow delayed list wake bug fixed**: Initially added wake logic for both
  pxDelayedTaskList and xDelayedTaskList2 (overflow list). This was WRONG — tasks in the
  overflow list have wake times that wrapped around (e.g., tick + portMAX_DELAY). They must
  wait for the tick counter to overflow and the lists to swap. The timer service task was
  stuck in a wake→delay→wake loop because its wake time (0) was always <= tick count.
  Fix: only check pxDelayedTaskList (the pointer, which FreeRTOS manages).
- [x] **Core 1 scheduler flag for single-core emulation**: main_task busy-waits on
  port_xSchedulerRunning[1] at 0x3FFC92BC, waiting for core 1's scheduler to start.
  Since we only emulate one core, this never gets set. Fix: write 1 to 0x3FFC92BC at
  first dispatch (alongside IPC demotion).
- [x] **Boot reaches app_main and beyond**: With the above fixes, the system now reaches:
  vTaskStartScheduler → main_task → app_main → Logging::Setup → uart_param_config →
  uart_set_pin → uart_driver_install → uart_pattern_queue_reset → uart_isr_register →
  brownout_init → C++ static constructors (do_global_ctors). UART output visible:
  "Re-enable cpu cache." printed before crash in Thread::TakeExecutionSemaphore.
- [x] **Stall inside uart_isr_register — RESOLVED**: Root cause was Renode's retw bug
  (reads AR[0] instead of AR[WINDOWBASE*4]) causing return address corruption in deeply
  nested call chains. Fix: NOP'd the `esp_intr_alloc` call inside uart_isr_register
  (interrupt registration not needed in emulation), plus global retw fix (see below).
- [x] **Renode retw bug — global mitigation**: Extracted all 6845 retw/retw.n addresses
  from MeadowComms.elf, generated `scripts/retw_hooks.resc` with a Python hook at each
  address that copies logical A0 to physical AR[0] before retw executes. Takes ~40s to
  load at startup but fixes retw system-wide. The `_fix_retw()` hook also detects bad
  return addresses (< 0x40000000 or > 0x401C0000) and logs them.
  - Discovery: `call8` does NOT rotate WINDOWBASE — only `entry` does. Patching `entry`
    to a jump breaks window rotation, causing retw to return to the wrong caller.
- [x] **esp_brownout_init skip**: Brownout detection irrelevant in emulation. Keep `entry`
  at 0x400d5948 (for window rotation), replace `l32r` at 0x400d594b with `j` to retw.n
  at 0x400d597d. Skips entire function body.
- [x] **__register_frame_info_bases skip**: GCC exception frame registration calls
  pthread_mutex_lock → xQueueGenericReceive before FreeRTOS scheduler starts, triggering
  assertions. Not needed for emulation. Replace `beqz.n` at 0x40199b57 with `j` to
  retw.n at 0x40199b90.
- [x] **xQueueGenericReceive pre-scheduler patch (partial)**: Patched scheduler-suspended
  branch at 0x40091d07 to jump to return pdTRUE (0x40091d83). However, this only catches
  scheduler state 0 (SUSPENDED). With state 1 (NOT_STARTED), code falls through to
  normal blocking path — still crashes. See next item.
- [x] **Thread::TakeExecutionSemaphore crash — RESOLVED**: Fixed by xQueueGenericReceive
  scheduler state hook + retw fix getting boot past C++ static constructors.
- [x] **ROM retw hook regression — RESOLVED**: Confirmed that _fix_retw on ROM retw addresses
  corrupts AR[0] for other contexts. Even a SINGLE ROM retw hook corrupts WB=0's physical
  registers. Root cause: _fix_retw writes A0 → AR[0] (physical), but AR[0] is shared across
  ALL window contexts where WB=0. With no-op hooks (pass), boot works — proving hook overhead
  isn't the issue, the register manipulation is. Solution: skip ROM function calls at firmware
  call sites instead.
- [x] **strftime ROM call crash**: After disabling ROM hooks, strftime (0x40059ab4) called from
  app_main returned to NULL. NOP'd the callx8 at 0x400d65ca (replaced with movi a8, 0).
  Result not needed since esp_log_write is also NOP'd.
- [x] **SpiTransportProvider ctor skip (final)**: Ctor at 0x400d93f8 must be fully skipped.
  Attempted partial un-skip (GPIO init skipped, buffer alloc/queue/task creation kept) but:
  (1) `or a1,a1,a1` used as NOP corrupts A1 in Renode (tlib bug), (2) ClearTransmitAndReceiveBuffers
  calls ROM memset (retw crash), (3) xTaskCreatePinnedToCore triggers window overflow/underflow
  with corrupted stack (DOUBLE_EXC on retw at ctor exit). Reverted to full skip: entry+3
  (0x400d93fb) → `j 0x400d95f4` (retw.n). SpiBridge handles SPI communication at peripheral level.
- [x] **PS.INTLEVEL / FreeRTOS lock abort**: NOP'd _xtos_set_intlevel calls left PS.INTLEVEL=3
  permanently, causing lock_acquire_generic to think we're in ISR context → abort(). Fix:
  patched `extui a11,a11,0,4` → `movi a11,0` in lock_acquire_generic (0x40082c49) and
  lock_release_generic (0x40082ca7). Forces non-ISR path. Safe because emulator has no real ISRs.
- [x] **spi_slave_hal_prepare_data null pointer — RESOLVED**: Root cause was corrupted _lock_t
  variables from Renode's window-spill bug, not the skipped SPI ctor. Queue handles pointed to
  uninitialized heap memory. Fix: Python hook on xQueueGenericReceive (0x40091cb8) detects
  uninitialized Queue_t structs (both list pxIndex fields NULL) and initializes all List_t and
  Queue_t fields in-place before the function proceeds.
- [x] **prvDeleteTCB/prvDeleteTLS/reclaim_reent/vPortReleaseMPU crashes — RESOLVED**: All four
  task cleanup functions crash when Renode's window-spill corrupts TCB pointer fields. Skipped
  with entry→j-to-retw binary patches. Task cleanup isn't critical for emulation boot.
- [x] **CCOUNT not incrementing — RESOLVED**: Renode's Xtensa CCOUNT register returns a stale
  value (never advances from its initial value). This means CCOMPARE0 timer interrupts never
  fire, and FreeRTOS has no tick source. `waiti` instruction in esp_pm_impl_waiti also halts
  the CPU, preventing clock advancement.
  Fix: (1) Patched `waiti 0` to `nop` in esp_pm_impl_waiti (0x401aef8b), (2) Python tick
  injection hook on idle task loop (0x400920fe) that periodically fires CPU interrupt 6 via
  `OnGPIO(6, True/False)` with PS.INTLEVEL cleared. xPortSysTickHandler now runs natively
  via the interrupt vector, performing real tick counting and delayed task waking.
- [x] **Boot reaches idle task with working FreeRTOS ticks**: Full boot sequence:
  ROM init → heap init → FreeRTOS scheduler start → app_main → Logging::Setup →
  NvsManager::Setup → SpiTransportProvider ctor (skipped) → MessageDispatcher ctor →
  idle task. Tick injection fires every ~50 idle loops, context switches work, system
  runs stably for 90+ seconds with no crashes.
- [x] **tlib fixes pushed to bryancostanich/renode**: 5 patches on branch `fix/xtensa-esp32-patches`:
  arch_tlb_fill (return real MMU cause), WAITI (don't halt CPU), CCOUNT/CCOMPARE (fix time
  unit conversion), clock_freq_khz (240MHz), win_helper (assert→logging). Pushed via SSH.
  Patched build at `/Users/bryancostanich/Git_Repos/Pub_Code/renode-retw-fix/output/bin/Release/Renode`.
- [x] **UART debug output capture**: Hooked ets_printf (ROM @ 0x40007d54) to capture format strings
  directly. Per-character capture via putc1→uart_tx_one_char doesn't work because the retw bug
  corrupts returns from ROM callx8 to the function pointer. Format strings show all 16 ESP-IDF
  boot messages (Pro cpu up, Application info, memory regions, Pro cpu start user code).
  Added uart0Mirror (ESP32_UART @ 0x60000000) for the AHB→data bus mirror — absorbs FIFO writes.
- [x] **Complete peripheral map — zero unmapped access warnings**: Added I2S0 (0x3FF4F000) and
  I2S1 (0x3FF68000) stub memory regions. All hardware accesses during boot are now covered.
  Previous: 1 warning for I2S0 access at 0x3FF4F0AC. Now: zero "non existing peripheral" warnings.
- [x] **Renode retw source fix — ROOT CAUSE FOUND AND FIXED**:
  The bug was in `translate_retw()` in `tlib/arch/xtensa/translate.c`. The function used
  `tcg_const_i32(1)` to create a non-local TCG temporary for the return address, then used
  it AFTER a `gen_helper_retw()` call. In Renode's older TCG (forked from QEMU), non-local
  temps are NOT preserved across helper calls — the register allocator can reuse the native
  register, causing `gen_jump(dc, tmp)` to jump to a garbage address.
  - **Root cause**: QEMU modernized their TCG to make all temps preserved across calls
    (removing the local/non-local distinction). Renode's tlib still uses the old TCG where
    the distinction matters. The translate_retw code was ported without adjustment.
  - **Fix**: One line — `tcg_const_i32(1)` → `tcg_temp_local_new_i32()` + `tcg_gen_movi_i32(tmp, 1)`
  - **Impact**: Eliminates ALL 6845 retw hooks, ~40s startup delay, and the `_fix_retw()` Python
    function. Boot is instant. The previous `_fix_retw` hook worked not because of the
    phys_regs[0] write (which was irrelevant for WB != 0), but because Python hooks act as
    TB execution barriers that changed register allocation behavior.
  - **SPI ctor now works**: With retw fixed, the SpiTransportProvider constructor runs
    successfully — all ROM function calls (gpio_*, spi_slave_initialize, memset) return
    properly. The SPI task and sync task are created. No more DOUBLE_EXC.
  - **Boot reaches WiFi init**: Firmware now executes through SPI ctor → MessageDispatcher →
    WiFiRequestHandler::Setup() → esp_wifi_init → hits unmapped WiFi registers → esp_restart.
    This is a Phase 3 problem (WiFi stubs), not a retw problem.

### Key FreeRTOS Addresses (ESP-IDF 4.2.4 dual-core)
- xTickCount: 0x3FFC92F0
- xNextTaskUnblockTime: 0x3FFBEDC4
- pxDelayedTaskList: 0x3FFC9358 (pointer to current delayed list)
- xDelayedTaskList1: 0x3FFC9370 (List_t)
- xDelayedTaskList2: 0x3FFC935C (List_t, overflow)
- pxReadyTasksLists: 0x3FFC9384 (25 lists × 20 bytes)
- uxTopReadyPriority: 0x3FFC92EC
- pxCurrentTCB: 0x3FFC9578 (array[2] for dual-core)
- uxCurrentNumberOfTasks: 0x3FFC92F4
- xSuspendedTaskList: 0x3FFC9300
- xPendingReadyList: 0x3FFC9340
- port_xSchedulerRunning: 0x3FFC92B8 (array[2])

## Phase 2: SPI Bridge Peripheral

**Goal:** Connect STM32 and ESP32 machines so ESPCP frames flow through the real ESP32 firmware instead of EspcpStub.

### Architecture

```
┌─────────────────────────┐                    ┌─────────────────────────┐
│ Machine: stm32          │                    │ Machine: esp32          │
│                         │                    │                         │
│  NuttX ESPCP driver     │                    │  ESP-IDF firmware       │
│     │                   │                    │  SpiTransportProvider   │
│  espcp_send_data_over_  │                    │     │ (real ctor runs)  │
│  spi (bypassed via hook)│                    │  ProcessMessages loop   │
│     │                   │                    │     │                   │
│  SpiBridge (ISPIPeripheral)  ──── frames ────►  spi_slave_queue_trans  │
│  on SPI2                │◄── frames ─────────  spi_slave_get_trans_   │
│     │                   │                    │  result (stubbed)       │
│  Ready ── ── ── ── ── ──│── ── ── ── ── ── ──  GPIO0 (Ready)         │
│  MsgWaiting ── ── ── ── │── ── ── ── ── ── ──  GPIO3 (MsgWaiting)    │
└─────────────────────────┘                    └─────────────────────────┘
```

**Design decisions:**
- SpiBridge is a C# peripheral on STM32 SPI2, replacing EspcpStub
- ~~ESP32 firmware's SPI ctor is fully skipped~~ **SPI ctor now runs successfully** after the
  tlib retw source fix. The SPI task and sync task are created by FreeRTOS. The firmware's
  ProcessMessages state machine is live and ready to process frames.
- Frame-level forwarding (not byte-level) — the bridge captures complete ESPCP frames and passes them as byte arrays
- SpiBridge exposes public methods (`DequeueFrameFromStm32`, `EnqueueFrameToStm32`) for the ESP32 side
- GPIO signals (Ready, MessageWaiting) managed by SpiBridge directly
- ESP32-side: the firmware's SPI task calls `spi_slave_queue_trans` / `spi_slave_get_trans_result`
  which need to be stubbed to feed frames from SpiBridge instead of real SPI hardware

### 2.1 — Design SPI Bridge
- [x] Architecture: SpiBridge (C#) on STM32 SPI2 + Python hooks on ESP32 SPI slave driver
- [x] STM32 side: ISPIPeripheral, replaces EspcpStub, same GPIO wiring (Ready→gpioPortI@10, MsgWaiting→gpioPortI@0)
- [x] ESP32 side: Python hooks intercept spi_slave_queue_trans / spi_slave_get_trans_result
- [x] Frame buffering: thread-safe queues in both directions (STM32→ESP32, ESP32→STM32)
- [x] GPIO forwarding: bridge outputs Ready/MessageWaiting, ESP32 hooks update via bridge API

### 2.2 — Implement and Test Bridge
- [x] Create `peripherals/SpiBridge.cs` with ISPIPeripheral + cross-machine frame queues
- [x] ~~SpiTransportProvider ctor: confirmed must be fully skipped~~ **PARTIALLY RESOLVED by tlib
  retw fix**. Retw no longer crashes ROM function returns. However, the SPI ctor still aborts
  during execution — one of the `callx8` targets inside the ctor body (likely gpio_set_pull_mode,
  gpio_config, or spi_slave_initialize) triggers abort(). The singleton check passes (first-time
  init), entry works, but a ROM/driver call fails. Needs further tracing.
- [x] Cleaned up boot script: removed SPI driver stubs, xTaskNotifyWait patch, and Python
  frame inject/capture hooks (all were for firmware SPI task that no longer runs)
- [x] Added peripheral stubs: APLL registers (0x3FF66000) for RTC clock init, WiFi BB registers
  (0x3FF71000) for WiFi baseband. These absorb hardware accesses without crashing.
- [x] Added watchdog skips: esp_task_wdt_init (return ESP_OK), esp_task_wdt_add (return ESP_OK),
  esp_int_wdt_init (return immediately). Without real timer hardware, watchdog timeouts cause
  esp_restart. Note: `esp_task_wdt_init` must return 0 in A2 or ESP_ERROR_CHECK aborts.
- [x] Fixed j instruction encoding: correct formula is `byte0 = ((offset & 0x3) << 6) | 0x06`,
  `byte1 = (offset >> 2) & 0xFF`, `byte2 = (offset >> 10) & 0xFF`. Previous formula had
  offset bits in wrong positions causing ILLEGAL_INSTRUCTION at patched addresses.
- [ ] Debug SPI ctor abort — trace which callx8 inside the ctor body triggers abort()
- [ ] Stub `spi_slave_queue_trans` / `spi_slave_get_trans_result` to feed frames from SpiBridge
- [ ] Wire SpiBridge into STM32 platform (replace EspcpStub on SPI2)
- [ ] Test: STM32 sends GetConfiguration → firmware handler processes → response returns via SpiBridge
- [ ] Test: ESP32 queues event → STM32 polls via SendResponse → event delivered
- [ ] Verify frame integrity (CRC checks pass on both sides)

## Phase 3: ESP32 Peripheral Stubs

**Goal:** Stub hardware that ESP-IDF requires but Renode can't emulate.

### 3.1 — WiFi Hardware Stub
- [x] Added WiFi BB register stub (0x3FF71000, 4KB MappedMemory) — absorbs baseband accesses
- [x] Added APLL register stub (0x3FF66000, 4KB MappedMemory) — absorbs RTC clock PLL accesses
- [ ] Identify remaining WiFi hardware registers accessed during `esp_wifi_init`/`esp_wifi_start`
- [ ] Create pydev or C# stub that returns success for WiFi hardware init
- [ ] OR: skip WiFiRequestHandler::Setup() (0x400de268) to bypass esp_wifi_restore entirely
- [ ] OR: build ESP32 firmware variant with compile-time WiFi mock

### 3.2 — BLE Hardware Stub
- [ ] Same approach as WiFi — stub Bluetooth controller registers
- [ ] OR: skip BluetoothRequestHandler::Setup() (0x400d9d2c) — note GetInstance() calls Setup
      internally, so must skip the Setup call within GetInstance at 0x400d9df6
- [ ] OR: disable BLE in ESP32 build for initial bring-up

### 3.3 — Flash/NVS Stub
- [ ] Stub SPI flash controller for NVS reads/writes
- [ ] Provide initial NVS partition data (device name, MAC, config)

### 3.4 — Watchdog Stubs
- [x] esp_task_wdt_init (0x400d5b30): return ESP_OK — movi a2,0 + retw.n at entry+3
- [x] esp_task_wdt_add (0x400d5c38): return ESP_OK — movi a2,0 + retw.n at entry+3
- [x] esp_int_wdt_init (0x400d5dac): return immediately — retw.n at entry+3

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
| 2026-03-11 | **Retw workarounds confirmed unnecessary** | tlib debug logging confirmed entry/sync_windowbase produce correct register values. Retw reads logical A0 (env->regs[0]) which is always correctly synced. All ~428 lines of retw workarounds removed from boot script. The "retw bug" was caused by arch_tlb_fill masking STORE_PROHIBITED as SYSCALL, which corrupted EPC1 via the syscall handler adding 3 to PC for non-SYSCALL instructions. |
| 2026-03-11 | **arch_tlb_fill fix exposed silent null pointer writes** | Before the fix, writes to address 0x0 raised STORE_PROHIBITED_CAUSE(29) → masked to TRANSLATE_FAIL(1)==SYSCALL_CAUSE → SYSCALL handler (no-op) → execution continued silently. After the fix, these writes correctly raise cause 29 → general exception handler → firmware panics. The null writes come from vListInsertEnd dereferencing null pxIndex in corrupted list structures. |
| 2026-03-11 | **FreeRTOS list corruption is a separate bug** | 16 lists still corrupted during init even with arch_tlb_fill fix. The corruption has the same "off by 8 bytes" pattern. Root cause is NOT arch_tlb_fill and NOT retw. Must be a third bug — possibly in TCG code generation, region TLB init, or another tlib issue. |
| 2026-03-11 | **tlib build chain: .so embedding** | After rebuilding tlib with `build.sh`, must also run `dotnet build Renode_NET.sln -c Release` to re-embed the updated translate-xtensa-le.so in the .NET assembly. Renode extracts the embedded .so to a temp dir at runtime via dlopen — if you only rebuild tlib, the old .so is still used. |
| 2026-03-11 | **A8=A0 is firmware code, not a bug** | The `addi a8, a0, 0` at 0x40092b7e in vTaskSwitchContext intentionally copies A0→A8 for processor-ID-based array indexing. The phys_regs vs env->regs divergence is normal (phys_regs sync only at sync_windowbase). No TCG register allocator bug. Multiple sessions were spent on this red herring. |
| 2026-03-11 | **No upstream Xtensa window bugs reported** | Searched renode/renode, renode-infrastructure, antmicro/tlib GitHub repos. Zero issues/PRs for register window corruption, RETW bugs, or ESP32 FreeRTOS crashes. The tlib Xtensa port from QEMU has had minimal bug fixes. Any bugs we find are novel/unreported. |
| 2026-03-11 | **tlib build on macOS: Infrastructure_NET.csproj only** | `dotnet build Renode.sln` fails on macOS due to .NET Framework projects. Current workaround: build only `Infrastructure_NET.csproj` (targets net8.0), then copy Infrastructure.dll. Full solution build may work with an older .NET SDK installed — worth investigating as it would simplify the build chain. |
| 2026-03-11 | **tlib debug instrumentation needs cleanup** | Multiple debug hooks added across 7 files during A8 investigation: TB chaining disabled globally, per-instruction helper calls, opcode name logging, load/store value tracing, IRQ corruption checks. All must be removed before further work — they add significant overhead and noise. Keep only the actual bug fixes (assert→logging, WAITI, CCOUNT, arch_tlb_fill). |
| 2026-03-11 | **List corruption scan-and-repair removed** | The 16 "corrupted lists" were false positives: 15 valid Queue_t binary semaphores + 1 valid xSuspendedTaskList overlap. The scan-and-repair code was destroying valid data structures. Removed entirely. |
| 2026-03-11 | **Retw workarounds confirmed still needed** | Testing esp32-meadow-boot-minimal.resc (without retw workarounds) crashes with "Illegal retw instruction(pc=40092c3c, PS=00060028, m=0, n=0)" at vTaskSwitchContext. The arch_tlb_fill fix alone does NOT eliminate all retw failures. The full boot script with retw patches for _frxt_dispatch, xQueueGenericReceive, and xQueueGenericSend is still required. |
| 2026-03-11 | **pxIndex DRAM corruption mitigation** | Renode window-spill writes IRAM addresses into FreeRTOS list fields. Rather than fixing the root cause in tlib (complex register window spill analysis), added per-tick validation of all 25 ready-list pxIndex values. Any value outside DRAM (0x3FF00000-0x40000000) is reset to &xListEnd. Also validates pxIndex before use in _wake_delayed insertion. |
| 2026-03-11 | **Do NOT wake overflow delayed list** | Tasks in xDelayedTaskList2 (overflow) have wrapped wake times and must wait for tick counter overflow + list swap. Only check pxDelayedTaskList (the pointer managed by FreeRTOS). |
| 2026-03-11 | **Fake port_xSchedulerRunning[1]** | main_task busy-waits on port_xSchedulerRunning[1] (0x3FFC92BC). Set to 1 at first dispatch since we don't emulate core 1. |
| 2026-03-11 | **ESP-IDF TCB layout confirmed empirically** | ESP-IDF 4.2.4 dual-core TCB offsets: pxTopOfStack(+0), unknown(+4), xGenericListItem(+8, 20B), xEventListItem(+28, 20B), pxStack(+44?), uxPriority(+48), pcTaskName(+56). Extra 4 bytes vs standard FreeRTOS due to portCRITICAL_NESTING_IN_TCB=0 but xCoreID field. |
| 2026-03-12 | **Bulk retw fix — 6845 hooks** | Extracted all retw/retw.n addresses from MeadowComms.elf, generated `retw_hooks.resc` with `_fix_retw()` Python hook at each address. Copies logical A0 to physical AR[0] before retw executes. Fixes retw system-wide at cost of ~40s startup. This replaced the per-function retw patches and got boot past the uart_isr_register stall. |
| 2026-03-12 | **call8 does NOT rotate WINDOWBASE** | Only `entry` rotates WINDOWBASE. Replacing a function's `entry` with `j retw.n` breaks window rotation — retw reads the caller-of-caller's A0. Must keep `entry` intact and patch the instruction AFTER it. Discovered when esp_brownout_init skip returned to 0x40081d82 (invalid) instead of back to start_cpu0_default. |
| 2026-03-12 | **esp_brownout_init skip** | Keep entry at 0x400d5948, replace l32r at 0x400d594b with `j 0x400d597d` (retw.n). Brownout detection is irrelevant in emulation. |
| 2026-03-12 | **__register_frame_info_bases skip** | GCC exception frame registration calls pthread_mutex_lock → xQueueGenericReceive before scheduler starts. Replace beqz.n at 0x40199b57 with `j 0x40199b90` (retw.n). |
| 2026-03-12 | **xQueueGenericReceive scheduler state analysis** | `xTaskGetSchedulerState` returns 1 (NOT_STARTED). The `beqz.n a10` at 0x40091cf8 only branches on 0 (SUSPENDED); value 1 falls through to normal blocking path. Binary patch at 0x40091d07 (SUSPENDED path) is ineffective. Need Python hook at 0x40091cf8 to catch state=1 and skip to return pdTRUE. |
| 2026-03-12 | **_xt_kernel_exc is a double fault handler** | The BREAK at 0x40082754 is the kernel exception vector (`_xt_kernel_exc`), not a configASSERT. It fires when an exception occurs during exception processing (PS.EXCM=1). In our case: vTaskPlaceOnEventList accesses uninitialized task list structures → memory fault while already in critical section. |
| 2026-03-12 | **ROM retw hooks cause AR[0] corruption** | Even a SINGLE _fix_retw hook on a ROM function's retw corrupts physical AR[0], breaking any context using WB=0. The _fix_retw approach is fundamentally incompatible with ROM functions. The corruption cascades because AR[0] is shared across all WB=0 contexts — a hooked retw writes one function's A0 value to AR[0], and a later unhooked retw (or any code at WB=0) reads the wrong value. No-op hooks ("pass") don't cause corruption, confirming the register manipulation (not hook overhead) is the problem. |
| 2026-03-12 | **Skip ROM function calls at firmware call sites** | Instead of fixing retw in ROM functions, skip them entirely by patching the firmware's callx8 → movi (NOP) or patching function entry → j retw.n. Applied to: strftime (NOP callx8), SpiTransportProvider ctor (j to retw.n), _xtos_set_intlevel (NOP 10 callx8 sites). |
| 2026-03-12 | **PS.INTLEVEL fix via lock function patches** | NOP'd _xtos_set_intlevel leaves PS.INTLEVEL permanently elevated (=3). This makes lock_acquire_generic think code is running in ISR context → abort(). Patched `extui a11,a11,0,4` → `movi a11,0` in both lock_acquire_generic (0x40082c49) and lock_release_generic (0x40082ca7) to always take the non-ISR path. Safe for single-core emulation with no real interrupts. |
| 2026-03-12 | **SpiTransportProvider ctor skip** | Ctor at 0x400d93f8 has 20 callx8 instructions targeting ROM functions (gpio_pad_select_gpio, spi_bus_initialize, gpio_set_pull_mode). Patched entry+3 (0x400d93fb) with `j 0x400d95f4` (retw.n). SPI hardware init not needed for emulation. Leaves SPI HAL structs uninitialized — may need to provide dummy initialized state for later SPI operations. |
| 2026-03-12 | **movi encoding: 82 A0 00 (not 82 C0 00)** | Verified `movi a8, 0` = bytes `82 A0 00` from objdump. Previous patches using `82 C0 00` encoded a different (nonzero) immediate but happened to work as harmless NOPs. Corrected encoding for lock function patches uses `B2 A0 00` for `movi a11, 0`. |
| 2026-03-12 | **Renode `or a1,a1,a1` corrupts A1 (tlib bug)** | Using `or a1,a1,a1` (bytes 20 11 10) as a NOP zeroes A1 (SP) under certain conditions in Renode's Xtensa tlib. The proper `nop` instruction (bytes 00 20 f0) does not have this issue. All `call8 → NOP` patches must use `nop` (00 20 f0) not `or a1,a1,a1` (20 11 10). |
| 2026-03-12 | **SpiTransportProvider ctor must be fully skipped** | Attempted partial un-skip (skip GPIO init, keep buffer alloc + task creation). Three blocking issues: (1) `or a1,a1,a1` NOP corrupts SP, (2) ClearTransmitAndReceiveBuffers calls ROM memset (retw crash), (3) deep call chain corrupts stack during window overflow → DOUBLE_EXC on ctor retw. Full skip (entry+3 → j retw.n) is the only viable approach. SpiBridge handles SPI at peripheral level. |
| 2026-03-12 | **IPC demotion now runs every dispatch** | Changed from one-shot to per-dispatch to catch the SPI task (created after initial demotion). Returns immediately when no pri-24 tasks exist (checks count first). Moot now that SPI task doesn't run, but correct for any future pri-24 tasks. |
| 2026-03-12 | **tlib patches pushed to bryancostanich/renode** | 5 patches on branch `fix/xtensa-esp32-patches`: arch_tlb_fill, WAITI, CCOUNT, clock_freq_khz, win_helper assert→logging. Pushed via SSH to `git@github.com:bryancostanich/renode.git`. Old patched build was recovered from Trash after accidental deletion. |
| 2026-03-13 | **RETW SOURCE FIX — TCG temp corruption** | `translate_retw()` in `translate.c` used `tcg_const_i32(1)` (non-local temp) for the return address, then read it after `gen_helper_retw()`. In Renode's older TCG, non-local temps are NOT preserved across helper calls. QEMU modernized their TCG to preserve all temps; Renode's fork didn't. Fix: `tcg_temp_local_new_i32()`. This one-line change eliminates 6845 retw hooks, ~40s startup, and unblocks the SPI ctor. |
| 2026-03-13 | **SPI ctor no longer needs skip** | With the retw fix, all ROM function calls in the SpiTransportProvider constructor (gpio_set_pull_mode, gpio_config, spi_slave_initialize, heap_caps_calloc, memset, xTaskCreate) return properly. The SPI task and sync task are created. ProcessMessages state machine is live. |
| 2026-03-13 | **Boot now reaches WiFi init** | With retw fix + SPI ctor un-skipped, firmware boots through: ROM → heap → FreeRTOS → app_main → SPI ctor → MessageDispatcher → WiFiRequestHandler::Setup() → esp_wifi_init → hits unmapped WiFi regs at 0x3FF71xxx → esp_restart_noos. Next: stub WiFi init. |
| 2026-03-13 | **j instruction encoding corrected** | The Xtensa `j` instruction encodes the 18-bit signed offset as: `byte0 = ((offset[1:0]) << 6) \| 0x06`, `byte1 = offset[9:2]`, `byte2 = offset[17:10]`. The previous formula incorrectly placed `offset[5:0]` in `byte0[7:2]`, which produced ILLEGAL_INSTRUCTION exceptions at patched addresses. Verified against existing working patches (brownout skip at 0x400d594b: offset=46, bytes=86 0B 00). |
| 2026-03-13 | **Watchdog causes esp_restart without timer hardware** | EXCCAUSE=512 is the interrupt watchdog timeout panic code. Without real timer interrupts, the watchdog fires and calls esp_panic_handler → esp_restart_noos. Fix: skip esp_task_wdt_init (return ESP_OK), esp_task_wdt_add (return ESP_OK — required by ESP_ERROR_CHECK in main_task), esp_int_wdt_init (return immediately). |
| 2026-03-13 | **SPI ctor abort (under investigation)** | With watchdog skips + retw fix, boot reaches SPI ctor entry (confirmed via hook). The ctor then aborts inside one of its callx8 targets. The singleton check passes correctly (first-time init path at beq a10,a8). The abort EXCCAUSE=0xFFFFFFFF indicates software abort(), not a hardware exception. Likely one of: gpio_set_pull_mode, gpio_config, spi_bus_config, or spi_slave_initialize hitting unmapped SPI3/GPIO registers or a driver-level assertion. |
| 2026-03-13 | **`or a1,a1,a1` bug status unclear** | 683 instances of `or a1,a1,a1` in firmware (compiler-generated NOPs). Previously documented as corrupting A1 in Renode. The tlib translate_or code looks correct (`tcg_gen_or_i32(out, in, in)` with same TCG variable). May have been a retw-cascade symptom. Needs isolated testing to confirm. |

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
| 2026-03-11 | `scripts/esp32-meadow-boot-minimal.resc` | Removed ~428 lines of retw workarounds: vTaskSwitchContext trampoline, _frxt_dispatch trampoline, non-windowed list functions (vListInsertEnd/vListInsert/uxListRemove + 18 call8 redirects + entry→j patches), retw trampoline, deferred retw patches, _retw_patched flag. Retains list fixup, IPC demotion, tick simulation, milestone hooks |
| 2026-03-11 | tlib `arch/xtensa/helper.c` | **arch_tlb_fill fix**: Changed `return TRANSLATE_FAIL` → `return ret` to propagate real MMU cause codes. Submodule chain: tlib `2520925`→`2574b54`, Infrastructure `bcc70d280`, renode `e7098410` |
| 2026-03-11 | tlib `arch/xtensa/win_helper.c` | Added defensive bounds checks with LOG_LEVEL_ERROR assertions in copy_window_from_phys, copy_phys_from_window, and window_check helpers. Prevents out-of-bounds access on phys_regs array |
| 2026-03-11 | tlib (7 files) | **Debug instrumentation (TO REMOVE)**: Added extensive per-instruction tracing, TCG register dumps, opcode logging, TB chaining disable, load/store value traps across translate.c, op_helper.c, helper.h, cpu-exec.c, exc_helper.c, win_helper.c, softmmu_template.h. All related to A8=A0 investigation (now resolved as red herring). |
| 2026-03-11 | tlib (8 files) | **Debug instrumentation REMOVED**: Reverted all debug logging from A8 investigation. translate.c, op_helper.c, helper.h, cpu-exec.c, exc_helper.c, win_helper.c, softmmu_template.h, mmu_helper.c all restored to clean state. tlib rebuilt + .NET assembly updated. |
| 2026-03-11 | `scripts/esp32-meadow-boot.resc` | Major tick simulation improvements: (1) Removed destructive list scan-and-repair (was false positive). (2) Extracted _wake_delayed helper function with pxIndex DRAM validation (resets to &xListEnd if outside 0x3FF00000-0x40000000). (3) Added per-tick ready-list pxIndex repair loop (25 lists). (4) Fixed overflow delayed list — only check pxDelayedTaskList pointer, NOT xDelayedTaskList2. (5) Set port_xSchedulerRunning[1] = 1 at first dispatch for single-core emulation. (6) Added app_main sub-function milestone hooks. System now reaches app_main → uart_driver_install → uart_isr_register. |
| 2026-03-12 | `scripts/retw_hooks.resc` | Generated file: 6845 lines of `cpu AddHook 0xADDRESS "_fix_retw()"` for every retw/retw.n in MeadowComms.elf. Generated from: `awk '{printf "cpu AddHook %s \"_fix_retw()\"\n", $1}' /tmp/retw_addrs.txt`. Takes ~40s to load. |
| 2026-03-12 | `scripts/esp32-meadow-boot.resc` | (1) Added bulk retw fix: `_fix_retw()` Python function + `include @scripts/retw_hooks.resc`. (2) NOP'd esp_intr_alloc call in uart_isr_register. (3) Skip esp_brownout_init body (keep entry, j to retw.n). (4) Skip __register_frame_info_bases body. (5) Patch xQueueGenericReceive scheduler-suspended path (partial fix). (6) Added diagnostic hooks for xTaskGetSchedulerState return value and break instruction detection. |
| 2026-03-12 | `scripts/find_reg_indices.py` | Helper: discovers IronPython register indices for named registers |
| 2026-03-12 | `scripts/trace_callx8.py` | Helper: traces call8/retw sequences with register dumps |
| 2026-03-12 | `scripts/check_bytes.py` | Helper: verifies binary patch bytes at specific addresses |
| 2026-03-12 | `scripts/test_set_reg.py` | Helper: tests IronPython register read/write via _cpu.GetRegister/SetRegister |
| 2026-03-12 | `scripts/esp32-meadow-boot.resc` | (1) Disabled ROM retw hooks (partial hooking corrupts AR[0]). (2) NOP'd strftime callx8 at 0x400d65ca. (3) Skip SpiTransportProvider ctor (j 0x400d95f4 at 0x400d93fb). (4) Patched lock_acquire_generic and lock_release_generic to force non-ISR path (movi a11,0 replacing extui INTLEVEL extraction). (5) Boot now reaches app_main → MessageDispatcher ctor → spi_slave_hal_prepare_data (null ptr crash). |
| 2026-03-12 | `scripts/retw_hooks_rom.resc` | 23 ROM retw hooks (narrowed from 621) — currently DISABLED via comment in boot script. Kept for reference. |
| 2026-03-12 | `scripts/esp32-meadow-boot.resc` | Added ets_printf format string capture hook (reads A2 after entry at 0x40007d57). Captures all 16 ESP-IDF boot messages. Per-character hooks (uart_tx_one_char, uart_tx_char, uart_hal_write_txfifo) don't fire because retw bug corrupts putc1 function pointer returns. |
| 2026-03-12 | `platforms/meadow-esp32.repl` | Added uart0Mirror (ESP32_UART @ 0x60000000) for data bus mirror, I2S0 (0x3FF4F000), I2S1 (0x3FF68000). Zero "non existing peripheral" warnings during boot. |
| 2026-03-12 | `peripherals/SpiBridge.cs` | Created SPI bridge peripheral: ISPIPeripheral + IGPIOReceiver, cross-machine frame queues (toEsp32Queue/toStm32Queue), Ready/MessageWaiting GPIO outputs, public API for ESP32 side (DequeueFrameFromStm32, EnqueueFrameToStm32, SetReady, SetMessageWaiting). |
| 2026-03-12 | `scripts/esp32-meadow-boot.resc` | Phase 2 SPI changes: (1) Reverted to full SpiTransportProvider ctor skip (partial un-skip failed due to tlib bugs). (2) Removed SPI driver stubs, xTaskNotifyWait patch, Python frame inject/capture hooks — all for firmware SPI task that no longer runs. (3) IPC demotion now runs every dispatch (not one-shot). (4) Fixed DOUBLE_EXC diagnostic hook (RegisterValue → GetRegisterUnsafe). (5) Discovered `or a1,a1,a1` NOP corrupts A1 in Renode — use proper `nop` (00 20 f0). |
| 2026-03-12 | `bryancostanich/renode` branch `fix/xtensa-esp32-patches` | Pushed 5 tlib patches to GitHub fork. Submodule chain: tlib commit → Infrastructure commit → renode commit. All patches preserved from recovered build. |
| 2026-03-13 | tlib `arch/xtensa/translate.c` | **RETW source fix**: Changed `tcg_const_i32(1)` → `tcg_temp_local_new_i32()` + `tcg_gen_movi_i32(tmp, 1)` in `translate_retw()`. Non-local TCG temp was invalidated across `gen_helper_retw()` call, causing `gen_jump()` to use garbage return address. This is the 6th tlib patch (after arch_tlb_fill, WAITI, CCOUNT, clock_freq, win_helper). |
| 2026-03-13 | `scripts/esp32-meadow-boot-no-retw-hooks.resc` | Test script: copy of esp32-meadow-boot.resc with retw hooks removed (`_fix_retw` function, `include @scripts/retw_hooks.resc`) and SPI ctor skip removed. Validates tlib source fix. |
