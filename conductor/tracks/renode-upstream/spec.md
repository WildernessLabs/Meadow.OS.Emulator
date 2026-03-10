# Specification: Upstream Contributions to Renode

## Track ID
`renode-upstream`

## Problem Statement
During Meadow.OS emulator development, we discovered several bugs and gaps in Renode that affect any STM32F7 or Cortex-M7 user. Contributing fixes upstream benefits the community and reduces our maintenance burden.

## Goal
Contribute validated fixes and platform definitions back to the Renode project:
1. STM32F777 platform definition (supersedes buggy PR #583)
2. Critical Cortex-M7 CPU bugs (SVC-with-PRIMASK, FPU frame)
3. RCC clock controller ready-bit handling
4. Winbond W25QXXXJV flash peripheral

## Repo
User's fork of `renode/renode` (already forked)

## Contribution Items

| ID | Item | Type | Gate | Impact |
|----|------|------|------|--------|
| U.1 | STM32F777 platform definition | PR | After `renode-platform` 1B+ validated | Supersedes PR #583; correct SRAM, RCC, GPIO, full peripherals |
| U.2 | SVC-with-PRIMASK bug | PR or Issue | Minimal repro ready | Breaks any RTOS using PRIMASK around SVC (NuttX, FreeRTOS) |
| U.3 | RCC ready bit handling | PR or Issue | Our `rcc.py` as reference | Hangs any firmware polling clock ready bits (NuttX, STM32 HAL) |
| U.4 | FPU exception frame | Issue | Documented | Non-critical but spec-incorrect; breaks stack verification code |
| U.5 | Winbond W25QXXXJV flash | PR | After `renode-platform` 1B.5 validated | ~30-line `GenericSpiFlash` subclass; very common flash chip |
| U.6 | STM32H7_QuadSPI TCF bug | PR or Issue | Minimal repro ready | QUADSPI SR never sets Transfer Complete Flag (TCF, bit 1) after transactions; firmware polling `qspi_waitstatusflags` hangs forever |
| U.7 | GenericSpiFlash Winbond register fix | PR | After U.5 accepted | Command 0x35/0x31/0x15 mapped to wrong operations; Winbond 3-register model vs Micron config registers |
