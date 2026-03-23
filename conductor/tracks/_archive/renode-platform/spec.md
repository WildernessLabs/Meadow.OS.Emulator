# Specification: Renode Platform Definition & Boot Bring-Up

## Track ID
`renode-platform`

## Problem Statement
The Meadow.OS project has been rebased onto a newer NuttX version. The previous emulation setup relied on a custom emulator fork that is no longer available. Development and testing currently requires physical Meadow F7 Micro hardware, slowing iteration cycles.

## Goal
Establish a working Renode-based platform definition and boot environment for the rebased Meadow.OS:
1. Chip-level STM32F777 platform definition (reusable, upstreamable)
2. Board-level Meadow F7 definition (chip + external peripherals)
3. NuttX booting to a healthy state in both FLAT and PROTECTED modes
4. Working QSPI flash with complete `board_late_initialize()` chain
5. GDB debugging support

## Repository Structure
Emulation lives in `Meadow.OS.Emulator/` — a **separate repo**, sibling to `Meadow.OS/`. This keeps emulation tooling out of the OS source tree. The emulator repo references Meadow.OS build artifacts via sibling path (`../Meadow.OS/`).

## Why Renode

- **Native STM32F7 support:** Renode ships `stm32f746.repl` — an STM32F746 platform definition with Cortex-M7, NVIC, correct memory map, and peripheral models. The F746 and F777 share the same base addresses and peripheral set.
- **Peripheral coverage out-of-the-box:** USART, SPI, I2C, Timers, GPIO, DMA, SDMMC, Ethernet, RNG, RTC, EXTI, SYSCFG
- **Declarative platform descriptions:** `.repl` files are text-based, diffable, version-controllable.
- **GDB integration:** Built-in GDB server.
- **NuttX proven:** Renode is actively used with NuttX by the community.
- **CI-friendly:** Headless mode, Robot Framework integration.
- **SVD support:** Can apply STM32F7x6.svd for register-level debugging.

## Architecture Context

### Platform Description: Chip + Board Separation
- **`stm32f777.repl`** (chip) — All on-die peripherals: CPU, NVIC, internal flash, SRAM (DTCM/SRAM1/SRAM2), ROM, all UART/USART, SPI, I2C, CAN, timers, DMA, GPIO, RCC, PWR, SYSCFG, EXTI, RTC, RNG, OTG_FS, LTDC, Ethernet MAC, SDMMC controller, ADC, DWT, flash controller
- **`meadow-f7.repl`** (board) — `using "stm32f777.repl"` + external SDRAM (32MB), QSPI flash (64MB W25Q), SD card, ESP32 coprocessor (future), LAN9355 PHY (future)

### STM32F746.repl → STM32F777 Delta
Key adaptations from the stock F746 definition:
- **SRAM:** Split single 384KB block into DTCM (128KB @ 0x20000000), SRAM1 (368KB @ 0x20020000), SRAM2 (16KB @ 0x2007C000)
- **Additional peripherals:** UART4/5/7/8, SPI4/5/6, CAN2/3, ADC x3, DWT, flash controller, `fpuInterruptNumber: 81`
- **GPIO reset values:** GPIOA 0xA8000000, GPIOB 0x00000280 (debug pins default to AF)
- **GPIO→EXTI routing:** Through SYSCFG mux (not direct)

### CONFIG_BUILD_PROTECTED is Non-Negotiable
Protected build mode is **architecturally fundamental** to Meadow.OS:
1. **Two-binary architecture:** `nuttx.bin` (kernel, 1MB @ 0x08000000) and `nuttx_user.bin` (HCOM + apps, 1MB @ 0x08100000)
2. **HCOM runs in user space:** `hcom_main` is `CONFIG_INIT_ENTRYPOINT` — uses syscalls for kernel services
3. **MPU grants peripheral access:** `stm32_boot.c` configures MPU regions for user-space HCOM
4. **Memory split is hard-coded:** Kernel: 128KB SRAM @ 0x20020000. User: 240KB SRAM @ 0x20040000.

### QSPI Flash + Board Init Chain
`board_late_initialize()` in `stm32_boot.c` runs:
```
QSPI controller init → GPIO version detect → MTD flash init → filesystem → config → hcom_nx_setup_mgr → register /dev/nxupd
```
This chain must complete fully for HCOM to start. Requires:
- Working QSPI flash peripheral (Winbond W25QXXXJV via C# `GenericSpiFlash` subclass)
- GPIO version detection hook (CCMv2 board version)

## Key Decisions

| Decision | Rationale |
|----------|-----------|
| Separate repo (`Meadow.OS.Emulator`) | Keep emulation tooling out of OS source; different cadence |
| Renode as emulation platform | Native STM32F7 support, correct Cortex-M7 arch, declarative platform files |
| Chip + board .repl separation | `stm32f777.repl` is reusable/upstreamable; `meadow-f7.repl` adds board-specific hardware |
| Start with `nsh` (FLAT), move to `meadow_os` (PROTECTED) | De-risks boot bring-up |
| C# peripheral for QSPI flash | `GenericSpiFlash` base class handles all SPI NOR commands; ~30-line subclass |
| Reuse `STM32H7_QuadSPI` controller for F7 | Identical register layout, different base address only |

## Success Criteria
- [x] NuttX boots to NSH shell in Renode (FLAT mode, `nsh` config)
- [x] NuttX boots with PROTECTED mode (`meadow_os` config, both binaries loaded)
- [x] USART1 console output visible
- [x] GDB attach + step-through debugging works
- [ ] Platform split into chip (`stm32f777.repl`) + board (`meadow-f7.repl`)
- [ ] All STM32F777 on-chip peripherals defined (including new: UART4/5/7/8, SPI4/5/6, CAN2/3, ADC, DWT)
- [ ] QSPI flash read/write functional (W25QXXXJV C# peripheral)
- [ ] `board_late_initialize()` completes fully (`/dev/nxupd` registered)

## Out of Scope (handled by other tracks)
- HCOM bring-up over emulated UART → `renode-hcom`
- ESP32 coprocessor emulation → `renode-esp32`
- CI pipeline integration → `renode-ci`
- Upstream contributions to Renode → `renode-upstream`

## Open Questions

### Resolved
1. ~~**Renode MPU support:**~~ ✅ Works — protected mode boots, kernel/user transitions functional.
2. ~~**RCC/Clock tree:**~~ ✅ Renode's built-in RCC is insufficient. Custom Python peripheral (`rcc.py`) handles ready bits and clock switching.
3. ~~**GDB register set:**~~ ✅ Verified — standard Cortex-M registers, full debugging support works.
4. ~~**HCOM `/dev/nxupd` dependency:**~~ ✅ Requires full QSPI/MTD chain. Phase 1B.5 addresses this.

### Open
5. **Renode SVC-with-PRIMASK bug:** Workaround in place (IronPython hooks). Upstream fix tracked in `renode-upstream` U.2.
6. **SVC address automation:** Currently the 7 SVC hook addresses are hardcoded and must be manually re-scanned after kernel rebuild. Should automate.
7. **`0x35` command conflict:** NuttX W25QXXXJV driver uses `0x35` as "Read Status Register-2" but `GenericSpiFlash` maps it to `EnterQuadInputOutputMode`. Need to verify compatibility.
8. **Flash partition layout:** What partitions does NuttX create on the QSPI flash? Need to understand if blank flash (all 0xFF) is sufficient for first boot.
