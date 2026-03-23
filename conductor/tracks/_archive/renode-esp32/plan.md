# Plan: ESP32 Coprocessor Emulation

## Track ID: `renode-esp32`
## Status: `complete` (ESPCP SPI stub — boot + post-boot polling working)
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
- [x] A.6 — Event delivery implemented in EspcpStub (QueueEvent + pendingEvents queue)
  - End-to-end testing requires Mono → moved to `renode-network` track

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
- [ ] Handle multi-packet messages (payload > 4061 bytes spans multiple SPI frames) — deferred, not needed for current stub

### A.3 — Implement Interface Handlers
**Minimum viable set for boot + basic networking:**

**System interface (4) — Required for boot:**
- [x] `get_configuration (0)` → return default config (device name, antenna, MAC addresses)
- [x] `set_configuration_item (1)` → ACK, store in memory
- [x] `logging_configuration (13)` → ACK
- [x] `get_battery_charge_level (3)` → return mock value (e.g., 100%)

**Transport interface (5) — Required for handshake:**
- [x] `response_ready (0)` → signal that stub is ready
- [x] `reset_esp32 (3)` → ACK, re-initialize stub state

**WiFi interface (1) — Stub responses implemented:**
- [x] `start_wi_fi_interface (0)` → return success, fire `start_wi_fi_interface_event (34)`
- [x] `stop_wi_fi_interface (1)` → return success
- [x] `connect_to_default_access_point (3)` → return success, fire `network_connected_event (36)` with mock IP
- [x] `get_access_points (6)` → return empty list or mock AP
- [x] `socket (8)` → return mock file descriptor (incrementing int)
- [x] `connect (9)` → return success
- [x] `write (10)` / `send (19)` → ACK, discard data (or log it)
- [x] `read (13)` / `recv_from (16)` → return empty or mock data
- [x] `close (14)` → return success
- [x] `poll (17)` → return no events ready
- [x] `bind (20)`, `listen (21)`, `accept (22)` → return success/mock FD
- [x] `set_sock_opt (11)` / `get_sock_opt (12)` → ACK/return defaults
- [x] `get_addr_info (27)` → return mock address resolution

**Bluetooth interface (2) — Stub only:**
- [x] `start (0)` → return success
- [x] `stop (1)` → return success

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
- [x] Verify ESPCP init sequence: reset ESP32 → wait for Ready → send first message
- [x] Confirm system config exchange works
- [ ] Test WiFi start → connect → socket create → send/recv cycle (requires Mono app)
- [x] Verify no timeouts or deadlocks in ESPCP message queues
- [x] Test graceful handling of unsupported functions (return `espcp_status_codes_not_implemented`)
- [x] Add Renode logging for all ESPCP messages (useful for debugging)

**Moved to separate tracks:**
- Network bridge to host → `renode-network` track
- Dual-machine ESP32 emulation → `renode-esp32-dual` track

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
| 2026-03-10 | ~~USART3 needed for post-boot ESPCP~~ | SUPERSEDED: mq_intercept hook at mq_receive bypasses the need for UART control signals entirely. UART5 wired to port 4243 but not required. |
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
| 2026-03-10 | `scripts/meadow-legacy*.resc` | Added mq_intercept hook (post-boot polling), espcp_poll_enable hook, UART5 socket terminal (headless only) |
| 2026-03-10 | `build-meadow.os_legacy-emulated.sh` | Fixed comment: documented ttyS device numbering |

## Open Questions
1. ~~**Renode ESP32 support:**~~ RESOLVED — Renode has Xtensa CPU with `esp32` cpuType and `ESP32_UART`. No pre-built board definition but enough to build one. See `renode-esp32-dual` track.
2. ~~**ESPCP stub fidelity:**~~ RESOLVED — Full ESPCP protocol implemented in EspcpStub.cs (Transport, System, WiFi, BLE).
3. ~~**Mono runtime gate:**~~ RESOLVED — Bypassed at 0x08057bee (return 0 = not running).
4. ~~**Ready GPIO propagation:**~~ RESOLVED — `sysbus.spi2.espcpStub Reset` after platform load works; GPIO IDR reflects the input correctly.
5. ~~**ESPCP polling CPU burn:**~~ RESOLVED — mq_intercept hook enables continuous polling; espcp_thread polls EspcpStub via send_response loop. CPU burn is a known issue but doesn't affect stability. Throttle mechanism is a future optimization.
6. ~~**USART3 not in legacy build:**~~ SUPERSEDED — Post-boot ESPCP polling now uses mq_intercept hook at mq_receive (0x08157130) instead of UART control signals. USART3 is no longer needed. UART5 is wired to port 4243 for control signals but the monitor thread's poll() doesn't wake (Renode limitation, non-blocking).
7. ~~**MINT system clarification:**~~ RESOLVED — `mint_config_interrupt` is for managed code GPIO interrupts (STM32F7 → Mono/.NET), NOT for ESPCP. Confirmed by engineer.
8. ~~**mq_inject crash root cause:**~~ RESOLVED — mq_inject hook was inert (bl instruction executes regardless of PC change in hook). The "crash" was unrelated. Boot ESPCP works from real queued messages.
9. ~~**EspcpStub MessageWaiting GPIO re-queue behavior:**~~ SUPERSEDED — Post-boot polling uses mq_intercept hook, not GPIO-triggered re-queues. MessageWaiting GPIO is wired but not actively used by the current polling mechanism.
10. **READ_ESP_MAC uses different protocol:** `hcom_esp32_util_read_esp32_mac` talks to ESP32 via UART boot mode (esptool protocol), reading eFuse registers. Completely separate from ESPCP SPI. Not needed for normal Meadow.CLI operation (MAC addresses come from ESPCP boot exchange).
