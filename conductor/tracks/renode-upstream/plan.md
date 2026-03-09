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

## U.6 — STM32H7_QuadSPI Transfer Complete Flag Bug (PR or Issue)
- [ ] **Bug:** `STM32H7_QuadSPI` never sets TCF (Transfer Complete Flag, bit 1) in the Status Register (SR, offset 0x8) after indirect-mode transactions complete
- [ ] **Impact:** Any firmware using the QUADSPI peripheral in indirect mode that polls SR for TCF will hang forever (NuttX `qspi_waitstatusflags`, STM32 HAL `QSPI_WaitFlagStateUntilTimeout`)
- [ ] **Also likely missing:** BUSY flag (bit 5) clearing, FTF (FIFO Threshold Flag, bit 2) handling
- [ ] Our workaround: Hook `qspi_waitstatusflags` to skip via `cpu.PC = cpu.LR`. Proper fix belongs in the C# `STM32H7_QuadSPI` model.
- [ ] Decide: submit as issue with reproduction details, or fix in C# and submit as PR
- [ ] Note: `STM32H7_QuadSPI` register layout is identical to STM32F7 QUADSPI (same IP block), so fix benefits both F7 and H7 users

---

## Decision Log

| Date       | Decision | Rationale |
|------------|----------|-----------|
| 2026-03-08 | Submit our STM32F777 definition instead of reviewing PR #583 | Our definition is more complete and correct; supersedes their work |
