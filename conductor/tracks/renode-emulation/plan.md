# Plan: Renode Emulation for Meadow.OS

## Track ID: `renode-emulation`
## Status: `planning`
## Repo: `Meadow.OS.Emulator` (sibling to `Meadow.OS/`)

---

## Phase 1A: First Boot — NSH in Renode (FLAT mode)
**Goal:** Prove Renode can boot NuttX on the Meadow F7 platform
**Build config:** `nsh` (FLAT mode, no MPU, single binary)
**Binary:** `nuttx.bin` only

### 1A.1 — Environment Setup
- [ ] Install Renode (`brew install renode` or from https://renode.io/download)
- [ ] Verify installation: `renode --version`, `renode --console` (headless mode)
- [ ] Locate built-in STM32F746 platform: find `stm32f746.repl` in Renode's install directory
- [ ] Quick smoke test: run any built-in Renode STM32 demo to confirm setup works

### 1A.2 — Build NuttX with `nsh` Config
- [ ] Create `build-emulator.sh` in `Meadow.OS.Emulator/`
- [ ] Script should:
  - Reference `../Meadow.OS/nuttx/` for source
  - Configure with: `./tools/configure.sh stm32f777zit6-meadow:nsh`
  - Build: `make -j8`
  - Copy `nuttx.bin` to `Meadow.OS.Emulator/build/`
- [ ] Verify build succeeds and `nuttx.bin` is produced

### 1A.3 — Create Meadow F7 Platform Description
- [ ] Create `platforms/meadow-f7.repl` based on Renode's `stm32f746.repl`
- [ ] Modifications from stock F746:
  - Resize `fsmcBank1` (external SDRAM): 256MB → 32MB
  - Add QSPI flash stub: `qspiFlash: Memory.MappedMemory @ sysbus 0x90000000 { size: 0x2000000 }`
  - Update SVD reference to F7x7 if available
  - All other peripherals (USART, timers, GPIO, DMA, etc.) stay as-is — same addresses on F777

### 1A.4 — Create Renode Launch Script
- [ ] Create `scripts/meadow-nsh.resc`:
  ```
  using sysbus
  mach create "meadow"
  machine LoadPlatformDescription @platforms/meadow-f7.repl

  sysbus LoadBinary @build/nuttx.bin 0x08000000
  sysbus.cpu VectorTableOffset 0x08000000

  showAnalyzer usart1

  start
  ```
- [ ] Create `run.sh` wrapper for convenience:
  ```bash
  renode --console scripts/meadow-nsh.resc
  ```

### 1A.5 — Boot and Debug
- [ ] Run: `./run.sh`
- [ ] Observe USART1 output — expect NuttX boot messages or fault
- [ ] If fault: start GDB server (`machine StartGdbServer 3333`), attach, diagnose
- [ ] Common issues to expect and resolve:
  - **RCC/Clock init:** NuttX configures PLL from HSE. Renode's RCC stub (`STM32F4_RCC`) may not fully model this. May need to stub specific registers.
  - **Flash wait states:** `flash_regs` is a MappedMemory region — may need preset values
  - **DTCM/ITCM memory:** May need additional memory regions if NuttX references them
- [ ] Iterate until `nsh>` prompt appears on USART1
- **Depends on:** 1A.2, 1A.3, 1A.4

---

## Phase 1B: Protected Mode Boot
**Goal:** Boot full Meadow.OS with PROTECTED build (kernel + user binaries)
**Build config:** `meadow_os` (PROTECTED mode, MPU, two-pass build)
**Binaries:** `nuttx.bin` + `nuttx_user.bin` (or combined `Meadow.OS.bin`)
**Critical gate:** Renode MPU support

### 1B.1 — Investigate Renode MPU Support
- [ ] Research Renode's Cortex-M7 MPU implementation (docs, source, community)
- [ ] Test: write a minimal MPU configuration program, run in Renode, verify behavior
- [ ] Determine if MPU region configuration works (needed for user-space GPIO/QSPI access)
- [ ] If MPU not supported: evaluate workarounds or contribute upstream
- **This is the critical gate for Phase 1B. If blocked, document and escalate.**

### 1B.2 — Build Meadow.OS (Protected Mode)
- [ ] Update `build-emulator.sh` to support `--config=meadow_os`
- [ ] Script should:
  - Configure with: `./tools/configure.sh stm32f777zit6-meadow:meadow_os`
  - Two-pass build: `make pass2` → `build-mbedtls.sh` → `make pass1` → `make nuttx`
  - Copy `nuttx.bin` and `nuttx_user.bin` to `build/`
  - Optionally combine into `Meadow.OS.bin` (matching `build.sh` packaging logic)
- [ ] Verify build succeeds

### 1B.3 — Load Both Binaries in Renode
- [ ] Create `scripts/meadow-os.resc`:
  ```
  using sysbus
  mach create "meadow"
  machine LoadPlatformDescription @platforms/meadow-f7.repl

  # Load kernel at 0x08000000
  sysbus LoadBinary @build/nuttx.bin 0x08000000
  # Load user binary at 0x08100000
  sysbus LoadBinary @build/nuttx_user.bin 0x08100000

  sysbus.cpu VectorTableOffset 0x08000000

  showAnalyzer usart1

  start
  ```
- [ ] Alternatively, load combined `Meadow.OS.bin` at 0x08000000

### 1B.4 — Validate Protected Mode Boot
- [ ] Verify kernel boots and transitions to user-space `hcom_main`
- [ ] Confirm MPU regions are configured (GPIO, QSPI, Ethernet SRAM access)
- [ ] Check memory isolation: kernel SRAM (0x20020000, 128KB) vs user SRAM (0x20040000, 240KB)
- [ ] Debug any faults related to MPU/privilege level transitions
- **Depends on:** 1B.1, 1B.2, 1B.3

---

## Phase 1C: HCOM Bring-Up
**Goal:** Get HCOM protocol working over emulated serial
**Depends on:** Phase 1B (PROTECTED mode boot with `hcom_main`)

### 1C.1 — HCOM Serial Configuration
- [ ] Wire second UART for HCOM (add UART4 or UART5 to `meadow-f7.repl` if not present)
- [ ] Expose HCOM UART via Renode socket: `emulation CreateServerSocketTerminal 1234 "hcom"`
- [ ] Connect HCOM UART to socket terminal in `.resc` script
- [ ] USART1 stays as console/debug output

### 1C.2 — HCOM Protocol Testing
- [ ] Verify `hcom_main` initializes and listens on the HCOM serial port
- [ ] Send basic HCOM commands via socket (e.g., using `nc` or a test script)
- [ ] Verify command/response cycle
- [ ] Test file transfer operations (requires QSPI flash working — Phase 1A.3 stub may suffice)

### 1C.3 — GDB Debugging Workflow
- [ ] Start Renode with GDB server: `machine StartGdbServer 3333`
- [ ] Connect: `arm-none-eabi-gdb ../Meadow.OS/nuttx/nuttx -ex "target remote :3333"`
- [ ] Verify: breakpoints, step, backtrace, register inspection
- [ ] Test NuttX GDB Python scripts (`../Meadow.OS/gdb/Nuttx.py`)
  - Renode's Cortex-M7 GDB should expose OpenOCD-style registers (xPSR, msp, psp)
  - Renode behaves like real hardware from GDB's perspective
- [ ] Document the debugging workflow

---

## Phase 2: NuttX Simulator (Complementary)
**Goal:** Fast, lightweight OS-level testing without ARM compilation
**Priority:** Secondary — useful for app-layer and protocol-level testing

### 2.1 — Build NuttX sim target
- [ ] Verify `sim` architecture exists in rebased NuttX: `ls ../Meadow.OS/nuttx/boards/sim/`
- [ ] Build stock config: `cd ../Meadow.OS/nuttx && ./tools/configure.sh sim:nsh && make`
- [ ] Run: `./nuttx` → should drop into NSH shell on host terminal
- [ ] Document any build issues with the rebased NuttX

### 2.2 — Evaluate Meadow sim configuration
- [ ] Assess feasibility: can HCOM code compile for sim target? (likely needs significant ifdef work)
- [ ] If feasible: create sim defconfig with HCOM, LittleFS, FAT, crypto, ELF
- [ ] Set `CONFIG_BUILD_FLAT=y` (no MPU in sim)
- [ ] Adapt `CONFIG_INIT_ENTRYPOINT` if needed
- [ ] If not feasible: document blockers, use sim only for kernel/OS-level testing

---

## Phase 3: CI Integration
**Goal:** Automated emulated testing in CI pipeline
**Priority:** After Phase 1B is stable

### 3.1 — Create test runner script
- [ ] Create `scripts/run-tests.sh`
- [ ] Support `--backend=renode` and optionally `--backend=sim`
- [ ] Renode backend: run `.resc` script in headless mode, capture UART output, detect test pass/fail
- [ ] Implement timeout and exit-code reporting

### 3.2 — Renode Robot Framework tests (optional)
- [ ] Explore Renode's Robot Framework integration for structured test cases
- [ ] Create `.robot` test files for key boot/peripheral scenarios
- [ ] Integrate with CI reporting

### 3.3 — Pipeline integration
- [ ] Add emulation test step to CI (Azure Pipelines per existing `azure-pipelines.yml` in Meadow.OS)
- [ ] Install Renode in CI environment (Linux container)
- [ ] Build Meadow.OS → run in Renode → validate boot + tests → report
- [ ] Gate merges on emulated test pass (once stable)

---

## Phase 4: Peripheral Parity (Ongoing)
**Goal:** Incrementally improve emulation fidelity
**Priority:** As-needed, driven by testing requirements

### 4.1 — QSPI Flash
- [ ] Replace MappedMemory stub with proper SPI flash model (if Renode supports W25QXXXJV)
- [ ] Add persistent backing file for flash contents
- [ ] Test LittleFS mount, file create/read/write/delete
- [ ] Test FAT filesystem operations

### 4.2 — Additional Peripherals
- [ ] **Timers:** Verify PWM output and timer interrupt generation
- [ ] **GPIO:** Test basic pin read/write
- [ ] **I2C/SPI:** Basic bus communication (may need virtual devices attached)
- [ ] **SDMMC:** Test SD card emulation
- [ ] **RTC:** Verify time keeping
- [ ] Document which peripherals work, which need stubs, which are blocked

---

## File Structure
```
Meadow.OS.Emulator/
├── platforms/
│   └── meadow-f7.repl              # Renode platform description
├── scripts/
│   ├── meadow-nsh.resc             # NSH boot (FLAT mode)
│   ├── meadow-os.resc              # Full Meadow.OS boot (PROTECTED mode)
│   ├── meadow-os-tests.resc        # Test automation
│   └── run-tests.sh                # CI test runner
├── build/                           # Build artifacts (gitignored)
│   ├── nuttx.bin
│   ├── nuttx_user.bin
│   └── Meadow.OS.bin
├── build-emulator.sh                # Build script (calls into ../Meadow.OS/)
├── run.sh                           # Quick-launch wrapper
├── conductor/                       # Planning docs
│   ├── product.md
│   ├── tech-stack.md
│   ├── tracks.md
│   └── tracks/renode-emulation/
│       ├── spec.md
│       ├── plan.md
│       └── metadata.json
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
| 2026-03-08 | NuttX sim as complement only | Useful for OS-logic testing but can't validate ARM code, peripherals, or PROTECTED mode |

## Open Questions
1. **Renode MPU support:** Does Renode's Cortex-M7 model fully support MPU configuration? Critical gate for Phase 1B.
2. **RCC/Clock tree:** How much of the STM32F7 clock tree does Renode model? NuttX's `stm32_clockconfig()` configures PLL from HSE — may need stubs.
3. **HCOM dual-serial:** Can Renode expose two UART instances (console + HCOM) to the host simultaneously? (likely yes — multiple analyzers/sockets supported)
4. **GDB register set:** Renode's Cortex-M7 GDB server likely exposes OpenOCD-style registers (xPSR, msp, psp, control). Need to verify.
