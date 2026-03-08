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
  - **Fix memory regions** — stock F746 has a single 384KB `sram` block. Replace with accurate STM32F777 layout:
    - `dtcm: Memory.MappedMemory @ sysbus 0x20000000 { size: 0x20000 }` (128KB DTCM)
    - `sram1: Memory.MappedMemory @ sysbus 0x20020000 { size: 0x5C000 }` (368KB SRAM1)
    - `sram2: Memory.MappedMemory @ sysbus 0x2007C000 { size: 0x4000 }` (16KB SRAM2)
    - Note: NSH flat config uses RAM starting at 0x20010000 (mid-DTCM), so DTCM must be present
  - Resize `fsmcBank1` (external SDRAM): 256MB → 32MB (matches CONFIG_HEAP2_SIZE=33554432)
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

  # Load kernel at link-time address (0x08000000)
  # NOTE: The production bootloader places kernel at 0x08040000 in combined
  # Meadow.OS.bin, but the kernel is LINKED at 0x08000000. In Renode we skip
  # the bootloader and load each binary at its link-time address directly.
  sysbus LoadBinary @build/nuttx.bin 0x08000000
  # Load user binary at 0x08100000 (CONFIG_NUTTX_USERSPACE)
  sysbus LoadBinary @build/nuttx_user.bin 0x08100000

  sysbus.cpu VectorTableOffset 0x08000000

  showAnalyzer usart1

  start
  ```
- [ ] Do NOT use the combined `Meadow.OS.bin` — it includes the bootloader and places the kernel at offset 0x40000 (0x08040000), which doesn't match link-time addresses. Always load the individual `nuttx.bin` and `nuttx_user.bin` directly.

### 1B.4 — Validate Protected Mode Boot
- [ ] Verify kernel boots and transitions to user-space `hcom_main`
- [ ] Confirm MPU regions are configured (GPIO, QSPI, Ethernet SRAM access)
- [ ] Check memory isolation: kernel SRAM (0x20020000, 128KB) vs user SRAM (0x20040000, 240KB per memory.ld)
  - Note: SDRAM at 0xC0000000 is used as heap2 (CONFIG_HEAP2_SIZE=32MB) — verify accessible from user space
- [ ] Debug any faults related to MPU/privilege level transitions
- **Depends on:** 1B.1, 1B.2, 1B.3

---

## Phase 1C: HCOM Bring-Up
**Goal:** Get HCOM protocol working over emulated serial
**Depends on:** Phase 1B (PROTECTED mode boot with `hcom_main`)

### Important: HCOM Uses USB CDC-ACM, Not UART
On real hardware, HCOM communicates over USB CDC-ACM (`/dev/ttyACM0`), configured via `CONFIG_CDCACM=y` in the `meadow_os` defconfig. HCOM is NOT a UART — it's a USB device class.

Renode does not emulate USB device controllers, so HCOM cannot use its native CDC-ACM transport in emulation. **Solution:** Create an emulation-specific NuttX defconfig (`meadow_os_emu`) that redirects HCOM to a UART by changing `CONFIG_HCOM_COMMS_DEVICE_NAME` from `/dev/ttyACM0` to a UART device (e.g., `/dev/ttyS1` mapped to USART2 or UART4).

### 1C.1 — Create Emulation Defconfig
- [ ] Copy `meadow_os` defconfig to `meadow_os_emu`
- [ ] Disable USB CDC-ACM: `CONFIG_CDCACM=n`, `CONFIG_USBDEV=n`
- [ ] Add/enable a second UART for HCOM: e.g., `CONFIG_STM32F7_USART2=y`
- [ ] Set `CONFIG_HCOM_COMMS_DEVICE_NAME="/dev/ttyS1"` (or whichever UART is chosen)
- [ ] Keep all other PROTECTED mode settings identical to `meadow_os`
- [ ] Verify build succeeds with the new defconfig

### 1C.2 — HCOM Serial Configuration in Renode
- [ ] Ensure the chosen UART (e.g., USART2) is present in `meadow-f7.repl`
- [ ] Expose HCOM UART via Renode socket: `emulation CreateServerSocketTerminal 1234 "hcom"`
- [ ] Connect HCOM UART to socket terminal in `.resc` script
- [ ] USART1 stays as console/debug output

### 1C.3 — HCOM Protocol Testing
- [ ] Verify `hcom_main` initializes and listens on the redirected UART
- [ ] Send basic HCOM commands via socket (e.g., using `nc` or a test script)
- [ ] Verify command/response cycle
- [ ] Test file transfer operations (requires QSPI flash working — Phase 1A.3 stub may suffice)

### 1C.4 — GDB Debugging Workflow
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

## Open Questions
1. **Renode MPU support:** Does Renode's Cortex-M7 model fully support MPU configuration? Critical gate for Phase 1B.
2. **RCC/Clock tree:** How much of the STM32F7 clock tree does Renode model? NuttX's `stm32_clockconfig()` configures PLL from HSE — may need stubs.
3. **HCOM dual-serial:** ~~Can Renode expose two UART instances?~~ **RESOLVED:** HCOM uses USB CDC-ACM, not UART. Renode can't emulate USB device controllers. Solution: emulation-specific defconfig redirecting HCOM to a UART. Two UARTs (console + HCOM) are supported via multiple analyzers/sockets.
4. **GDB register set:** Renode's Cortex-M7 GDB server likely exposes OpenOCD-style registers (xPSR, msp, psp, control). Need to verify.
5. **Renode ESP32 support:** Does Renode have an ESP32/Xtensa platform? If so, dual-machine emulation becomes much more viable.
6. **ESPCP stub fidelity:** How much of the ESPCP protocol needs to be stubbed for Meadow.OS to boot with networking enabled? Minimum viable: system status + WiFi init responses.
