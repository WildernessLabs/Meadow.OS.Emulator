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

### Current Boot Output
```
stm32_boot.c@300-ERROR: Meadow version could not be determined
hcom_via_nx_access.c@94-/dev/nxupd Failed to open, errno:2
hcom_startup_manager.c@174-setup hcom nx access:-1
```
HCOM fails because `/dev/nxupd` (coprocessor communication device) doesn't exist — expected without working QSPI flash chain.

---

## Phase 1B+: Platform Description Refactor + Upgrade
**Goal:** Separate chip-level and board-level platform descriptions; add missing on-chip peripherals identified via PR #583 comparison
**Status:** `next_up`
**Depends on:** Phase 1B ✅

### Background: Chip vs. Board Separation

The current `meadow-f7.repl` mixes on-chip STM32F777 peripherals with board-level external hardware (SDRAM, QSPI flash chip). Renode supports a clean `using` pattern to separate these:

- **`stm32f777.repl`** (chip) — CPU, internal flash, SRAM, all on-die peripherals
- **`meadow-f7.repl`** (board) — `using "stm32f777.repl"` + external SDRAM, QSPI flash chip, SD card, ESP32 coprocessor (future), LAN9355 PHY (future)

This makes the chip definition reusable and keeps board-specific hardware clearly separated.

### 1B+.1 — Refactor: Split into Chip + Board Files
- [ ] Create `platforms/stm32f777.repl` with all on-chip peripherals extracted from current `meadow-f7.repl`:
  - CPU (Cortex-M7), NVIC, internal flash (2MB @ 0x08000000), flash mirror (@ 0x00000000)
  - DTCM (128KB), SRAM1 (368KB), SRAM2 (16KB), ROM (64KB)
  - All UART/USART, SPI, I2C, CAN, timers, DMA, GPIO ports
  - RCC, PWR, SYSCFG, EXTI, RTC, RNG, OTG_FS, LTDC, Ethernet MAC, SDMMC controller
  - Flash registers, SVD reference
- [ ] Refactor `platforms/meadow-f7.repl` to use chip file + board-level externals:
  ```
  using "stm32f777.repl"

  // Board-level external peripherals
  sdram: Memory.MappedMemory @ sysbus 0xC0000000
      size: 0x02000000    // 32MB SDRAM (Meadow board)

  qspiFlash: Memory.MappedMemory @ sysbus 0x90000000
      size: 0x04000000    // 64MB W25Q flash (Meadow board)
  ```
  Note: QSPI flash size corrected from 32MB to 64MB to match actual Meadow board hardware.
- [ ] Verify all `.resc` scripts still work with the refactored files
- [ ] Update README.md project structure section

### 1B+.2 — Add Missing On-Chip Peripherals (to `stm32f777.repl`)

Based on comparison with Renode PR #583 and STM32F777 datasheet:

| Item | Peripheral | Address | IRQ | Notes |
|------|-----------|---------|-----|-------|
| 1 | `MTD.STM32F4_FlashController` | 0x40023C00 | — | Replace `flash_regs` MappedMemory stub with proper flash interface |
| 2 | `Miscellaneous.DWT` | 0xE0001000 | — | Data Watchpoint & Trace. Set `frequency: 216000000` (216MHz, not PR's incorrect 72MHz) |
| 3 | `Analog.STM32_ADC` x3 | ADC1: 0x40012000, ADC2: 0x40012100, ADC3: 0x40012200 | nvic@18 | All three share one NVIC line |
| 4 | `UART.STM32_UART` x4 | UART4: 0x40004C00, UART5: 0x40005000, UART7: 0x40007C00, UART8: 0x40007800 | 52, 53, 82, 83 | Additional UARTs not in original F746 file |
| 5 | `fpuInterruptNumber: 81` | — | 81 | Add to CPU definition |
| 6 | `CAN.STMCAN` x2 | CAN2: 0x40006800, CAN3: 0x40006C00 | 63, 104 | CAN1 already present. STM32F777 has 3 CAN buses |
| 7 | `SPI.STM32SPI` x3 | SPI4: 0x40013400, SPI5: 0x40015000, SPI6: 0x40015400 | 84, 85, 86 | Confirmed in NuttX chip.h and memory map. Meadow board uses SPI2, SPI3, SPI5 |

Deferred (not adding now):
- **Ethernet PHY** — Meadow uses LAN9355/ML, not LAN8742A. Add when networking phase begins.
- **CRC unit** — Hardware CRC not enabled in Meadow defconfig. Renode's `STM32F0_CRC` model is incorrect for F7 anyway.

### 1B+.3 — Board-Level Notes (for `meadow-f7.repl`)

Items that belong in the board file, tracked here for future phases:
- **QSPI flash chip** — W25QXXXJV (Winbond). Phase 1B.5 replaces MappedMemory stub with real C# peripheral.
- **SD card** — Physical card attached via SDMMC controller. Controller is on-chip, card is board-level. Add when needed.
- **ESP32 coprocessor** — `renode-esp32` track. Connected via SPI2 + GPIO handshaking.
- **Ethernet PHY** — LAN9355/ML. Add when networking phase begins.
- **SDRAM** — 32MB external SDRAM via FMC. Already in board file.

### Verification: Things We Got Right (vs. PR #583 Bugs)

Confirmed our implementations are correct where PR #583 has errors:
- ✅ **SRAM layout** — Proper 3-region split (DTCM/SRAM1/SRAM2) vs PR's incorrect 256MB single block
- ✅ **RCC** — Custom Python peripheral with ready bits vs PR's built-in model that hangs NuttX
- ✅ **GPIO reset values** — GPIOA 0xA8000000, GPIOB 0x00000280 vs PR's all-zeros (wrong for A/B debug pins)
- ✅ **GPIO→EXTI routing** — Through SYSCFG mux vs PR's incorrect direct connection
- ✅ **Timers** — Full 14-timer set vs PR's 5 timers
- ✅ **SDMMC** — Proper `STM32FSDMMC` peripheral vs PR's nothing
- ✅ **USB OTG FS** — Custom Python stub vs PR's nothing
- ✅ **SDRAM** — 32MB @ 0xC0000000 vs PR's nothing

---

## Phase 1B.5: QSPI Flash + Board Init Chain
**Goal:** Get `board_late_initialize()` completing fully — QSPI flash, filesystem, config, `/dev/nxupd` registration
**Status:** `next_up`
**Depends on:** Phase 1B+ (platform refactor — peripherals go into correct chip/board files)
**Blocks:** `renode-hcom` track (HCOM needs `/dev/nxupd` which is registered at end of this chain)

### Background: Why This Is Required

The `board_late_initialize()` chain in `stm32_boot.c` runs:
```
QSPI controller init → GPIO version detect → MTD flash init → filesystem → config → hcom_nx_setup_mgr → register /dev/nxupd
```

Currently this chain fails at step 2 (GPIO version pins all read high → `UNKNOWN` → returns early). Even if fixed, step 3 would fail because the QSPI flash is a MappedMemory stub that can't respond to flash commands. Without completing this chain, `/dev/nxupd` is never registered, and `hcom_main` fails to start.

### Architecture: C# Peripherals via Renode AdHoc Compiler

Renode supports runtime loading of C# peripherals via `include @path/to/Peripheral.cs` — no Renode rebuild required. The existing `STM32H7_QuadSPI` controller and `GenericSpiFlash` base class do most of the heavy lifting:

- **`STM32H7_QuadSPI`** — QSPI controller with identical register layout to STM32F7 (CR, DCR, SR, CCR, AR, DR). Reusable as-is.
- **`GenericSpiFlash`** — Base class handling all SPI NOR flash commands (JEDEC ID, read, write, erase, status registers, 4-byte addressing, quad I/O). Supports 50+ commands.
- **`Micron_MT25Q`** — Example subclass: just 30 lines setting manufacturer ID (`0x20`) and memory type (`0xBB`).

We need a `Winbond_W25QXXXJV` subclass (~30 lines) with Winbond's JEDEC IDs, plus a GPIO version detection fix.

### 1B.5.1 — Create Winbond W25QXXXJV Flash Peripheral
- [ ] Create `peripherals/Winbond_W25QXXXJV.cs` extending `GenericSpiFlash`
  - Manufacturer ID: `0xEF` (Winbond)
  - Memory Type: `0x40` (W25QXXXJVQ, QE=1 fixed)
  - Capacity auto-calculated by `GenericSpiFlash.GetCapacityCode()` from backing memory size (32MB → `0x19`)
  - Size range validation: 2MB–64MB
- [ ] Verify the NuttX W25QXXXJV driver commands are all in `GenericSpiFlash.Commands` enum:
  - `0x9F` ReadID ✅, `0x05` ReadStatusRegister ✅, `0x35` EnterQuadInputOutputMode ✅
  - `0x06` WriteEnable ✅, `0xEB` QuadInputOutputFastRead ✅, `0xEC` QuadInputOutputFastRead4byte ✅
  - `0x02` PageProgram ✅, `0x20` SubsectorErase4kb ✅, `0xD8` SectorErase ✅
  - `0xB7` Enter4byteAddressMode ✅, `0xE9` Exit4byteAddressMode ✅
  - `0x66`/`0x99` ResetEnable/ResetMemory ✅
- [ ] Note: `0x35` is `EnterQuadInputOutputMode` in GenericSpiFlash but NuttX uses it as "Read Status Register-2". Need to verify compatibility or override if needed.

### 1B.5.2 — Update Platform Description
- [ ] Add QSPI controller to `stm32f777.repl` (on-chip, reusing STM32H7 model at F7 address):
  ```
  quadspi: SPI.STM32H7_QuadSPI @ sysbus 0xA0001000
      -> nvic@92
  ```
- [ ] Add W25QXXXJV flash and backing memory to `meadow-f7.repl` (board-level):
  ```
  w25q: SPI.Winbond_W25QXXXJV @ quadspi
      underlyingMemory: qspiFlashMem

  qspiFlashMem: Memory.MappedMemory @ sysbus 0x90000000
      size: 0x4000000    // 64MB
  ```
- [ ] Remove old QSPI MappedMemory stub

### 1B.5.3 — Fix GPIO Board Version Detection
- [ ] Add IronPython hook at `meadow_hw_version_find_gpio_ver` return (0x08005666) to force R0=0x01 (CCMv2 GPIO pattern)
  - CCMv2 (0x01) skips QSPI flash chip ID query, directly sets version to `MEADOW_F7_HW_VERSION_NUMB_CCMV2` (3)
  - This selects the W25QXXXJV MTD driver path in `board_late_initialize()`
- [ ] Alternative: set GPIO pin states in Renode to match CCMv2 board (PC14=low, PG3=low, PC13=low)
- [ ] Address is binary-specific — add to the objdump re-scan procedure alongside SVC hooks

### 1B.5.4 — Update .resc Scripts
- [ ] Add `include @peripherals/Winbond_W25QXXXJV.cs` to all .resc scripts (before platform load)
- [ ] Add GPIO version hook to scripts that have the SVC PRIMASK hooks
- [ ] Test with `meadow-os.resc` (interactive) and `meadow-os-gdb.resc` (debugging)

### 1B.5.5 — Validate Full Boot Chain
- [ ] Verify `board_late_initialize()` completes:
  - QSPI init succeeds
  - Version detection returns CCMv2
  - W25QXXXJV MTD driver initializes
  - Filesystem init (LittleFS/FAT on flash partitions)
  - Config init
  - `hcom_nx_setup_mgr()` runs
  - `/dev/nxupd` registered
- [ ] Verify `hcom_main` opens `/dev/nxupd` successfully
- [ ] Check for any new failures further in the boot chain
- [ ] Monitor UART output for new error messages
- [ ] Test LittleFS mount, file create/read/write/delete
- [ ] Test FAT filesystem operations

### 1B.5.6 — Flash Persistence (Future)
- [ ] Investigate Renode `MappedMemory` file backing for persistent flash contents
- [ ] Pre-populate flash with valid filesystem image if needed for config/file operations
- [ ] Document flash partition layout (from NuttX defconfig / board code)

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

## Open Questions

### Resolved
1. ~~**Renode MPU support:**~~ ✅ Works. Protected mode boots without MPU-related faults. Kernel/user privilege transitions function correctly.
2. ~~**RCC/Clock tree:**~~ ✅ Custom Python peripheral required. Renode's built-in STM32F4_RCC does not set ready bits.
3. ~~**GDB register set:**~~ ✅ Verified — GDB server exposes standard Cortex-M registers, breakpoints/stepi/backtrace/memory reads all work.
4. ~~**HCOM `/dev/nxupd` dependency:**~~ ✅ HCOM works without the ESP32 coprocessor, but `/dev/nxupd` must still be registered (requires QSPI flash chain in `board_late_initialize`). Phase 1B.5 addresses this.

### Open
5. **SVC address automation:** Currently the 7 SVC hook addresses are hardcoded and must be manually re-scanned after kernel rebuild. Should automate this (e.g., script that parses objdump output and generates hook lines).
6. **`0x35` command conflict:** NuttX W25QXXXJV driver uses `0x35` as "Read Status Register-2" but `GenericSpiFlash` maps it to `EnterQuadInputOutputMode`. Need to verify if `GenericSpiFlash` handles this correctly or if we need to override behavior in the subclass.
7. **Flash partition layout:** What partitions does NuttX create on the QSPI flash? Need to understand if a blank flash (all 0xFF) is sufficient for first boot or if pre-populated filesystem images are needed.
