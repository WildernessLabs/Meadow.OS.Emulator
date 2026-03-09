# Plan: Renode Platform Definition & Boot Bring-Up

## Track ID: `renode-platform`
## Status: `in_progress`
## Repo: `Meadow.OS.Emulator` (sibling to `Meadow.OS/`)

---

## Phase 1A: First Boot — NSH in Renode (FLAT mode) ✅ COMPLETE
**Goal:** Prove Renode can boot NuttX on the Meadow F7 platform
**Build config:** `nsh` (FLAT mode, no MPU, single binary)
**Binary:** `nuttx.bin` only

### 1A.1 — Environment Setup ✅
- [x] Install Renode v1.16.1 (macOS `.dmg` → `/Applications/Renode.app`)
- [x] Verify installation: runs via `mono /Applications/Renode.app/Contents/MacOS/bin/Renode.exe`
- [x] Headless mode: `--disable-xwt --console` for interactive, `--disable-xwt` alone for telnet on port 1234

### 1A.2 — Build NuttX with `nsh` Config ✅
- [x] Created `build-emulator.sh` — handles both `nsh` (flat) and `meadow_os` (protected) configs
- [x] Includes version placeholder injection, mbedTLS build, artifact copy to `build/`
- [x] Added `tools/kconfig-tweak` shim for NuttX build compatibility

### 1A.3 — Create Meadow F7 Platform Description ✅
- [x] Created `platforms/meadow-f7.repl` based on `stm32f746.repl`
- [x] Fixed memory regions: DTCM (128KB), SRAM1 (368KB), SRAM2 (16KB)
- [x] Added SDRAM 32MB @ 0xC0000000, QSPI flash 32MB @ 0x90000000
- [x] Full peripheral set: 4x USART, 3x SPI, 4x I2C, 2x DMA, 14x Timers, 11x GPIO ports, Ethernet, LTDC, SDMMC, RTC, RNG, CAN, EXTI
- [x] Timer AF pin mappings, SVD reference (STM32F7x6)

### 1A.4 — Create Renode Launch Script ✅
- [x] Created `scripts/meadow-nsh.resc` (flat mode, single binary)
- [x] Created `run.sh` with auto-detection of Renode on PATH or in `/Applications/Renode.app`

### 1A.5 — Boot and Debug ✅
- [x] NSH config boots — required custom RCC Python peripheral for clock ready bits
- [x] Created `scripts/pydev/rcc.py` — auto-sets HSIRDY/HSERDY/PLLRDY/PLLI2SRDY/PLLSAIRDY, mirrors SW→SWS
- [x] Created `scripts/pydev/otg_fs.py` — USB OTG FS stub (AHBIDL, GRSTCTL, GINTSTS)
- [x] Created `scripts/pydev/flipflop.py` — simple read-back for PWR registers, LPTIM1

**Resolved open questions:**
- RCC/Clock tree: Renode's built-in STM32F4_RCC does NOT handle ready bits. Custom Python peripheral required.
- Flash wait states: MappedMemory region at 0x40023C00 is sufficient.

---

## Phase 1B: Protected Mode Boot ✅ COMPLETE
**Goal:** Boot full Meadow.OS with PROTECTED build (kernel + user binaries)
**Build config:** `meadow_os` (PROTECTED mode, MPU, two-pass build)
**Binaries:** `nuttx.bin` + `nuttx_user.bin`

### 1B.1 — Investigate Renode MPU Support ✅
- [x] Renode's Cortex-M7 model supports MPU configuration — protected mode boots without MPU-related faults
- [x] Kernel/user privilege transitions work correctly via SVC
- [x] MPU regions configured by NuttX at boot (GPIO, QSPI, Ethernet SRAM) — no issues observed

### 1B.2 — Build Meadow.OS (Protected Mode) ✅
- [x] `build-emulator.sh` supports `meadow_os` (default) and `nsh` configs
- [x] Two-pass build: pass2 → mbedTLS → pass1 → nuttx
- [x] Copies `nuttx.bin`, `nuttx_user.bin`, and `nuttx.elf` (for GDB) to `build/`

### 1B.3 — Load Both Binaries in Renode ✅
- [x] Created `scripts/meadow-os.resc` — loads kernel @ 0x08000000, user @ 0x08100000
- [x] Created `scripts/meadow-os-gdb.resc` — same but with GDB server on port 3333, starts paused
- [x] Created `scripts/meadow-os-diag.resc` — diagnostic hooks for SVC/context-switch tracing

### 1B.4 — Validate Protected Mode Boot ✅
- [x] Kernel boots, creates hpwork/lpwork threads, transitions to user space
- [x] AppBringUp task starts, runs `nx_start_application()`, exits cleanly
- [x] `hcom_main` starts and runs in user space
- [x] System enters healthy idle state (CPU in `wfi`)
- [x] **Critical bug found and fixed:** Renode SVC-with-PRIMASK bug (see Workarounds below)

### Workaround: SVC-with-PRIMASK (discovered during 1B.4)
**Root cause:** Renode v1.16.1 silently skips `SVC` when `PRIMASK=1`. On real ARMv7-M, this escalates to HardFault (which NuttX handles intentionally). The skip caused `arm_fullcontextrestore()` to fall through and `PANIC()`.

**Crash path traced via GDB:** `up_exit()` → `enter_critical_section()` (sets PRIMASK=1) → `arm_fullcontextrestore()` → `svc 0` (SKIPPED by Renode) → falls through → `PANIC()`

**Fix:** IronPython hooks at all 7 `SVC #0` instruction addresses unconditionally clear PRIMASK before execution. Addresses are binary-specific and must be re-scanned after kernel rebuild:
```bash
arm-none-eabi-objdump -d build/nuttx.elf | grep 'svc\s'
```

**Also identified (non-critical):** FPU frame mismatch — Renode always uses standard exception frames (32 bytes) but NuttX always calculates REG_R13 assuming extended frames (104 bytes). Off by 72 bytes but doesn't cause crashes since MSP restore comes from hardware frame.

### Phase 1B Boot Output (before Phase 1B.5 fixes)
```
stm32_boot.c@300-ERROR: Meadow version could not be determined
hcom_via_nx_access.c@94-/dev/nxupd Failed to open, errno:2
hcom_startup_manager.c@174-setup hcom nx access:-1
```

---

## Phase 1B+: Platform Description Refactor + Upgrade ✅ COMPLETE
**Goal:** Separate chip-level and board-level platform descriptions; add missing on-chip peripherals identified via PR #583 comparison
**Depends on:** Phase 1B ✅

### 1B+.1 — Refactor: Split into Chip + Board Files ✅
- [x] Created `platforms/stm32f777.repl` with all on-chip peripherals
- [x] Refactored `platforms/meadow-f7.repl` to `using "stm32f777.repl"` + board-level externals (SDRAM, QSPI flash)
- [x] QSPI flash size corrected from 32MB to 64MB
- [x] All `.resc` scripts verified working with refactored files

### 1B+.2 — Add Missing On-Chip Peripherals ✅
- [x] `MTD.STM32F4_FlashController` @ 0x40023C00 (replaces MappedMemory stub)
- [x] `Miscellaneous.DWT` @ 0xE0001000 (frequency: 216000000)
- [x] `Analog.STM32_ADC` x3 (ADC1/2/3, shared IRQ nvic@18)
- [x] `UART.STM32F7_USART` x4 (UART4/5/7/8) — note: `STM32_UART` type doesn't exist in Renode v1.16.1, same register layout as USART on F7
- [x] `fpuInterruptNumber: 81` on CPU definition
- [x] `CAN.STMCAN` x2 (CAN2/3, IRQs 63/104)
- [x] `SPI.STM32SPI` x3 (SPI4/5/6, IRQs 84/85/86)
- [x] `SPI.STM32H7_QuadSPI` @ 0xA0001000 (identical register layout to STM32F7 QUADSPI)

Deferred (not adding now):
- **Ethernet PHY** — Meadow uses LAN9355/ML, not LAN8742A. Add when networking phase begins.
- **CRC unit** — Hardware CRC not enabled in Meadow defconfig. Renode's `STM32F0_CRC` model is incorrect for F7 anyway.

---

## Phase 1B.5: QSPI Flash + Board Init Chain — IN PROGRESS
**Goal:** Get `board_late_initialize()` completing fully — QSPI flash, filesystem, config, `/dev/nxupd` registration
**Depends on:** Phase 1B+ ✅
**Blocks:** `renode-hcom` track (HCOM needs `/dev/nxupd` which is registered at end of this chain)

### 1B.5.1 — Create Winbond W25QXXXJV Flash Peripheral ✅
- [x] Created `peripherals/Winbond_W25QXXXJV.cs` extending `GenericSpiFlash`
  - Manufacturer ID: `0xEF`, Memory Type: `0x40`, size validation 2MB–64MB
- [x] Loaded at runtime via AdHoc Compiler (`include @peripherals/Winbond_W25QXXXJV.cs`)

### 1B.5.2 — Update Platform Description ✅
- [x] Added QUADSPI controller (`STM32H7_QuadSPI @ 0xA0001000`) to `stm32f777.repl`
- [x] Added W25Q flash + 64MB backing memory to `meadow-f7.repl`
- [x] Removed old QSPI MappedMemory stub

### 1B.5.3 — Fix GPIO Board Version Detection ✅
- [x] Hook at 0x08005668 (in `meadow_hw_version_find_device_ver`, after `bl` returns from GPIO function)
- [x] Writes 0x01 (CCMv2) to stack slot via `machine.SystemBus.WriteDoubleWord(sp + 12, 0x01)`
- [x] Note: `cpu.SetRegister()`, `cpu.SetRegisterUlong()`, and `cpu.R0` all cause hangs or errors on CortexM in hooks. Stack write is the reliable approach.
- [x] Address is binary-specific — must be re-scanned after kernel rebuild

### 1B.5.4 — Fix QUADSPI Status Register Polling ✅
- [x] Renode's `STM32H7_QuadSPI` doesn't set TCF (Transfer Complete Flag) in SR after transactions
- [x] Hook at `qspi_waitstatusflags` (0x08036bcc) skips function via `cpu.PC = cpu.LR`
- [x] Tracked as U.6 in `renode-upstream` (reclassified as incomplete peripheral, not a bug)

### 1B.5.5 — Fix QUADSPI DMA Transfers ✅
- [x] Renode's `STM32H7_QuadSPI` doesn't drive DMA requests → 800ms watchdog timeout (ETIMEDOUT)
- [x] Hook at `qspi_memory_dma` entry (0x08036db4) does direct memcpy from/to flash backing memory
- [x] Reads `struct qspi_meminfo_s` from R1: flags(+0,u8) buflen(+8,u32) addr(+12,u32) buffer(+20,ptr)
- [x] `QSPIMEM_WRITE = 0x04` → copy buf→flash; else (read) → copy flash→buf
- [x] Second hook at 0x0803731e: fix return value by writing 0 to stack slot (sp+44)
- [x] Created `scripts/pydev/qspi_dma_bypass.py` as reference documentation

### 1B.5.6 — Update .resc Scripts ✅
- [x] Added `include @peripherals/Winbond_W25QXXXJV.cs` to all .resc scripts
- [x] Added GPIO version hook + QSPI wait skip + DMA bypass to meadow-os, meadow-os-gdb, meadow-os-diag
- [x] Added optional UART file backend to meadow-os.resc (commented out)

### 1B.5.7 — Validate Boot Chain ✅
- [x] Version detection passes (CCMv2)
- [x] MTD flash driver initializes — partition table detected (7 partitions, 64MB total)
- [x] LittleFS format + mount succeeds on blank flash (initial mount fails → auto-format → remount OK)
- [x] Filesystem creation completes
- [x] System reaches idle (no crash)
- [x] Mono runtime not found (expected — not flashed yet)
- [ ] Verify `hcom_main` opens `/dev/nxupd` successfully — needs investigation

### Current Boot Output
```
MTD Geo info - total erase sectors 16384, eraseSize 4096 block/page size 256
Runtime       part:0, size: 3145728 ( 3072 Kb), Byte Offset:0x00000000, Block Offset     0
Meadow update part:1, size: 1835008 ( 1792 Kb), Byte Offset:0x00300000, Block Offset 12288
OTA State     part:2, size:    4096 (    4 Kb), Byte Offset:0x004c0000, Block Offset 19456
OS Persisted  part:3, size:    4096 (    4 Kb), Byte Offset:0x004c1000, Block Offset 19472
Assert Data   part:4, size:   32768 (   32 Kb), Byte Offset:0x004c2000, Block Offset 19488
Unused        part:5, size:  221184 (  216 Kb), Byte Offset:0x004ca000, Block Offset 19616
File System   part:6, size:61865984 (60416 Kb), Byte Offset:0x00500000, Block Offset 20480
Created 7 partitions, Total size:67108864 bytes (65536 Kb)
F/S creation complete.
Meadow reset code: 0x00, reset count: 1, power cycle count 1
Mono runtime was not found flashed in external flash.
```

### 1B.5.8 — Flash Persistence (Future)
- [ ] Investigate Renode `MappedMemory` file backing for persistent flash contents
- [ ] Pre-populate flash with valid filesystem image if needed
- [ ] Document flash partition layout

---

## File Structure
```
Meadow.OS.Emulator/
├── platforms/
│   ├── stm32f777.repl              # STM32F777 chip-level platform (on-die peripherals only)
│   └── meadow-f7.repl              # Meadow F7 board (uses stm32f777.repl + external hw)
├── peripherals/
│   └── Winbond_W25QXXXJV.cs        # W25Q flash peripheral (C#, Phase 1B.5)
├── scripts/
│   ├── meadow-nsh.resc             # NSH boot (FLAT mode)
│   ├── meadow-os.resc              # Full Meadow.OS boot (PROTECTED mode)
│   ├── meadow-os-gdb.resc          # GDB-enabled (paused, port 3333)
│   ├── meadow-os-diag.resc         # Diagnostic with SVC/context-switch logging
│   ├── meadow-os-test.resc         # Test script with verbose logging
│   └── pydev/                      # Python peripherals and hooks
│       ├── rcc.py                  # RCC clock controller
│       ├── otg_fs.py               # USB OTG FS stub
│       ├── qspi_dma_bypass.py      # QSPI DMA bypass hook (reference)
│       ├── fix_svc_primask.py      # SVC PRIMASK workaround (reference)
│       ├── fix_fpu_frame.py        # FPU frame mismatch correction
│       └── flipflop.py             # Simple read-back peripheral
├── build/                           # Build artifacts (gitignored)
├── build-emulator.sh                # Build script
├── run.sh                           # Quick-launch wrapper
├── conductor/                       # Planning docs
│   ├── tracks.md
│   └── tracks/
│       ├── renode-platform/         # This track
│       ├── renode-hcom/             # HCOM bring-up
│       ├── renode-esp32/            # ESP32 coprocessor
│       ├── renode-ci/               # CI integration
│       └── renode-upstream/         # Upstream contributions
├── README.md
├── LICENSE
└── .gitignore
```

---

## Decision Log

| Date       | Decision | Rationale |
|------------|----------|-----------|
| 2026-03-08 | Separate repo (`Meadow.OS.Emulator`) | Keep emulation tooling out of OS source; different cadence; sibling to `Meadow.OS/` |
| 2026-03-08 | Renode as emulation platform | Native STM32F7 support (stm32f746.repl), correct Cortex-M7 architecture, declarative platform files, peripheral models included |
| 2026-03-08 | Start with `nsh` (FLAT), move to `meadow_os` (PROTECTED) | De-risks boot bring-up; existing `nsh` defconfig sidesteps MPU questions initially |
| 2026-03-08 | CONFIG_BUILD_PROTECTED is required | HCOM architecture depends on kernel/user split, MPU-granted peripheral access, and separate flash regions. Cannot run in FLAT mode long-term |
| 2026-03-08 | QSPI as MappedMemory stub initially | 32MB @ 0x90000000 simulates memory-mapped QSPI. Avoids needing SPI flash model for first boot |
| 2026-03-08 | Separate build script in emulator repo | Emulation build will diverge initially; converge over time as feature parity grows |
| 2026-03-08 | Skip bootloader in Renode, load at link-time addresses | Bootloader places kernel at 0x08040000 in combined image, but kernel is linked at 0x08000000. Loading individual binaries at link addresses avoids address mismatch. |
| 2026-03-08 | Custom RCC Python peripheral | Renode's built-in STM32F4_RCC does not set clock ready bits. Custom `rcc.py` handles HSIRDY/HSERDY/PLLRDY/etc. and SW→SWS mirroring. |
| 2026-03-08 | SVC PRIMASK workaround via inline hooks | Renode silently skips SVC when PRIMASK=1. IronPython hooks at all 7 SVC addresses clear PRIMASK before execution. Hooks are address-specific and must be updated when kernel is rebuilt. |
| 2026-03-08 | SVC hooks inline in .resc, not PyDevFromFile | Inline one-liner is simpler than a separate Python file for CPU hooks. `fix_svc_primask.py` kept as reference. |
| 2026-03-08 | C# peripheral for QSPI flash (not Python) | Renode's `GenericSpiFlash` base class handles all SPI NOR flash commands. Creating a `Winbond_W25QXXXJV` subclass (~30 lines of C#) gives us full flash emulation. Renode's AdHoc Compiler loads C# at runtime via `include @file.cs` — no Renode rebuild needed. |
| 2026-03-08 | Reuse `STM32H7_QuadSPI` controller for F7 | STM32F7 and H7 QUADSPI have identical register layouts (CR, DCR, SR, CCR, AR, DR). Only difference is base address (F7: 0xA0001000, H7: 0x52005000), handled in .repl. |
| 2026-03-08 | `/dev/nxupd` requires full QSPI/MTD chain | `board_late_initialize()` gates `hcom_nx_setup_mgr()` (which registers `/dev/nxupd`) behind successful QSPI flash init, version detection, MTD driver init, and filesystem setup. Cannot shortcut — need working QSPI flash. |
| 2026-03-08 | GPIO version detection hook for CCMv2 | Board version is detected via 4 GPIO pins. In Renode, unconnected inputs with pull-ups all read high (0x0F = F7V1/V2), triggering a flash chip ID query that fails. Hook to return 0x01 (CCMv2) skips the query and selects W25QXXXJV driver directly. |
| 2026-03-08 | Split platform into chip + board .repl files | `stm32f777.repl` has on-die peripherals only; `meadow-f7.repl` uses it and adds board-level hardware (SDRAM, QSPI flash chip, future ESP32/PHY). Makes chip definition reusable and keeps board-specific hardware clearly separated. |
| 2026-03-08 | QSPI flash size corrected to 64MB | Meadow board has 64MB W25Q flash, not 32MB. Previous stub was undersized. |
| 2026-03-08 | PR #583 comparison — selective adoption | Compared our platform against Renode PR #583 (stm32f777.repl). Adopted: flash controller, DWT, ADC, UARTs, FPU IRQ, CAN2/3, SPI4/5/6. Rejected: CRC (wrong model), Ethernet PHY (wrong chip). Confirmed our SRAM/RCC/GPIO/timer implementations are superior to PR's. |
| 2026-03-08 | Skip hardware CRC | Meadow defconfig does not enable hardware CRC. Renode's `STM32F0_CRC` model is also incorrect for F7 (different polynomial/register layout). |
| 2026-03-08 | Ethernet PHY deferred — LAN9355/ML | Meadow uses LAN9355/ML PHY, not LAN8742A. NuttX defconfig has `CONFIG_ETH0_PHY_LAN8742A=y` but actual board differs. Defer until networking phase. |
| 2026-03-08 | Dropped NuttX sim target | Renode provides everything the sim would, plus ARM fidelity and peripheral testing. Not worth maintaining a separate sim build. |
| 2026-03-08 | Peripheral verification ad-hoc, not formal track | On-chip peripheral fidelity (timers, GPIO, I2C/SPI, RTC) will be verified as Meadow.OS features exercise them, not as a standalone effort. |
| 2026-03-08 | GPIO version hook uses stack write, not register set | `cpu.SetRegister()`, `cpu.SetRegisterUlong()`, `cpu.R0` all fail in IronPython hooks on CortexM (hangs or missing attribute). Writing to stack via `machine.SystemBus.WriteDoubleWord(sp + 12, value)` is reliable. |
| 2026-03-08 | QSPI wait skip via `cpu.PC = cpu.LR` | `STM32H7_QuadSPI` doesn't set TCF in SR. Rather than replace the whole controller, skip `qspi_waitstatusflags` entirely. Flash I/O still works for partition detection (MTD reads geometry). Full read/write may need a proper controller later. |
| 2026-03-08 | DMA bypass via direct memcpy hook | QUADSPI doesn't drive DMA requests so transfers never complete (800ms timeout). Hook `qspi_memory_dma` to read `struct qspi_meminfo_s` and copy directly between RAM buffer and flash backing memory (0x90000000). Keeps W25QXXXJV C# peripheral for hardware fidelity while bypassing the broken DMA path. |
| 2026-03-08 | Keep W25QXXXJV C# peripheral (not PyDev bypass) | Attempted PyDev QUADSPI replacement but: (a) `self.SystemBus` unavailable in PyDev register handlers, (b) loses W25Q flash command fidelity. Kept built-in STM32H7_QuadSPI + C# W25Q + CPU hook bypass for DMA. |
| 2026-03-08 | `struct qspi_meminfo_s` layout (ARM32) | flags(+0,u8) addrlen(+1,u8) dummies(+2,u8) pad(+3) cmd(+4,u16) pad(+6-7) buflen(+8,u32) addr(+12,u32) key(+16,u32) buffer(+20,ptr). Total 24 bytes. Initial hook had wrong offsets (assumed all uint32_t) causing nonsensical values. |
| 2026-03-08 | `STM32_UART` type doesn't exist in Renode v1.16.1 | Must use `UART.STM32F7_USART` for all UART/USART instances — same register layout on STM32F7. |

## Open Questions

### Resolved
1. ~~**Renode MPU support:**~~ ✅ Works. Protected mode boots without MPU-related faults. Kernel/user privilege transitions function correctly.
2. ~~**RCC/Clock tree:**~~ ✅ Custom Python peripheral required. Renode's built-in STM32F4_RCC does not set ready bits.
3. ~~**GDB register set:**~~ ✅ Verified — GDB server exposes standard Cortex-M registers, breakpoints/stepi/backtrace/memory reads all work.
4. ~~**HCOM `/dev/nxupd` dependency:**~~ ✅ HCOM works without the ESP32 coprocessor, but `/dev/nxupd` must still be registered (requires QSPI flash chain in `board_late_initialize`). Phase 1B.5 addresses this.

5. ~~**`0x35` command conflict:**~~ ✅ Not an issue in practice — QUADSPI controller handles command routing, and our `qspi_waitstatusflags` skip means commands aren't fully executed through the SPI protocol layer anyway.
6. ~~**Flash partition layout:**~~ ✅ Resolved — NuttX creates 7 partitions on the 64MB QSPI flash (see boot output above). Blank flash (all 0xFF) boots successfully; LittleFS auto-formats and mounts.

### Open
7. **SVC/hook address automation:** Currently SVC hooks (7 addresses), GPIO version hook (1 address), QSPI wait skip (1 address), and DMA bypass hooks (2 addresses) are all hardcoded and must be manually re-scanned after kernel rebuild. Should automate this.
8. ~~**LittleFS mount -110 (ETIMEDOUT):**~~ ✅ Resolved — caused by QUADSPI not driving DMA requests (800ms watchdog timeout). Fixed by DMA bypass hook that does direct memcpy from/to flash backing memory. LittleFS auto-formats blank flash and mounts successfully.
9. **`/dev/nxupd` registration:** Need to verify whether `hcom_nx_setup_mgr()` now succeeds with working flash. If not, trace where it fails after filesystem setup.
10. ~~**CortexM register access from hooks:**~~ Not a Renode bug — we were using the wrong API. `GetRegisterUnsafe` is deprecated (Renode says to use `GetRegister`). `cpu.SetRegister()` hangs likely due to IronPython type mismatch, not a Renode defect. Named properties (`cpu.SP`, `cpu.PC`, `cpu.LR`, `cpu.PRIMASK`) and stack writes remain the practical approach for hooks.
