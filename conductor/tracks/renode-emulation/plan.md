# Plan: Renode Emulation for Meadow.OS

## Track ID: `renode-emulation`
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
HCOM fails because `/dev/nxupd` (coprocessor communication device) doesn't exist — expected without ESP32 coprocessor emulation.

---

## Phase 1B.5: QSPI Flash + Board Init Chain
**Goal:** Get `board_late_initialize()` completing fully — QSPI flash, filesystem, config, `/dev/nxupd` registration
**Status:** `next_up`
**Depends on:** Phase 1B ✅
**Blocks:** Phase 1C (HCOM needs `/dev/nxupd` which is registered at end of this chain)

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
- [ ] Remove old QSPI MappedMemory stub from `meadow-f7.repl`:
  ```
  // REMOVE: qspiFlash: Memory.MappedMemory @ sysbus 0x90000000
  ```
- [ ] Add QSPI controller (reusing STM32H7 model at F7 address):
  ```
  quadspi: SPI.STM32H7_QuadSPI @ sysbus 0xA0001000
      -> nvic@92
  ```
- [ ] Add W25QXXXJV flash connected to controller:
  ```
  w25q: SPI.Winbond_W25QXXXJV @ quadspi
      underlyingMemory: qspiFlashMem
  ```
- [ ] Add backing memory for flash (32MB):
  ```
  qspiFlashMem: Memory.MappedMemory @ sysbus 0x90000000
      size: 0x2000000
  ```

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

### 1B.5.6 — Flash Persistence (Future)
- [ ] Investigate Renode `MappedMemory` file backing for persistent flash contents
- [ ] Pre-populate flash with valid filesystem image if needed for config/file operations
- [ ] Document flash partition layout (from NuttX defconfig / board code)

---

## Phase 1C: HCOM Bring-Up
**Goal:** Get HCOM protocol working over emulated serial
**Status:** `blocked` (waiting on Phase 1B.5)
**Depends on:** Phase 1B.5 (QSPI flash / `/dev/nxupd` registration)

### Current Situation
HCOM initialization requires:
1. **`/dev/nxupd`** — Registered by kernel during `board_late_initialize()` → requires working QSPI flash chain (Phase 1B.5). HCOM is designed to work without the ESP32 coprocessor, but the device must exist.
2. **USB CDC-ACM** — HCOM's host communication transport (`/dev/ttyACM0`). Renode can't emulate USB device controllers. Need UART redirect.

### 1C.1 — Update Emulation Defconfig (`meadow_os_emu`)
- [ ] Disable USB CDC-ACM: `CONFIG_CDCACM=n`, `CONFIG_USBDEV=n`
- [ ] Redirect HCOM to UART: `CONFIG_HCOM_COMMS_DEVICE_NAME="/dev/ttyS1"` (USART2)
- [ ] Enable USART2: `CONFIG_STM32F7_USART2=y`, add USART2 serial driver configs
- [ ] Add USART2 pin definitions to board.h if needed (PA2/PA3 or PD5/PD6)
- [ ] Keep all other PROTECTED mode settings identical to `meadow_os`
- [ ] Verify build succeeds

### 1C.2 — HCOM Serial Configuration in Renode
- [ ] Expose HCOM UART via Renode socket: `emulation CreateServerSocketTerminal 1234 "hcom"`
- [ ] Connect USART2 to socket terminal in `.resc` script
- [ ] USART1 stays as console/syslog output

### 1C.3 — HCOM Protocol Testing
- [ ] Verify `hcom_main` initializes and listens on the redirected UART
- [ ] Send basic HCOM commands via socket (e.g., using `nc` or a test script)
- [ ] Verify command/response cycle
- [ ] Test file transfer operations (uses QSPI flash from Phase 1B.5)

### 1C.4 — GDB Debugging Workflow ✅ COMPLETE
- [x] GDB server works via `machine StartGdbServer 3333` (meadow-os-gdb.resc)
- [x] `arm-none-eabi-gdb build/nuttx.elf -ex "target remote :3333"` connects successfully
- [x] Breakpoints, stepi, backtrace, register inspection all work
- [x] Memory reads (`x/Nxw addr`) work correctly
- [x] NuttX data structures readable (e.g., `(struct tcb_s *)g_readytorun.head`)
- [x] UART output captured to `/tmp/renode-uart.txt` for monitoring
- [x] Documented in README.md

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

## Phase 4: ESP32 Coprocessor Emulation
**Goal:** Emulate the ESP32 coprocessor so Meadow.OS has networking (WiFi/BLE)
**Priority:** Required for full Meadow.OS functionality
**Context:** The Meadow F7 board has two chips — STM32F777 (main MCU) and ESP32 (coprocessor). They communicate via SPI2 with GPIO handshaking. The ESP32 provides WiFi, BLE, mesh networking, NTP, and file system services through the ESPCP protocol.

### Background: ESPCP Communication Architecture

**Physical connections (on-board ESP32):**
- **SPI2** @ 0x40003800 — primary data bus
- **CS:** PI0 (active-low chip select)
- **Ready:** PI10 (ESP32 → STM32, goes high when ESP32 is ready to receive)
- **Reset:** PF7 (STM32 → ESP32, open-drain output)
- **Boot:** PI10 (shared with Ready; STM32 → ESP32, open-drain)
- **UART5** — secondary channel for ESP32 debug/monitor output

**ESPCP Protocol (v1):**
- **Header:** 27 bytes — protocol version (1B), CRC8 (1B), message_type (1B), interface (1B), function (4B), status_code (4B), message_id (4B), packet_offset (2B), packet_length (2B), payload_length (4B), CRC32 (4B)
- **Max SPI frame:** 4092 bytes (ESP32 silicon bug limits this)
- **SPI overhead:** 4 bytes padding (last 4 bytes discarded — ESP32 silicon bug workaround)
- **Max packet payload:** 4061 bytes (4092 - 27 header - 4 overhead)
- **Max message payload:** 8192 bytes (larger payloads split across multiple packets)
- **Message direction:** ID < 0x80000000 = ESP32→STM32; ID >= 0x80000000 = STM32→ESP32

**Message types:**
- `0x00` ACK, `0x01` NAK, `0x02` Reset, `0x04` Event
- `0x10` Response, `0x20` Transport, `0x40` Header, `0x80` Data

**Interfaces (uint8):**
- `0` None, `1` WiFi, `2` Bluetooth, `3` Mesh, `4` System, `5` Transport, `6` Wired Ethernet

**Key functions per interface:**
- **System (4):** get_configuration, set_configuration_item, deep_sleep, get_battery_charge_level, file_system_*, logging_*, core_dump_*
- **WiFi (1):** start/stop interface, connect/disconnect AP, scan, set_antenna, full socket API (socket, connect, bind, listen, accept, send, recv, poll, close, getaddrinfo, setsockopt, ioctl)
- **Bluetooth (2):** start, stop, get_handles, server_data_set, client_write_request_event
- **Transport (5):** response_ready, send_response, kill_nuttx_thread, reset_esp32

**STM32 threading model:**
- ESPCP main thread — processes outgoing message queue
- Event handler thread — processes incoming events from ESP32
- UART monitor thread — captures ESP32 debug output via UART5

**Startup sequence:**
1. `espcp_init()` — create threads, message queues, buffers
2. `espcp_enter_run_mode()` — configure GPIO, reset ESP32
3. Wait for SPI Ready line (PI10) to go high
4. Begin SPI request-response communication

**Key source files:**
- `Meadow.OS/espcp/espcp_coprocessor.c` — SPI communication, startup, message send/receive
- `Meadow.OS/espcp/espcp_message.h` — header structure, constants
- `Meadow.OS/espcp/espcp_shared_enums.h` — all interface/function/status enums
- `Meadow.OS/espcp/espcp_thread.c` — thread management
- `Meadow.OS/espcp/doc/coprocessor.md` — architecture documentation

**Build flag:** `CONFIG_MEADOW_ESPCP_MANAGER` (currently `not set` in defconfig — safe for Phase 1A-1C)

---

### Phase 4A: ESPCP SPI Stub Peripheral (C# in Renode)
**Goal:** Create a Renode peripheral that acts as a virtual ESP32, responding to ESPCP messages on SPI2
**Approach:** C# peripheral (not Python — Python peripherals only handle simple register reads/writes; we need the `ISPISlave` interface for proper SPI frame handling)

#### 4A.1 — Stub Architecture Design
The stub peripheral implements Renode's `ISPISlave` interface and sits on the STM32's SPI2 bus. It:
1. Receives SPI frames from the STM32 ESPCP driver
2. Parses the 27-byte ESPCP header
3. Routes to interface-specific handlers based on `interface` + `function` fields
4. Constructs response frames with appropriate headers and CRC
5. Manages the Ready GPIO line to signal readiness

```
┌─────────────────────────────────────────────────┐
│ STM32F7 (Renode Machine)                        │
│                                                 │
│  ┌──────────┐    SPI2     ┌──────────────────┐  │
│  │ ESPCP    │◄──────────►│ EspcpStub        │  │
│  │ Driver   │            │ (C# peripheral)  │  │
│  └──────────┘            │                  │  │
│       │                  │  Handles:        │  │
│       │ GPIO             │  - System info   │  │
│  PI10 (Ready) ◄──────── │  - WiFi stubs    │  │
│  PF7  (Reset) ────────► │  - Socket API    │  │
│  PI0  (CS)    ────────► │  - BLE stubs     │  │
│                          └──────────────────┘  │
└─────────────────────────────────────────────────┘
```

#### 4A.2 — Create ESPCP Stub C# Peripheral
- [ ] Create `peripherals/EspcpStub.cs` implementing `ISPISlave`
- [ ] Implement SPI frame receive/transmit (4092-byte frames)
- [ ] Parse 27-byte ESPCP message header:
  - Validate CRC8 (header) and CRC32 (payload)
  - Extract interface, function, message_id, payload
- [ ] Construct response frames:
  - Set message_type = `0x10` (Response)
  - Set status_code = `espcp_status_codes_completed_ok` (0)
  - Calculate CRC8/CRC32
  - Add 4-byte SPI overhead padding
- [ ] Handle multi-packet messages (payload > 4061 bytes spans multiple SPI frames)

#### 4A.3 — Implement Interface Handlers
**Minimum viable set for boot + basic networking:**

**System interface (4) — Required for boot:**
- [ ] `get_configuration (0)` → return default config (device name, antenna, MAC addresses)
- [ ] `set_configuration_item (1)` → ACK, store in memory
- [ ] `logging_configuration (13)` → ACK
- [ ] `get_battery_charge_level (3)` → return mock value (e.g., 100%)

**Transport interface (5) — Required for handshake:**
- [ ] `response_ready (0)` → signal that stub is ready
- [ ] `reset_esp32 (3)` → ACK, re-initialize stub state

**WiFi interface (1) — Required for networking:**
- [ ] `start_wi_fi_interface (0)` → return success, fire `start_wi_fi_interface_event (34)`
- [ ] `stop_wi_fi_interface (1)` → return success
- [ ] `connect_to_default_access_point (3)` → return success, fire `network_connected_event (36)` with mock IP
- [ ] `get_access_points (6)` → return empty list or mock AP
- [ ] `socket (8)` → return mock file descriptor (incrementing int)
- [ ] `connect (9)` → return success
- [ ] `write (10)` / `send (19)` → ACK, discard data (or log it)
- [ ] `read (13)` / `recv_from (16)` → return empty or mock data
- [ ] `close (14)` → return success
- [ ] `poll (17)` → return no events ready
- [ ] `bind (20)`, `listen (21)`, `accept (22)` → return success/mock FD
- [ ] `set_sock_opt (11)` / `get_sock_opt (12)` → ACK/return defaults
- [ ] `get_addr_info (27)` → return mock address resolution

**Bluetooth interface (2) — Stub only:**
- [ ] `start (0)` → return success
- [ ] `stop (1)` → return success

#### 4A.4 — Wire Stub into Platform
- [ ] Add to `meadow-f7.repl`:
  ```
  espcpStub: SPI.EspcpStub @ spi2
      -> gpioPortI@10  // Ready signal to PI10
  ```
- [ ] Handle GPIO connections in `.resc`:
  ```
  # Wire ESP32 reset line
  gpioPortF.7 -> espcpStub@0  // Reset from PF7
  # Wire CS line
  gpioPortI.0 -> espcpStub@1  // CS from PI0
  ```
- [ ] Update `build-emulator.sh` to set `CONFIG_MEADOW_ESPCP_MANAGER=y`

#### 4A.5 — Test ESPCP Stub
- [ ] Verify ESPCP init sequence: reset ESP32 → wait for Ready → send first message
- [ ] Confirm system config exchange works
- [ ] Test WiFi start → connect → socket create → send/recv cycle
- [ ] Verify no timeouts or deadlocks in ESPCP message queues
- [ ] Test graceful handling of unsupported functions (return `espcp_status_codes_not_implemented`)
- [ ] Add Renode logging for all ESPCP messages (useful for debugging)

#### 4A.6 — Network Bridge to Host (Stretch)
- [ ] Investigate bridging mock socket calls to real host networking
- [ ] If viable: `socket()` in stub creates a real host socket, `send()`/`recv()` proxy to it
- [ ] This would allow Meadow.OS to make real network connections from emulation
- [ ] Requires Renode host integration (may need C# peripheral with System.Net.Sockets)

---

### Phase 4B: Dual-Machine Emulation (Full Fidelity)
**Goal:** Run both STM32F7 and ESP32 as separate Renode machines connected via virtual SPI
**Priority:** Future — for testing real ESP32 firmware and full inter-chip protocol

#### Background: Renode Multi-Machine Capabilities
Renode supports multiple machines in a single simulation with inter-machine connections:
- **UART:** `emulation CreateUARTHub "hub"` + `connector Connect` — buffered, deterministic
- **GPIO:** `emulation CreateGPIOConnector "gpio-con"` — unidirectional boolean signals (max 2 machines per connector; bidirectional needs 2 connectors)
- **SPI:** No built-in SPI hub/connector documented — may need custom bridge peripheral or C# connector
- **Xtensa CPU:** Renode has basic Xtensa support (`xtensa-sample-controller.repl`) but no full ESP32 platform

#### 4B.1 — Investigate Renode SPI Inter-Machine Support
- [ ] Check if Renode has an SPI equivalent of `CreateUARTHub`
- [ ] If not: design a C# bridge peripheral that sits on both machines' SPI buses
  - Machine A (STM32): SPI master sends frame → bridge captures
  - Bridge forwards frame to Machine B (ESP32): SPI slave receives
  - ESP32 processes, responds → bridge forwards back to STM32
- [ ] Evaluate timing/synchronization: SPI is synchronous, Renode time domains may need coordination
- [ ] Check Renode community/issues for prior art on SPI multi-machine setups

#### 4B.2 — Create ESP32 Platform Description
- [ ] Create `platforms/meadow-esp32.repl` based on Renode's `xtensa-sample-controller.repl`
- [ ] Adapt memory map for ESP32 (or ESP32-S3 if that's what Meadow uses):
  - IRAM: 0x40000000
  - DRAM: 0x3FFE0000 / 0x3FFC0000
  - Flash: memory-mapped region
  - SPI slave peripheral (ESPCP receiver side)
- [ ] Add UART for debug output (connects to UART5 on STM32 side via UARTHub)
- [ ] CPU: Xtensa with ESP32-appropriate configuration

#### 4B.3 — Build ESP32 Firmware for Renode
- [ ] Review `../Meadow.OS/esp32/` build system (ESP-IDF + CMake)
- [ ] Determine if ESP32 firmware can be built for Renode's Xtensa model
- [ ] Key challenge: ESP-IDF relies on hardware-specific peripherals (WiFi radio, etc.) that Renode won't have
- [ ] May need to build a stripped-down ESP32 firmware that only runs the ESPCP SPI slave + mock WiFi responses
- [ ] Alternative: use the stub approach (Phase 4A) for WiFi/BLE, only emulate the ESPCP transport layer in ESP32 firmware

#### 4B.4 — Wire Machines Together
- [ ] Create `scripts/meadow-full.resc`:
  ```
  # Create STM32F7 machine
  mach create "stm32"
  machine LoadPlatformDescription @platforms/meadow-f7.repl
  sysbus LoadBinary @build/nuttx.bin 0x08000000
  sysbus LoadBinary @build/nuttx_user.bin 0x08100000

  # Create ESP32 machine
  mach create "esp32"
  machine LoadPlatformDescription @platforms/meadow-esp32.repl
  sysbus LoadBinary @build/esp32/meadow-esp32.bin 0x40000000

  # Connect UART (ESP32 debug → STM32 UART5 monitor)
  emulation CreateUARTHub "espUartHub"
  mach set "stm32"
  connector Connect sysbus.uart5 espUartHub
  mach set "esp32"
  connector Connect sysbus.uart espUartHub

  # Connect GPIO: Reset line (STM32 PF7 → ESP32 reset)
  emulation CreateGPIOConnector "resetLine"
  mach set "stm32"
  connector Connect sysbus.gpioPortF resetLine
  resetLine SelectSourcePin sysbus.gpioPortF 7
  mach set "esp32"
  connector Connect sysbus.resetController resetLine
  resetLine SelectDestinationPin sysbus.resetController 0

  # Connect GPIO: Ready line (ESP32 → STM32 PI10)
  emulation CreateGPIOConnector "readyLine"
  mach set "esp32"
  connector Connect sysbus.readyGpio readyLine
  readyLine SelectSourcePin sysbus.readyGpio 0
  mach set "stm32"
  connector Connect sysbus.gpioPortI readyLine
  readyLine SelectDestinationPin sysbus.gpioPortI 10

  # Connect SPI (requires custom bridge — see 4B.1)
  # ... TBD based on SPI bridging investigation

  # Start both machines
  mach set "stm32"
  showAnalyzer usart1
  mach set "esp32"
  showAnalyzer uart

  start
  ```

#### 4B.5 — Validate End-to-End Communication
- [ ] Verify startup: STM32 resets ESP32 → ESP32 boots → signals Ready → SPI handshake
- [ ] Test ESPCP message round-trip: STM32 request → SPI → ESP32 processes → SPI → STM32 receives
- [ ] Compare message traces with real hardware captures (if available)
- [ ] Test WiFi init sequence end-to-end
- [ ] Measure SPI timing accuracy and identify any synchronization issues

#### 4B.6 — Network Passthrough (Stretch)
- [ ] If ESP32 machine has mock WiFi: bridge to host networking via Renode's TAP/virtual network
- [ ] Or: implement network passthrough at the ESPCP socket API level (same as 4A.6)

---

### Phase 4 Decision Matrix

| Capability | Phase 4A (Stub) | Phase 4B (Dual-Machine) |
|-----------|----------------|------------------------|
| ESPCP boot handshake | ✅ Emulated in C# | ✅ Real ESP32 firmware |
| WiFi connect/scan | ✅ Mock responses | ✅ Real (if ESP32 WiFi modeled) |
| Socket API | ✅ Mock FDs, no real network | ✅ Real ESP32 stack |
| BLE | ✅ Stub start/stop | ✅ Real (if BLE modeled) |
| Test STM32 ESPCP code | ✅ Full coverage | ✅ Full coverage |
| Test ESP32 firmware | ❌ Not running | ✅ Real firmware |
| Setup complexity | Low-Medium | High |
| Real network access | Possible via host bridge | Possible via Renode TAP |
| Recommended for | Development, CI testing | Integration testing, firmware validation |

---

## Phase 5: Peripheral Parity (Ongoing)
**Goal:** Incrementally improve emulation fidelity
**Priority:** As-needed, driven by testing requirements

### 5.1 — QSPI Flash
- [ ] Replace MappedMemory stub with proper SPI flash model (if Renode supports W25QXXXJV)
- [ ] Add persistent backing file for flash contents
- [ ] Test LittleFS mount, file create/read/write/delete
- [ ] Test FAT filesystem operations

### 5.2 — Additional Peripherals
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
│   ├── meadow-f7.repl              # STM32F7 Renode platform description
│   └── meadow-esp32.repl           # ESP32 coprocessor platform (Phase 4)
├── peripherals/
│   └── EspcpStub.cs                 # ESPCP SPI stub peripheral (C#, Phase 4)
├── scripts/
│   ├── meadow-nsh.resc             # NSH boot (FLAT mode)
│   ├── meadow-os.resc              # Full Meadow.OS boot (PROTECTED mode)
│   ├── meadow-full.resc            # Dual-machine: STM32 + ESP32 (Phase 4)
│   ├── meadow-os-tests.resc        # Test automation
│   └── run-tests.sh                # CI test runner
├── build/                           # Build artifacts (gitignored)
│   ├── nuttx.bin
│   ├── nuttx_user.bin
│   ├── Meadow.OS.bin
│   └── esp32/                       # ESP32 firmware (Phase 4)
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
| 2026-03-08 | ESP32 coprocessor: stub first, dual-machine later | ESPCP SPI stub unblocks networking tests without needing ESP32 firmware. Dual-machine emulation is a future goal for full-fidelity testing |
| 2026-03-08 | ESPCP disabled for Phase 1A-1C | `CONFIG_MEADOW_ESPCP_MANAGER` is already `not set` in defconfig — safe to boot without ESP32 for initial bring-up |
| 2026-03-08 | Skip bootloader in Renode, load at link-time addresses | Bootloader places kernel at 0x08040000 in combined image, but kernel is linked at 0x08000000. Loading individual binaries at link addresses avoids address mismatch. |
| 2026-03-08 | HCOM needs emulation-specific defconfig | HCOM uses USB CDC-ACM (`/dev/ttyACM0`) which Renode can't emulate. Create `meadow_os_emu` defconfig that redirects HCOM to a UART. |
| 2026-03-08 | Custom RCC Python peripheral | Renode's built-in STM32F4_RCC does not set clock ready bits. Custom `rcc.py` handles HSIRDY/HSERDY/PLLRDY/etc. and SW→SWS mirroring. |
| 2026-03-08 | SVC PRIMASK workaround via inline hooks | Renode silently skips SVC when PRIMASK=1. IronPython hooks at all 7 SVC addresses clear PRIMASK before execution. Hooks are address-specific and must be updated when kernel is rebuilt. |
| 2026-03-08 | SVC hooks inline in .resc, not PyDevFromFile | Inline one-liner is simpler than a separate Python file for CPU hooks. `fix_svc_primask.py` kept as reference. |
| 2026-03-08 | C# peripheral for QSPI flash (not Python) | Renode's `GenericSpiFlash` base class handles all SPI NOR flash commands. Creating a `Winbond_W25QXXXJV` subclass (~30 lines of C#) gives us full flash emulation. Renode's AdHoc Compiler loads C# at runtime via `include @file.cs` — no Renode rebuild needed. |
| 2026-03-08 | Reuse `STM32H7_QuadSPI` controller for F7 | STM32F7 and H7 QUADSPI have identical register layouts (CR, DCR, SR, CCR, AR, DR). Only difference is base address (F7: 0xA0001000, H7: 0x52005000), handled in .repl. |
| 2026-03-08 | `/dev/nxupd` requires full QSPI/MTD chain | `board_late_initialize()` gates `hcom_nx_setup_mgr()` (which registers `/dev/nxupd`) behind successful QSPI flash init, version detection, MTD driver init, and filesystem setup. Cannot shortcut — need working QSPI flash. |
| 2026-03-08 | GPIO version detection hook for CCMv2 | Board version is detected via 4 GPIO pins. In Renode, unconnected inputs with pull-ups all read high (0x0F = F7V1/V2), triggering a flash chip ID query that fails. Hook to return 0x01 (CCMv2) skips the query and selects W25QXXXJV driver directly. |

## Open Questions

### Resolved
1. ~~**Renode MPU support:**~~ ✅ Works. Protected mode boots without MPU-related faults. Kernel/user privilege transitions function correctly.
2. ~~**RCC/Clock tree:**~~ ✅ Custom Python peripheral required. Renode's built-in STM32F4_RCC does not set ready bits.
3. ~~**HCOM dual-serial:**~~ ✅ HCOM uses USB CDC-ACM, not UART. Solution: emulation-specific defconfig redirecting HCOM to a UART.
4. ~~**GDB register set:**~~ ✅ Verified — GDB server exposes standard Cortex-M registers, breakpoints/stepi/backtrace/memory reads all work.

### Open
5. **Renode ESP32 support:** Does Renode have an ESP32/Xtensa platform? If so, dual-machine emulation becomes much more viable.
6. **ESPCP stub fidelity:** How much of the ESPCP protocol needs to be stubbed for Meadow.OS to boot with networking enabled? Minimum viable: system status + WiFi init responses.
7. ~~**HCOM `/dev/nxupd` dependency:**~~ ✅ HCOM works without the ESP32 coprocessor, but `/dev/nxupd` must still be registered (requires QSPI flash chain in `board_late_initialize`). Phase 1B.5 addresses this.
8. **SVC address automation:** Currently the 7 SVC hook addresses are hardcoded and must be manually re-scanned after kernel rebuild. Should automate this (e.g., script that parses objdump output and generates hook lines).
9. **`0x35` command conflict:** NuttX W25QXXXJV driver uses `0x35` as "Read Status Register-2" but `GenericSpiFlash` maps it to `EnterQuadInputOutputMode`. Need to verify if `GenericSpiFlash` handles this correctly or if we need to override behavior in the subclass.
10. **Flash partition layout:** What partitions does NuttX create on the QSPI flash? Need to understand if a blank flash (all 0xFF) is sufficient for first boot or if pre-populated filesystem images are needed.
