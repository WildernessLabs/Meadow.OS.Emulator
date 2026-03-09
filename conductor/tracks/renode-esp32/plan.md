# Plan: ESP32 Coprocessor Emulation

## Track ID: `renode-esp32`
## Status: `planned`
## Repo: `Meadow.OS.Emulator` (sibling to `Meadow.OS/`)

---

## Phase A: ESPCP SPI Stub Peripheral (C# in Renode)
**Goal:** Create a Renode peripheral that acts as a virtual ESP32, responding to ESPCP messages on SPI2
**Approach:** C# peripheral (not Python — Python peripherals only handle simple register reads/writes; we need the `ISPISlave` interface for proper SPI frame handling)

### A.1 — Stub Architecture Design
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

### A.2 — Create ESPCP Stub C# Peripheral
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

### A.3 — Implement Interface Handlers
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

### A.4 — Wire Stub into Platform
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

### A.5 — Test ESPCP Stub
- [ ] Verify ESPCP init sequence: reset ESP32 → wait for Ready → send first message
- [ ] Confirm system config exchange works
- [ ] Test WiFi start → connect → socket create → send/recv cycle
- [ ] Verify no timeouts or deadlocks in ESPCP message queues
- [ ] Test graceful handling of unsupported functions (return `espcp_status_codes_not_implemented`)
- [ ] Add Renode logging for all ESPCP messages (useful for debugging)

### A.6 — Network Bridge to Host (Stretch)
- [ ] Investigate bridging mock socket calls to real host networking
- [ ] If viable: `socket()` in stub creates a real host socket, `send()`/`recv()` proxy to it
- [ ] This would allow Meadow.OS to make real network connections from emulation
- [ ] Requires Renode host integration (may need C# peripheral with System.Net.Sockets)

---

## Phase B: Dual-Machine Emulation (Full Fidelity)
**Goal:** Run both STM32F7 and ESP32 as separate Renode machines connected via virtual SPI
**Priority:** Future — for testing real ESP32 firmware and full inter-chip protocol

### Background: Renode Multi-Machine Capabilities
Renode supports multiple machines in a single simulation with inter-machine connections:
- **UART:** `emulation CreateUARTHub "hub"` + `connector Connect` — buffered, deterministic
- **GPIO:** `emulation CreateGPIOConnector "gpio-con"` — unidirectional boolean signals (max 2 machines per connector; bidirectional needs 2 connectors)
- **SPI:** No built-in SPI hub/connector documented — may need custom bridge peripheral or C# connector
- **Xtensa CPU:** Renode has basic Xtensa support (`xtensa-sample-controller.repl`) but no full ESP32 platform

### B.1 — Investigate Renode SPI Inter-Machine Support
- [ ] Check if Renode has an SPI equivalent of `CreateUARTHub`
- [ ] If not: design a C# bridge peripheral that sits on both machines' SPI buses
  - Machine A (STM32): SPI master sends frame → bridge captures
  - Bridge forwards frame to Machine B (ESP32): SPI slave receives
  - ESP32 processes, responds → bridge forwards back to STM32
- [ ] Evaluate timing/synchronization: SPI is synchronous, Renode time domains may need coordination
- [ ] Check Renode community/issues for prior art on SPI multi-machine setups

### B.2 — Create ESP32 Platform Description
- [ ] Create `platforms/meadow-esp32.repl` based on Renode's `xtensa-sample-controller.repl`
- [ ] Adapt memory map for ESP32 (or ESP32-S3 if that's what Meadow uses):
  - IRAM: 0x40000000
  - DRAM: 0x3FFE0000 / 0x3FFC0000
  - Flash: memory-mapped region
  - SPI slave peripheral (ESPCP receiver side)
- [ ] Add UART for debug output (connects to UART5 on STM32 side via UARTHub)
- [ ] CPU: Xtensa with ESP32-appropriate configuration

### B.3 — Build ESP32 Firmware for Renode
- [ ] Review `../Meadow.OS/esp32/` build system (ESP-IDF + CMake)
- [ ] Determine if ESP32 firmware can be built for Renode's Xtensa model
- [ ] Key challenge: ESP-IDF relies on hardware-specific peripherals (WiFi radio, etc.) that Renode won't have
- [ ] May need to build a stripped-down ESP32 firmware that only runs the ESPCP SPI slave + mock WiFi responses
- [ ] Alternative: use the stub approach (Phase A) for WiFi/BLE, only emulate the ESPCP transport layer in ESP32 firmware

### B.4 — Wire Machines Together
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

  # Connect SPI (requires custom bridge — see B.1)
  # ... TBD based on SPI bridging investigation

  # Start both machines
  mach set "stm32"
  showAnalyzer usart1
  mach set "esp32"
  showAnalyzer uart

  start
  ```

### B.5 — Validate End-to-End Communication
- [ ] Verify startup: STM32 resets ESP32 → ESP32 boots → signals Ready → SPI handshake
- [ ] Test ESPCP message round-trip: STM32 request → SPI → ESP32 processes → SPI → STM32 receives
- [ ] Compare message traces with real hardware captures (if available)
- [ ] Test WiFi init sequence end-to-end
- [ ] Measure SPI timing accuracy and identify any synchronization issues

### B.6 — Network Passthrough (Stretch)
- [ ] If ESP32 machine has mock WiFi: bridge to host networking via Renode's TAP/virtual network
- [ ] Or: implement network passthrough at the ESPCP socket API level (same as A.6)

---

## Decision Log

| Date       | Decision | Rationale |
|------------|----------|-----------|
| 2026-03-08 | ESP32 coprocessor: stub first, dual-machine later | ESPCP SPI stub unblocks networking tests without needing ESP32 firmware. Dual-machine emulation is a future goal for full-fidelity testing |
| 2026-03-08 | ESPCP disabled for other tracks | `CONFIG_MEADOW_ESPCP_MANAGER` is already `not set` in defconfig — safe to boot without ESP32 for initial bring-up |
| 2026-03-08 | C# for ESPCP stub (not Python) | Python peripherals only handle simple register reads/writes; we need the `ISPISlave` interface for proper SPI frame handling |

## Open Questions
1. **Renode ESP32 support:** Does Renode have an ESP32/Xtensa platform? If so, dual-machine emulation becomes much more viable.
2. **ESPCP stub fidelity:** How much of the ESPCP protocol needs to be stubbed for Meadow.OS to boot with networking enabled? Minimum viable: system status + WiFi init responses.
