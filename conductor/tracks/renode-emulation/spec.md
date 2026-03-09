# Specification: Renode Emulation for Meadow.OS

## Track ID
`renode-emulation`

## Problem Statement
The Meadow.OS project has been rebased onto a newer NuttX version. The previous emulation setup relied on a custom emulator fork that is no longer available. Development and testing currently requires physical Meadow F7 Micro hardware, slowing iteration cycles.

## Goal
Establish a working Renode-based emulation environment for the rebased Meadow.OS that enables:
1. Booting NuttX to the HCOM shell without physical hardware
2. Running kernel-level and OS-level tests in CI
3. Debugging with GDB
4. Emulating key peripherals: QSPI flash, UART, timers, basic GPIO

## Repository Structure
Emulation lives in `Meadow.OS.Emulator/` — a **separate repo**, sibling to `Meadow.OS/`. This keeps emulation tooling out of the OS source tree. The emulator repo references Meadow.OS build artifacts via sibling path (`../Meadow.OS/`).

## Why Renode

- **Native STM32F7 support:** Renode ships `stm32f746.repl` — an STM32F746 platform definition with Cortex-M7, NVIC, correct memory map, and peripheral models. The F746 and F777 share the same base addresses and peripheral set.
- **Peripheral coverage out-of-the-box:**
  - USART (STM32F7_USART model) — 4 instances
  - SPI (STM32SPI) — 3 instances
  - I2C (STM32F7_I2C) — 4 instances
  - Timers (STM32_Timer) — 14 instances with GPIO AF mapping
  - GPIO (STM32_GPIOPort) — 11 ports (A-K)
  - DMA (STM32DMA) — 2 controllers
  - SDMMC (STM32FSDMMC) — with DMA support
  - Ethernet (SynopsysEthernetMAC)
  - RNG, RTC, EXTI, SYSCFG
- **Declarative platform descriptions:** `.repl` files are text-based, diffable, version-controllable. Adding a peripheral = adding a few lines, not C code.
- **GDB integration:** Built-in GDB server.
- **NuttX proven:** Renode is actively used with NuttX by the community.
- **CI-friendly:** Headless mode, Robot Framework integration for automated testing.
- **SVD support:** Can apply STM32F7x6.svd for register-level debugging.

## Architecture Context

### STM32F746.repl → STM32F777 Delta
The existing `stm32f746.repl` provides:
- CPU: Cortex-M7 @ 462 MIPS, systick @ 216MHz ✅
- Flash: 2MB @ 0x08000000 ✅
- SRAM: 384KB @ 0x20000000 ⚠️ (stock F746 has single block; F777 needs DTCM 128KB @ 0x20000000, SRAM1 368KB @ 0x20020000, SRAM2 16KB @ 0x2007C000)
- External memory: 256MB @ 0xC0000000 ⚠️ (resize to 32MB for Meadow — matches CONFIG_HEAP2_SIZE)
- ROM: 64KB @ 0x1FFF0000 ✅

Needed adaptations for STM32F777:
- **QSPI flash:** MappedMemory stub at 0x90000000 (32MB) for initial bring-up. Proper SPI flash model later.
- **USART1 as console:** Already present, just needs wiring in `.resc` script
- **Additional UARTs:** F777 has UART4/5/7/8 not in F746 file (add as needed)
- **HCOM transport:** On real hardware, HCOM uses USB CDC-ACM (`/dev/ttyACM0`), not UART. Renode cannot emulate USB device controllers, so emulation needs a defconfig that redirects HCOM to a UART.
- **Crypto accelerator:** F777 has hardware crypto (CRYP/HASH) — skip initially
- **Memory regions:** Split single 384KB sram into DTCM (128KB @ 0x20000000), SRAM1 (368KB @ 0x20020000), SRAM2 (16KB @ 0x2007C000) — required for correct kernel/user memory isolation
- **SDRAM via FMC:** Already have `fsmcBank1` at 0xC0000000, adjust size to 32MB (CONFIG_HEAP2_SIZE)

### CONFIG_BUILD_PROTECTED is Non-Negotiable
Investigation confirmed that protected build mode is **architecturally fundamental** to Meadow.OS:

1. **Two-binary architecture:** Build produces `nuttx.bin` (kernel, 1MB @ 0x08000000) and `nuttx_user.bin` (HCOM + apps, 1MB @ 0x08100000). HCOM is too large to fit in a single flat image on 2MB flash.
2. **HCOM runs in user space:** `hcom_main` is `CONFIG_INIT_ENTRYPOINT` — a user-space app using syscalls for kernel services. The entire app stack (MBEDTLS, networking, file I/O) assumes user-space execution.
3. **MPU grants peripheral access:** `stm32_boot.c` configures MPU regions to let user-space HCOM access GPIO, QSPI flash, and Ethernet SRAM.
4. **Memory split is hard-coded:** Kernel: 128KB SRAM @ 0x20020000. User: 240KB SRAM @ 0x20040000. Separate linker scripts enforce this.

**Implication for emulation:** Renode's Cortex-M7 MPU support must be verified early. However, the existing `nsh` defconfig (FLAT mode) provides a safe first boot target.

## Recommended Approach
**Renode-first, incremental bring-up from FLAT → PROTECTED**

1. **Phase 1A:** Boot `nsh` config (FLAT mode) in Renode — prove the platform works
2. **Phase 1B:** Boot `meadow_os` config (PROTECTED mode) — validate MPU support, load both kernel + user binaries
3. **Phase 1C:** HCOM bring-up — dual serial, protocol testing
4. **Phase 2:** NuttX `sim` target as a lightweight complement for OS-logic testing
5. **Phase 3:** CI integration

## Key Decisions Made

| Decision | Rationale |
|----------|-----------|
| Separate repo (`Meadow.OS.Emulator`) | Keep emulation tooling out of OS source; different cadence; sibling directory to `Meadow.OS/` |
| Renode as emulation platform | Native STM32F7 support, correct Cortex-M7 arch, declarative platform files, no fork maintenance |
| Start with `nsh` (FLAT), move to `meadow_os` (PROTECTED) | De-risks boot bring-up; `nsh` defconfig already exists for the Meadow board |
| QSPI as MappedMemory stub initially | Avoids needing a proper SPI flash model for first boot; layer in later |
| Separate build script in emulator repo | Emulation build will diverge from hardware build; converge over time as feature parity grows |

## Success Criteria
- [x] NuttX boots to NSH shell in Renode (FLAT mode, `nsh` config)
- [x] NuttX boots with PROTECTED mode (`meadow_os` config, both binaries loaded)
- [ ] HCOM shell functional over emulated UART (via emulation-specific defconfig redirecting from USB CDC-ACM)
- [x] USART1 console output visible
- [x] GDB attach + step-through debugging works
- [ ] QSPI flash read/write functional (LittleFS mount)
- [ ] Unit tests (`--unittests=os,misc`) pass in emulation
- [ ] CI pipeline runs tests in Renode headless mode

### Dual-Chip Architecture: STM32F7 + ESP32 Coprocessor
The Meadow F7 board has two main chips:
- **STM32F777** — Main MCU running NuttX + Meadow.OS (kernel + HCOM)
- **ESP32** — Coprocessor providing WiFi, BLE, mesh networking, NTP, file system services

**Inter-chip communication:**
- **SPI2** with GPIO handshaking (CS: PI0, Ready: PI10, Reset: PF7)
- **ESPCP protocol:** Custom request-response, 27-byte headers, CRC8/CRC32, max 4092-byte SPI frames
- **UART5:** Secondary channel for ESP32 debug/monitor output
- **STM32 threads:** ESPCP thread (outgoing messages), event handler (incoming), UART monitor

**What ESP32 provides:** WiFi (connect, scan, AP mode, full POSIX socket API), BLE, mesh networking, NTP time sync, file system operations on ESP32 flash, battery charge level, core dump retrieval.

**Emulation strategy:** Start with ESPCP disabled (`CONFIG_MEADOW_ESPCP_MANAGER` not set) for initial bring-up. Then add an ESPCP SPI stub peripheral in Renode that responds to STM32 requests with mock data. Eventually, explore Renode dual-machine emulation with a real ESP32 machine for full-fidelity testing.

## Out of Scope (for now)
- Full dual-machine ESP32 emulation (Phase 4 goal, not initial)
- USB CDC-ACM device emulation (HCOM will use UART redirect instead)
- Hardware crypto accelerator (CRYP/HASH)
- LTDC display controller

## Open Questions

### Resolved
1. ~~**Renode MPU support:**~~ ✅ Works — protected mode boots, kernel/user transitions functional.
2. ~~**RCC/Clock tree:**~~ ✅ Renode's built-in RCC is insufficient. Custom Python peripheral (`rcc.py`) handles ready bits and clock switching.
3. ~~**HCOM transport:**~~ ✅ HCOM uses USB CDC-ACM. Emulation will redirect to UART via `meadow_os_emu` defconfig.
4. ~~**GDB register set:**~~ ✅ Verified — standard Cortex-M registers, full debugging support works.

### Open
5. **Renode SVC-with-PRIMASK bug:** Discovered that Renode silently skips SVC when PRIMASK=1 instead of escalating to HardFault. Workaround in place (IronPython hooks). Should this be reported upstream to Renode?
6. ~~**HCOM `/dev/nxupd` dependency:**~~ ✅ HCOM is designed to work without the coprocessor — the `/dev/nxupd` failure is non-fatal.
