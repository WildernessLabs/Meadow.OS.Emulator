# Upstream Fixes

Quick-reference tracker for fixes made to upstream dependencies. Full details live in the respective track plans under `conductor/tracks/`.

## Renode ([renode/renode](https://github.com/renode/renode))

See [conductor/tracks/renode-upstream/plan.md](conductor/tracks/renode-upstream/plan.md) for full details.

| ID | Fix | Repo / Branch | Commit | PR Status |
|----|-----|---------------|--------|-----------|
| U.1 | STM32F777 platform definition | — | — | Planned |
| U.2 | SVC-with-PRIMASK bug — NVIC silently masks SVCall when PRIMASK=1 instead of letting it through (per ARMv7-M ARM B1.5.14, SVC is synchronous and should escalate to HardFault, not be skipped) | `bryancostanich/renode` Infrastructure:`meadow-emulator-fixes` | Infrastructure:`d18c1f6fe`, parent:`0ac18629` | Fixed locally |
| U.3 | RCC ready bit handling | — | — | Planned |
| U.4 | FPU exception frame fix | — | — | Planned |
| U.5 | Winbond W25QXXXJV flash peripheral | — | — | Planned |
| U.6 | STM32H7_QuadSPI completeness | — | — | Planned |
| U.7 | GenericSpiFlash Winbond status registers | — | — | Planned |
| U.8 | Xtensa `arch_tlb_fill` wrong error code | `bryancostanich/renode` `fix/xtensa-retw` (worktree: `renode-retw-fix`) | tlib:`2520925` | Fixed locally |
| U.9 | STM32F4 RTC backup registers too few — `BackupEnd = 0x9C` only covers 20 registers, STM32F7 has 32 (needs `0xCC`) | `bryancostanich/renode` Infrastructure:`meadow-emulator-fixes` | Infrastructure:`d18c1f6fe`, parent:`0ac18629` | Fixed locally |
