# Plan: ESP32 Coprocessor Emulation

## Track ID: `renode-esp32`
## Status: `in-progress` (post-boot polling working, event delivery next)
## Repo: `Meadow.OS.Emulator` (sibling to `Meadow.OS/`)

---

## Phase A: ESPCP SPI Stub Peripheral (C# in Renode)
**Goal:** Create a Renode peripheral that acts as a virtual ESP32, responding to ESPCP messages on SPI2
**Approach:** C# peripheral (not Python — Python peripherals only handle simple register reads/writes; we need the `ISPIPeripheral` interface for proper SPI frame handling)

### Current Status (2026-03-10)

**ESPCP Boot + Post-Boot Polling WORKING ✅**

- [x] A.1 — Stub architecture designed
- [x] A.2 — `peripherals/EspcpStub.cs` created (ISPIPeripheral + IGPIOReceiver)
- [x] A.3 — All interface handlers implemented (Transport, System, WiFi stubs, BLE stubs)
- [x] A.4 — Wired into platform (.repl + .resc), SPI DMA bypass hook working
- [x] A.5 — Boot-time ESPCP exchange WORKING (SPI exchanges in espcp_late_init)
- [x] A.5b — Post-boot ESPCP polling WORKING (mq_intercept hook)
- [ ] A.6 — Event delivery: queue events in EspcpStub, verify firmware receives them

**What works:**
- EspcpStub.cs fully implements ESPCP protocol (27-byte header, CRC32, all interfaces)
- SPI DMA bypass hook at 0x08147c94 feeds bytes through EspcpStub correctly
- espcp_early_init and espcp_late_init run (forced via R5=1 at 0x08141144)
- Boot SPI exchanges happen directly in espcp_late_init (not through espcp_thread)
- Post-boot: mq_intercept hook at mq_receive (0x08157130) injects send_response
  when caller is espcp_thread (LR=0x0814de4b) and poll flag at 0x2007FFF0 is set
- espcp_thread continuously polls EspcpStub via transport/send_response SPI exchanges
- Poll flag set automatically from espcp_thread entry hook at 0x0814de28
- HCOM `info` and `name` commands work correctly alongside ESPCP polling
- System stable at 45+ seconds of virtual time

**Key discoveries (2026-03-10):**

1. **Boot SPI exchanges are DIRECT, not queue-based.** espcp_late_init does SPI
   exchanges synchronously. espcp_thread's message queue is empty during/after boot.
   Counter-based two-phase hook was unnecessary.

2. **HCOM commands do NOT trigger ESPCP SPI.** WiFi/network operations go through
   NuttX usrsock interface, triggered only by Mono/.NET application socket calls.
   Since Mono isn't running, only the polling mechanism triggers post-boot ESPCP.

3. **NuttX BSS range: 0x20020CE0 - 0x200331E4.** Hook flags placed in BSS get zeroed
   during boot. Must use addresses outside BSS (0x2007FFF0 = end of SRAM2).

4. **ESPCP control signals use UART5 (/dev/ttyS2), not USART3.** The firmware's
   `hcom_esp32_network_monitor_thread` reads `/dev/ttyS2` (UART5). UART5 is already
   compiled in. UART5 socket terminal on port 4243 delivers data but NuttX poll()
   doesn't wake the blocked monitor thread (Renode limitation).

**Known issues:**
- mq_intercept prevents real mq_receive → messages pile up in queue (maxmsgs=10)
- One thread blocks on mq_send (nwaitnotfull=1) — doesn't affect stability
- ESPCP polling burns CPU (no throttle mechanism yet)

**Workarounds in .resc scripts (currently active):**
1. **Force ESPCP init** — Hook at 0x08141144 forces R5=1 (WiFi network type)
2. **Skip SPI lock/unlock** — Hooks at 0x08147d0c/0x08147d18 (bypass deadlock)
3. **Bypass Mono runtime check** — Hook at 0x08057bee returns 0 (not running)
4. **SPI DMA bypass** — Hook at 0x08147c94 feeds bytes through EspcpStub directly
5. **LSI calibration skip** — Hook at 0x0814d0d8 skips timer 5 polling
6. **mq_intercept** — Hook at 0x08157130 injects send_response for espcp_thread
7. **espcp_poll_enable** — Hook at 0x0814de28 sets poll flag at thread start

**Key legacy ELF addresses:**
- `espcp_early_init`: 0x08147edc
- `espcp_late_init`: 0x08147f80
- `espcp_thread`: 0x0814de28 (waits on mq_receive)
- `espcp_send_message`: 0x0814779c
- `espcp_send_data_over_spi`: 0x08147c94 (R0=tx, R1=rx, R2=len; either can be NULL)
- `espcp_queue_message`: 0x081480ac
- `espcp_queue_send_response_message`: 0x08147744
- `espcp_get_message`: 0x081478b8
- `espcp_reset`: 0x08147d64
- `espcp_process_message_waiting_control_signal`: 0x08178baa
- `espcp_uart_monitor_process_line`: 0x08178bb2
- `hcom_esp32_network_monitor_thread`: 0x08058f08 (user-space)
- `_control_signals` table: 0x08185958 (4 entries × 16 bytes)

**NuttX serial device mapping (legacy emulator build):**
Only USART1, UART4, UART5 compiled in (no USART3):
- /dev/ttyS0 = USART1 @ 0x40011000 (console/syslog)
- /dev/ttyS1 = UART4 @ 0x40004C00 (HCOM, TCP port 4242)
- /dev/ttyS2 = UART5 @ 0x40005000 (unused)
ELF globals confirm: g_usart1priv, g_uart4priv, g_uart5priv only (no g_usart3priv)

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
- [x] Create `peripherals/EspcpStub.cs` implementing `ISPIPeripheral` (not ISPISlave)
- [x] Implement SPI frame receive/transmit (4092-byte frames)
- [x] Parse 27-byte ESPCP message header:
  - Validate CRC-32 (polynomial 0xedb88320, seed 0xffffffff) — no CRC8 needed
  - Extract interface, function, message_id, payload
- [x] Construct response frames:
  - Set message_type = `0x10` (Response)
  - Set status_code = 0 (OK)
  - Calculate CRC-32
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
- [x] Add to `meadow-f7.repl`:
  ```
  espcpStub: SPI.EspcpStub @ spi2
      Ready -> gpioPortI@10
  ```
- [x] Add `include @peripherals/EspcpStub.cs` to both legacy .resc scripts
- [x] Add `sysbus.spi2.espcpStub Reset` after platform load (Ready GPIO timing fix)
- [x] Add SPI DMA bypass hook at `espcp_send_data_over_spi` (0x08147c94)
- [ ] Wire ESP32 reset line (gpioPortF pin 7 → stub reset input) — deferred
- [ ] Wire CS line (gpioPortI pin 0 → stub CS input) — deferred, managed by SPI controller

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
| 2026-03-08 | C# for ESPCP stub (not Python) | Python peripherals only handle simple register reads/writes; we need the `ISPIPeripheral` interface for proper SPI frame handling |
| 2026-03-09 | Target legacy OS for ESPCP bringup | Legacy Meadow (not new OS rebase) because ESP isn't brought up yet on the rebase |
| 2026-03-09 | ISPIPeripheral (not ISPISlave) | Renode's SPI model uses `ISPIPeripheral` with byte-at-a-time `Transmit(byte)` + `FinishTransmission()` |
| 2026-03-09 | SPI DMA bypass via IronPython hook | Same pattern as QUADSPI DMA bypass — Renode's STM32SPI doesn't support DMA (TXDMAEN unhandled), so hook reads/writes memory directly and calls peripheral |
| 2026-03-09 | Stub starts booted (Ready=true) | GPIO reset wiring deferred; Reset() sets booted=true and Ready.Set(true) |
| 2026-03-10 | mq_receive injection (not GPIO EXTI) | ESPCP control signals use UART (`+++MW` etc. on USART3), not GPIO EXTI. `mint_config_interrupt` is for managed code GPIO interrupts (confirmed by engineer), unrelated to ESPCP. |
| 2026-03-10 | mq_inject hook at bl sites is inert | Hooks at `bl` instructions: the `bl` still executes after the hook, overriding PC changes. Boot ESPCP works from real queued messages (espcp_late_init). Hook removed. |
| 2026-03-10 | USART3 needed for post-boot ESPCP | USART3 not compiled into legacy build. Monitor thread can't read control signals. Build change required to enable. |
| 2026-03-10 | IronPython conditional register crash documented | `cpu.PC = X if cond else None` assigns None to PC on false branch. Must use list comprehensions: `[cpu.SetRegisterUnsafe(15, val) for _ in [1] if cond]` |
| 2026-03-10 | READ_ESP_MAC uses esptool protocol | `hcom_esp32_util_read_esp32_mac` puts ESP32 into UART boot mode and reads eFuse registers (0x60000078) — completely different from ESPCP SPI. Not needed for normal operation. |

## Changes Log

| Date | Files Changed | What & Why |
|------|---------------|------------|
| 2026-03-09 | `peripherals/EspcpStub.cs` | Created full ESPCP stub peripheral (ISPIPeripheral + IGPIOReceiver) |
| 2026-03-09 | `platforms/meadow-f7.repl` | Added `espcpStub: SPI.EspcpStub @ spi2` with Ready → gpioPortI@10, MessageWaiting → gpioPortI@0 |
| 2026-03-09 | `scripts/meadow-legacy.resc` | Added EspcpStub include, Reset call, SPI DMA bypass hook, debug traces |
| 2026-03-09 | `scripts/meadow-legacy-headless.resc` | Same changes as interactive script |
| 2026-03-09 | `tools/hcom_test.py` | Added `espmac` command (READ_ESP_MAC_ADDRESS 0x0117) for testing |
| 2026-03-10 | `scripts/meadow-legacy*.resc` | Removed inert mq_inject hook (bl sites don't skip). Boot ESPCP works from real queued messages. |
| 2026-03-10 | `README.md` | Moved detailed workarounds to KnownIssues_and_Errata.md, replaced with summary table |
| 2026-03-10 | `KnownIssues_and_Errata.md` | Updated ESPCP sections, added IronPython crash pattern, documented FPU frame legacy unaffected |

## Open Questions
1. **Renode ESP32 support:** Does Renode have an ESP32/Xtensa platform? If so, dual-machine emulation becomes much more viable.
2. ~~**ESPCP stub fidelity:**~~ RESOLVED — Full ESPCP protocol implemented in EspcpStub.cs (Transport, System, WiFi, BLE).
3. ~~**Mono runtime gate:**~~ RESOLVED — Bypassed at 0x08057bee (return 0 = not running).
4. ~~**Ready GPIO propagation:**~~ RESOLVED — `sysbus.spi2.espcpStub Reset` after platform load works; GPIO IDR reflects the input correctly.
5. ~~**ESPCP polling CPU burn:**~~ RESOLVED — mq_inject removed; espcp_thread blocks on mq_receive harmlessly. If/when ESPCP exchange is triggered, re-queue loop behavior needs testing (EspcpStub clears MessageWaiting GPIO after response consumed, which should prevent re-queue).
6. **USART3 not in legacy build:** `CONFIG_STM32F7_USART3` must be added to emulator build for post-boot ESPCP commands. This changes ttyS numbering (HCOM moves to ttyS2, needs CONFIG update) and requires full hook address re-scan. **Primary blocker for post-boot ESPCP.**
7. ~~**MINT system clarification:**~~ RESOLVED — `mint_config_interrupt` is for managed code GPIO interrupts (STM32F7 → Mono/.NET), NOT for ESPCP. Confirmed by engineer.
8. ~~**mq_inject crash root cause:**~~ RESOLVED — mq_inject hook was inert (bl instruction executes regardless of PC change in hook). The "crash" was unrelated. Boot ESPCP works from real queued messages.
9. ~~**EspcpStub MessageWaiting GPIO re-queue behavior:**~~ N/A for boot — boot uses real mq messages, not GPIO-triggered re-queues. Post-boot GPIO behavior needs testing after USART3 is enabled.
10. **READ_ESP_MAC uses different protocol:** `hcom_esp32_util_read_esp32_mac` talks to ESP32 via UART boot mode (esptool protocol), reading eFuse registers. Completely separate from ESPCP SPI. Not needed for normal Meadow.CLI operation (MAC addresses come from ESPCP boot exchange).
