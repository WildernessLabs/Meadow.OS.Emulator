# Track: ESP_Emu_Perf — ESP32 Emulation Performance

Dual-machine mode (STM32 + ESP32) runs at ~0.001x real-time. The STM32 alone
runs at ~1:1. The bottleneck is ~46 IronPython hooks on the ESP32 side, many
firing on hot paths (every idle tick, context switch, lock op, list insertion).

## Root Cause Analysis

The ESP32 has three categories of Python hooks:

| Category | Count | Fire Rate | Impact |
|----------|-------|-----------|--------|
| Diagnostic traces (SPI ISR, prepare_data) | ~25 | Every SPI transaction | High — should be removed |
| Functional guards (list, lock, queue fixups) | ~10 | Every FreeRTOS kernel op | High — need C# migration |
| Functional one-shot/rare (printf, exception) | ~5 | Rare | Low — fine as Python |

IronPython hook overhead is 10-100x slower than compiled C# hooks (proven by
the STM32 migration: QspiDmaBypass, SvcPrimaskFix, MqReceiveIntercept).

## Phases

### Phase 1: Remove diagnostic trace hooks
**Effort:** 30 min | **Expected speedup:** 2-3x

Strip ~25 debug trace hooks from `meadow-dual.resc` that were used during
ESP32 bring-up but are not needed for operation:

- [ ] Lines 886-891: SPI task/ISR entry traces (`SPI_TASK_ENTER`, `SPI_ISR_ENTER`, etc.)
- [ ] Lines 892-893: `_pd_entry_check`, `_prepare_data_trace` (verbose per-call diagnostics)
- [ ] Lines 895-905: 11 hooks tracing every instruction inside `spi_slave_hal_prepare_data`
- [ ] Lines 907-919: 13 hooks tracing every ISR path branch
- [ ] Line 884: `_demote_task_at_creation` (logs but doesn't act)
- [ ] Verify ESP32 still boots and ESPCP frames flow after removal
- [ ] Keep exception trace hooks (lines 929-930) — rare, useful for crash diagnosis

### Phase 2: Replace `_idle_tick_direct` with hardware timer peripheral
**Effort:** Half day | **Expected speedup:** 2-5x (eliminates highest-frequency hook)

The FreeRTOS tick currently comes from a Python hook on the idle task that
fires on every iteration and manually calls `cpu.OnGPIO(6, True/False)` every
50th hit. Replace with a real Renode timer peripheral.

- [ ] Rewrite `ESP32_TimerGroup.cs` using Renode's `LimitTimer` infrastructure
  - Register layout from ESP32 TRM Chapter 18 (Timer Group):
    - `TIMGn_TxCONFIG_REG` (0x00): prescaler, auto-reload, enable
    - `TIMGn_TxLO_REG` (0x04) / `TIMGn_TxHI_REG` (0x08): counter value
    - `TIMGn_TxALARMLO_REG` (0x10) / `TIMGn_TxALARMHI_REG` (0x14): alarm value
    - `TIMGn_TxLOADLO_REG` (0x18) / `TIMGn_TxLOADHI_REG` (0x1C): reload value
    - `TIMGn_Tx_INT_ENA_REG`, `TIMGn_Tx_INT_RAW_REG`, `TIMGn_Tx_INT_CLR_REG`
  - Keep existing RTC calibration ready bit logic (`RTC_CALI_RDY`)
  - Timer fires IRQ output on alarm match when interrupt enabled
- [ ] Wire timer interrupt in `meadow-esp32.repl`:
  ```
  timerGroup0: Timers.ESP32_TimerGroup @ sysbus 0x3FF5F000
      IRQ -> dport@14
  ```
  (Source 14 = TG0_T0_LEVEL — verify from ESP-IDF interrupt allocation table)
- [ ] Remove `_idle_tick_direct` hook and PS manipulation from `meadow-dual.resc`
- [ ] Verify FreeRTOS tick timing matches expectations (100Hz or 1000Hz depending on config)

### Phase 3: Migrate hot-path guards to compiled C#
**Effort:** 1-2 days | **Expected speedup:** 5-10x on remaining hooks

Migrate the remaining high-frequency functional hooks from IronPython to C#,
following the pattern established for STM32 (see `QspiDmaBypass.cs`, etc.):

- [ ] `_vListInsertEnd_guard` — fires on every FreeRTOS list insertion
  - Validates pxIndex and pxNext pointers, fixes corrupt entries
  - Pattern: `ICPUWithHooks` extension method, register with `cpu.AddHook`
- [ ] `_lock_validate` — fires on every lock/mutex operation
  - Validates lock handle, clears corrupt handles
- [ ] `_dispatch_fixup` — fires on every `vTaskSwitchContext`
  - Demotes IPC tasks from priority 24→0, fixes ready list pointers
  - Most complex hook — includes FreeRTOS linked list manipulation
- [ ] `_xqr_diag` — fires on `xQueueGenericReceive`
  - Initializes uninitialized queue structures
- [ ] `_queue_recv_sched_check` — fires on queue receive
  - Skips to scheduler-active path when scheduler not yet started
- [ ] `_fix_pd_a8` — fires on `prepare_data` entry
  - Fixes TCG stale-global bug (reads [a2+0], sets a8 if wrong)

Each C# hook should be a separate `.cs` file in `peripherals/` following the
`ICPUWithHooks` + `[AutoParameter] Machine machine` pattern.

### Phase 4: Investigate remaining Xtensa tlib bottlenecks (optional)
**Effort:** Unknown | **Expected speedup:** Unknown

After hook overhead is eliminated, profile whether tlib's Xtensa translation
itself is a bottleneck (window register ops, TCG code generation patterns).
This would require Renode-level profiling and is lower priority.

- [ ] Profile with hooks removed — measure instructions/sec for ESP32 vs STM32
- [ ] Check if window overflow/underflow handling has excessive overhead
- [ ] Compare with stock Renode Xtensa performance on simple benchmarks

## Success Criteria

| Metric | Current | After Phase 1 | After Phase 2 | After Phase 3 |
|--------|---------|---------------|---------------|---------------|
| Virtual time / wall time | ~0.001x | ~0.003x | ~0.01x | ~0.05-0.1x |
| STM32 boot to HCOM ready | ~10-15 min | ~5-7 min | ~2-3 min | ~1-2 min |
| CLI `device info` works | timeout | maybe | yes | yes |

## Files

| File | Role |
|------|------|
| `scripts/meadow-dual.resc` | Dual-machine script with all ESP32 hooks |
| `peripherals/ESP32_TimerGroup.cs` | Timer Group stub → real timer (Phase 2) |
| `peripherals/ESP32_DPORT.cs` | Interrupt matrix (already functional) |
| `platforms/meadow-esp32.repl` | ESP32 platform definition (timer wiring) |
| `peripherals/QspiDmaBypass.cs` | Reference: C# hook pattern (STM32) |
| `peripherals/MqReceiveIntercept.cs` | Reference: C# hook pattern (STM32) |
