# Plan: Upstream Contributions to Renode

## Track ID: `renode-upstream`
## Status: `planned`
## Repo: User's fork of `renode/renode`

---

## U.1 — STM32F777 Platform Definition (PR)
- [ ] Submit `stm32f777.repl` as a PR to `renode/renode`
- [ ] Supersedes PR #583 (which has SRAM size bug, incorrect GPIO reset values, missing SYSCFG routing, no QSPI/SDRAM, broken RCC)
- [ ] Our definition includes: correct SRAM split, full timer set (14), proper GPIO reset values, SYSCFG→EXTI routing, DWT, ADC, all 6 SPI, all 3 CAN, all 8 UART/USART, flash controller
- [ ] Wait until platform is validated with NuttX boot (`renode-platform` Phase 1B+ complete)
- [ ] Include test `.resc` script if Renode contribution guidelines require it

## U.2 — SVC-with-PRIMASK Bug Fix (PR or Issue)
- [ ] **Bug:** Renode v1.16.1 silently skips `SVC` when `PRIMASK=1`. Per ARMv7-M architecture, this should escalate to HardFault (synchronous, precise).
- [ ] **Impact:** Breaks any RTOS that uses PRIMASK for critical sections around SVC calls (NuttX, FreeRTOS patterns)
- [ ] **Location in Renode source:** Cortex-M CPU model, SVC instruction handling
- [ ] Decide: submit as bug report (issue) or attempt a fix (PR)
- [ ] Include minimal reproduction case (NuttX `arm_fullcontextrestore` with PRIMASK=1)

## U.3 — RCC Ready Bit Handling (PR or Issue)
- [ ] **Bug:** Built-in `STM32F4_RCC` does not auto-set clock ready bits (HSIRDY, HSERDY, PLLRDY, PLLI2SRDY, PLLSAIRDY) when corresponding enable bits are written
- [ ] **Impact:** Any firmware that enables a clock and polls the ready bit will hang forever (NuttX, STM32 HAL, bare-metal init code)
- [ ] **Also missing:** SW→SWS mirroring in RCC_CFGR (clock switch status never reflects selected clock)
- [ ] Our workaround: Python peripheral (`rcc.py`). Proper fix belongs in the C# `STM32F4_RCC` model.
- [ ] Decide: submit as issue with our Python implementation as reference, or port to C# and submit as PR

## U.4 — FPU Exception Frame Fix (Issue)
- [ ] **Bug:** Renode exception entry always uses standard frames (32 bytes, `EXC_RETURN` bit4=1) even when FPU is active. Should use extended frames (104 bytes, bit4=0) per ARMv7-M spec.
- [ ] **Impact:** Non-critical for most code (MSP restore uses hardware frame), but breaks stack pointer verification and any code that inspects saved FPU registers on the exception frame
- [ ] Submit as issue — this is a deeper fix in the Cortex-M exception entry model

## U.5 — Winbond W25QXXXJV Flash Peripheral (PR)
- [ ] Submit `Winbond_W25QXXXJV.cs` extending `GenericSpiFlash`
- [ ] ~30 lines: manufacturer ID `0xEF`, memory type `0x40`, size validation
- [ ] Wait until validated with NuttX MTD driver (`renode-platform` Phase 1B.5 complete)
- [ ] Winbond W25Q series is extremely common — high value contribution

## U.7 — GenericSpiFlash Winbond Status Register Fix (PR)
- [ ] **Issue:** `GenericSpiFlash` maps command `0x35` to `EnterQuadInputOutputMode` — on Winbond W25Q chips this is "Read Status Register 2". Similarly, `0x31` (Write SR2) is unrecognized, and `0x15` maps to Micron-style `ReadConfigurationRegister` instead of Winbond "Read Status Register 3".
- [ ] **Root cause:** GenericSpiFlash models Micron/Numonyx register architecture (1 status reg + volatile/non-volatile/enhanced config regs). Winbond uses 3 status registers with different command mappings.
- [ ] **Missing Winbond registers:**
  - SR1 (0x05/0x01) — WEL, BUSY, block protect bits (partially works today)
  - SR2 (0x35/0x31) — QE bit, SRP1, security register lock bits (broken: 0x35 = wrong command)
  - SR3 (0x15/0x11) — output driver strength, write protect, hold/reset (broken: 0x15 = wrong register)
- [ ] **Impact:** Firmware that configures quad mode, checks write protection, or reads security register locks via Winbond register layout gets wrong data or errors. Currently papered over by QSPI DMA bypass hooks.
- [ ] **Fix approach:** Override command handling in `Winbond_W25QXXXJV.cs` to remap 0x35→ReadSR2, 0x31→WriteSR2, 0x15→ReadSR3, 0x11→WriteSR3, and implement 3-register Winbond status model
- [ ] **Alternative:** Submit as GenericSpiFlash enhancement with vendor-specific register profiles
- [ ] Depends on U.5 (Winbond peripheral must be accepted upstream first)

## U.6 — STM32H7_QuadSPI Completeness (Feature Request / PR)
- [ ] **Issue:** `STM32H7_QuadSPI` is a minimal register stub — not a bug per se, but incomplete
- [ ] **Missing features:**
  - TCF (Transfer Complete Flag) never set in SR after indirect-mode transactions
  - No DMA request outputs (model doesn't expose them for `.repl` connection)
  - BUSY flag (bit 5) and FTF (FIFO Threshold Flag, bit 2) likely not handled
  - No actual indirect-mode read/write transaction logic
- [ ] **Impact:** Any firmware using QUADSPI in indirect mode with polling or DMA will hang (NuttX, STM32 HAL)
- [ ] Our workarounds: Skip `qspi_waitstatusflags` + DMA bypass hook that does direct memcpy
- [ ] Note: `STM32H7_QuadSPI` register layout is identical to STM32F7 QUADSPI (same IP block), so improvements benefit both F7 and H7 users
- [ ] Decide: submit as feature request issue, or implement full transaction logic in C# as PR

## U.8 — Xtensa `arch_tlb_fill` Wrong Error Code (PR)
- [x] **Bug:** `arch_tlb_fill()` in `tlib/arch/xtensa/helper.c` discards the real MMU exception cause code from `get_physical_address()` and returns `TRANSLATE_FAIL` (== `1`). The `TRY_FILL` macro passes this to `arch_raise_mmu_fault_exception()`, which uses it as the Xtensa exception cause. Since `SYSCALL_CAUSE` is also `1`, every MMU fault (TLB miss, store prohibited, etc.) is misreported as a SYSCALL exception.
- [x] **Impact:** On ESP32, the `_xt_syscall_exc` handler adds 3 to EPC1 (to skip the 3-byte SYSCALL instruction). When the real instruction is a 2-byte narrow instruction (e.g., `s32i.n`), this corrupts EPC1 to a non-instruction boundary, causing cascading PC misalignment crashes. Affects any Xtensa firmware with narrow instructions near MMU-faulting code, particularly FreeRTOS `vListInsertEnd`/`vListInsert` on ESP32.
- [x] **Fix:** Changed `return TRANSLATE_FAIL;` to `return ret;` so the actual cause code (e.g., `STORE_PROHIBITED_CAUSE=29`) propagates correctly. All Xtensa cause codes are non-zero, so the `TRY_FILL` error check still works.
- [x] **Location:** `src/Infrastructure/src/Emulator/Cores/tlib/arch/xtensa/helper.c`, line ~193
- [x] **Repo/Branch:** `bryancostanich/renode` fork, worktree `renode-retw-fix`, branch `fix/xtensa-retw`
- [x] **Commits:** tlib: `2520925`, Infrastructure: `bcc70d280`, renode: `e7098410`
- [ ] Submit PR upstream

---

## Decision Log

| Date       | Decision | Rationale |
|------------|----------|-----------|
| 2026-03-08 | Submit our STM32F777 definition instead of reviewing PR #583 | Our definition is more complete and correct; supersedes their work |
| 2026-03-08 | U.6 reclassified: QuadSPI TCF is incomplete peripheral, not a bug | `STM32H7_QuadSPI` is a minimal register stub — no DMA outputs, no transaction-completion logic. TCF not being set is an unimplemented feature, not broken behavior. DMA issue is the same root cause (model doesn't support it). |
| 2026-03-08 | CortexM register access not an upstream issue | `cpu.SetRegister()` hanging is likely IronPython type mismatch, not a Renode defect. `GetRegisterUnsafe` deprecation warning points to `GetRegister` as correct API. Not worth filing upstream. |
