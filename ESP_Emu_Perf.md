# Track: ESP_Emu_Perf — ESP32 Emulation Performance

Dual-machine mode (STM32 + ESP32) initially ran at ~0.001x real-time. The
STM32 alone runs at ~1:1. The bottleneck was ~46 IronPython hooks on the
ESP32 side, many firing on hot paths (every idle tick, context switch, lock
op, list insertion).

## Root Cause Analysis

The ESP32 had three categories of Python hooks:

| Category | Count | Fire Rate | Impact |
|----------|-------|-----------|--------|
| Diagnostic traces (SPI ISR, prepare_data) | ~25 | Every SPI transaction | High — removed in Phase 1 |
| Functional guards (list, lock, queue fixups) | ~10 | Every FreeRTOS kernel op | High — migrated to C# in Phase 3 |
| Tick injection hack (_idle_tick_direct) | 1 | Every idle task iteration | High — removed in Phase 2 (native timer works) |
| Functional one-shot/rare (printf, exception) | ~5 | Rare | Low — kept as Python |

IronPython hook overhead is 10-100x slower than compiled C# hooks (proven by
the STM32 migration: QspiDmaBypass, SvcPrimaskFix, MqReceiveIntercept).

## Phases

### Phase 1: Remove diagnostic trace hooks — COMPLETE ✅
**Effort:** 30 min | **Result:** Removed 35 hooks, 79 lines deleted

Stripped ~25 debug trace hook registrations and 3 dead Python function
definitions from `meadow-dual.resc`:

- [x] SPI task/ISR entry traces (`SPI_TASK_ENTER`, `SPI_ISR_ENTER`, etc.)
- [x] `_pd_entry_check`, `_prepare_data_trace` (verbose per-call diagnostics)
- [x] 11 hooks tracing every instruction inside `spi_slave_hal_prepare_data`
- [x] 13 hooks tracing every ISR path branch
- [x] `_demote_task_at_creation` (logs but doesn't act)
- [x] Verified ESP32 still boots and ESPCP frames flow after removal
- [x] Kept exception trace hooks — rare, useful for crash diagnosis

### Phase 2: Remove `_idle_tick_direct` — native CCOMPARE0 timer works — COMPLETE ✅
**Effort:** Half day | **Result:** Hook removed, native timer verified

Investigation revealed the `_idle_tick_direct` hook was actually PREVENTING
the native Xtensa CCOMPARE0 timer from working. Each injected tick triggered
the FreeRTOS handler which reset CCOMPARE0, moving the target before the
ComparingTimer could fire naturally.

Key findings:
- Renode's Xtensa CPU fully supports CCOMPARE0/CCOUNT timer (ComparingTimer)
- CCOMPARE0 fires interrupt 6, which is INTTYPE_TIMER (not cleared by OnGPIO deassert)
- The DPORT interrupt matrix correctly ORs with internal timer interrupts
- FreeRTOS tick at 100Hz (2.4M cycles at 240MHz = 10ms virtual time)
- With hook removed: 7000 native ticks in ~4 minutes, zero exceptions

The original Phase 2 plan (rewrite ESP32_TimerGroup with LimitTimer) was
unnecessary — the existing CCOMPARE0 mechanism is the correct solution.

- [x] Diagnosed why native timer appeared broken (injected ticks moving goalposts)
- [x] Removed `_idle_tick_direct` hook and Python function
- [x] Verified FreeRTOS tick fires naturally via CCOMPARE0 (100Hz)
- [x] No Timer Group rewrite needed — CCOMPARE0 is the real ESP32 tick source

### Phase 3: Migrate hot-path guards to compiled C# — COMPLETE ✅
**Effort:** 2 hours | **Result:** 8 hooks migrated, ~180 lines Python → 360 lines C#

All high-frequency functional hooks migrated to `peripherals/XtensaHotPathHooks.cs`,
a single C# file with ICPUWithHooks extension methods:

- [x] `InstallDispatchFixup` — IPC task demotion + ready list repair (every context switch)
- [x] `InstallVListInsertEndGuard` — corrupt list pointer fix (every list insert)
- [x] `InstallLockValidate` — corrupt lock handle fix (every mutex op)
- [x] `InstallFixPdA8` — TCG stale-global bug fix (every SPI prepare_data)
- [x] `InstallXqrDiag` — uninitialized queue fix (every queue receive)
- [x] `InstallQueueRecvSchedCheck` — scheduler state redirect (every queue receive)
- [x] `InstallThreadTaskGuard` — NULL function pointer guard (thread creation)
- [x] `InstallSetRegZero` — register zeroing (rare)

Only `_ets_printf_hook` remains as IronPython (low frequency, complex string formatting).

Verified: dual-machine boots, 3305 SPI frames in 2 minutes, zero exceptions.

### Phase 4: Investigate remaining Xtensa tlib bottlenecks (optional)
**Effort:** Unknown | **Expected speedup:** Unknown

After hook overhead is eliminated, profile whether tlib's Xtensa translation
itself is a bottleneck (window register ops, TCG code generation patterns).
This would require Renode-level profiling and is lower priority.

- [ ] Profile with hooks removed — measure instructions/sec for ESP32 vs STM32
- [ ] Check if window overflow/underflow handling has excessive overhead
- [ ] Compare with stock Renode Xtensa performance on simple benchmarks

## Results

### Hook Count Reduction

| Stage | IronPython Hooks | C# Hooks | Total |
|-------|-----------------|----------|-------|
| Before | ~46 | 0 | ~46 |
| After Phase 1 | ~11 | 0 | ~11 |
| After Phase 2 | ~10 | 0 | ~10 |
| After Phase 3 | 3 (ets_printf + 2 exception traces) | 8 | 11 |

### Performance (estimated)

| Metric | Before | After All Phases |
|--------|--------|-----------------|
| IronPython hooks on hot paths | ~36 | 1 (ets_printf, low freq) |
| Native FreeRTOS tick | Broken (hack) | Working (CCOMPARE0) |
| SPI frames in 2 min | ~0 (timeout) | 3305 |
| Exceptions | Frequent | Zero |

## Files

| File | Role |
|------|------|
| `scripts/meadow-dual.resc` | Dual-machine script (cleaned up) |
| `peripherals/XtensaHotPathHooks.cs` | All 8 compiled C# hooks for ESP32 |
| `peripherals/ESP32_TimerGroup.cs` | Timer Group stub (RTC calibration only) |
| `peripherals/ESP32_DPORT.cs` | Interrupt matrix (functional) |
| `peripherals/QspiDmaBypass.cs` | Reference: C# hook pattern (STM32) |
| `peripherals/MqReceiveIntercept.cs` | Reference: C# hook pattern (STM32) |
