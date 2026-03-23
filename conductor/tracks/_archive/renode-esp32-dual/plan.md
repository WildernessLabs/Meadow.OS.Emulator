# Plan: Dual-Machine ESP32 Emulation

## Track ID: `renode-esp32-dual`
## Status: Phases 1–3 ✅, Phase 4 in progress
## Repo: `Meadow.OS.Emulator` (branch `track/renode-esp32-dual`)

---

## Current State (2026-03-17)

Both STM32 (NuttX) and ESP32 (Meadow firmware) run simultaneously in Renode.
ESPCP frames flow bidirectionally through the SPI bridge. The Meadow CLI connects
via `socket://localhost:4242` and `device info` returns correct hardware info.

**What works end-to-end:**
- STM32 NuttX boots → HCOM handler active on UART4 (TCP 4242)
- ESP32 firmware boots → SPI task runs ProcessMessages loop
- STM32 ESPCP polling → SpiBridge → ESP32_SPI DMA inject → firmware processes → response back
- Firmware sends System events with "MeadowF7" device info (type=0x04, iface=4)
- Meadow CLI: Model=F7Micro, HW=F7FeatherV2, Proc=STM32F777IIK6, CoPro=ESP32, Protocol=8
- System runs stably with no crashes

**Key configuration:**
- STM32: `cpu PerformanceInMips 100` / ESP32: `cpu PerformanceInMips 10` (execution time balance)
- CLI route: `meadow config route socket://localhost:4242`
- CLI binary: `Meadow.CLI/Source/Meadow.CLI/bin/Debug/meadow.dll`

---

## Phase 1: ESP32 Boot Spike ✅

**Goal:** Validate Renode's Xtensa/ESP32 support can boot the Meadow firmware.

- [x] ESP32 platform definition (`meadow-esp32.repl`) — Xtensa CPU, memory map, 30+ peripheral stubs
- [x] Bare-metal hello-world test — UART output confirmed
- [x] Meadow firmware (MeadowComms.elf) loads and executes
- [x] ESP32 ROM binary loaded (exception vectors, window handlers, ets_printf)
- [x] Custom C# peripherals: ESP32_RTC_CNTL, ESP32_TimerGroup, ESP32_DPORT
- [x] Binary patches for unsupported Xtensa features (FPU, rer, s32c1i, waiti)
- [x] **VECBASE fix** — critical: window overflow handlers at 0x40080000, not ROM
- [x] FreeRTOS boots: scheduler, context switches, IPC task demotion, tick simulation
- [x] **retw source fix** (`tcg_const_i32` → `tcg_temp_local_new_i32`) — eliminated 6845 Python hooks
- [x] **arch_tlb_fill fix** — return real MMU cause codes, not TRANSLATE_FAIL
- [x] Boot reaches idle task with SPI ProcessMessages loop running

## Phase 2: SPI Bridge ✅

**Goal:** Connect STM32 and ESP32 machines so ESPCP frames flow through real firmware.

### Architecture
```
STM32 (NuttX)                          ESP32 (Meadow firmware)
  ESPCP driver                           SpiTransportProvider
    │                                      │
  SpiBridge (C#, ISPIPeripheral)  ──→   ESP32_SPI (C#)
    on SPI2          ←──────────────      on SPI3
    │                                      │
  Ready/MsgWaiting GPIO ─────────────   DPORT interrupt matrix → CPU
```

### Completed
- [x] `peripherals/SpiBridge.cs` — ISPIPeripheral, cross-machine frame queues, GPIO
- [x] `peripherals/ESP32_SPI.cs` — register map, DMA descriptors, frame injection, IRQ
- [x] `peripherals/ESP32_DPORT.cs` — 69-source interrupt matrix, level-OR routing
- [x] Dual-machine boot script (`meadow-dual.resc`) — both machines boot simultaneously
- [x] STM32 ESPCP polling flows through SpiBridge (32-byte frames, type=0x20)
- [x] Xtensa interrupt delivery — two tlib bugs fixed (check_interrupts, OnGPIO)
- [x] SPI ISR fires → `spi_slave_hal_prepare_data` → `spi_slave_hal_user_start` → `cmd.usr` armed
- [x] ESP32_SPI injects frames from SpiBridge into firmware DMA buffers
- [x] Firmware processes ESPCP frames, generates 4092-byte responses
- [x] Responses flow back through SpiBridge to STM32
- [x] Continuous ISR cycle: prepare → arm → inject → respond → repeat

### Key fixes required for SPI bridge
- **Trampoline relocation**: window init trampoline ELF at 0x4009F000 was overwriting
  `spi_slave_hal_user_start` and `spi_slave_hal_prepare_data`. Replaced with Renode
  register API (`SetRegister` for WINDOWBASE/WINDOWSTART/PS/A1).
- **rfwo no-phys-sync**: overflow handler scratch values were propagating to phys_regs
  via sync, corrupting the caller's return address. New `restore_owb_no_phys_sync` helper.
- **TCG non-local temp fixes**: `tcg_const_i32` → `tcg_temp_local_new_i32` in translate_rfw,
  translate_callw/gen_callw_slot, translate_entry (same pattern as the retw fix).
- **Entry rotation in helper**: moved window rotation into HELPER(entry) + cpu_R reload
  to avoid stale-global writeback by gen_exit_tb.
- **SPI ISR patches**: freeze_cs/restore_cs NOPs (IO_MUX null function pointers).
- **Python workarounds**: Thread::Task null method guard, vListInsertEnd pxIndex validation,
  prepare_data a2/a8 fixup (TCG stale-global bug).

## Phase 3: ESP32 Peripheral Stubs ✅

All stubs sufficient for current operation. Added incrementally as boot revealed needs.

- [x] WiFi BB registers (0x3FF71000) — MappedMemory absorbs accesses
- [x] APLL registers (0x3FF66000) — RTC clock PLL config
- [x] WiFiRequestHandler::Setup skipped — unmapped WiFi registers
- [x] BluetoothRequestHandler::GetInstance returns NULL — ROM BT init hangs
- [x] NvsManager::Setup skipped — SPI flash partition table
- [x] FileSystem::Setup skipped — LittleFS partition access
- [x] Watchdog skips — esp_task_wdt_init/add return ESP_OK, esp_int_wdt_init returns
- [x] GPIO ROM callx8 NOPs in SPI ctor — IO_MUX returns null function pointers
- [x] ESP32 SPI3 peripheral (`ESP32_SPI.cs`) — full register map, DMA, SpiBridge connection
- [x] DPORT interrupt matrix (`ESP32_DPORT.cs`) — 69 sources, level-OR routing

## Phase 4: Full Integration Validation — IN PROGRESS

**Goal:** Both machines running real firmware end-to-end, full ESPCP protocol working.

### 4.1 — ESPCP Protocol Validation
- [x] Basic frame flow: STM32 polls → ESP32 receives → firmware responds ✅
- [x] Firmware parses ESPCP headers correctly (echoes msgId, matches iface/func) ✅
- [x] Firmware sends System events with device info ("MeadowF7", "emulator") ✅
- [x] Meadow CLI `device info` works via HCOM socket ✅
- [ ] System config exchange: GetConfiguration → SystemRequestHandler → full device details
- [ ] Compare response content with EspcpStub approach for protocol fidelity
- [ ] Multi-packet messages (PayloadLength > MAXIMUM_PACKET_SIZE)

### 4.2 — Full System Validation
- [ ] STM32 processes ESP32 responses (ESPCP response handler updates state)
- [ ] HCOM commands work alongside dual-machine ESPCP
- [ ] System runs stably for extended periods (minutes, not seconds)

---

## tlib Fixes (branch `meadow-emulator-fixes` on `WildernessLabs/tlib`)

15 patches total, all on the Xtensa target:

| # | Fix | Commit |
|---|-----|--------|
| 1 | arch_tlb_fill: return real MMU cause | `2520925` |
| 2 | WAITI: don't halt CPU | `4271279` |
| 3 | CCOUNT/CCOMPARE: fix time unit conversion | `8a1549f` |
| 4 | clock_freq_khz: 240MHz | `46768e0` |
| 5 | win_helper: assert→logging | `2574b54` |
| 6 | translate_retw: local TCG temp | `3c66ae3d` |
| 7 | xtensa_cpu_set_irq_pending_bit: call check_interrupts | `729c74b6` |
| 8 | Xtensa OnGPIO: remove base.OnGPIO call | (in Xtensa.cs) |
| 9 | translate_rfw: local TCG temp | `ca374e31` |
| 10 | window_check: n>w early exit | `ca374e31` |
| 11 | test_ill_retw: a0 fixup for WOVF8 scratch | `ca374e31` |
| 12 | rfwo: no-phys-sync (restore_owb_no_phys_sync) | `d6f6bcfc` |
| 13 | gen_callw_slot/translate_callw: local CALLINC temp | `9c77ec0b` |
| 14 | translate_entry: local temps + rotation in helper + cpu_R reload | `0132f8c4` |
| 15 | Diagnostic logging removed | `0cb72727` |

### Known non-blocking issue
TCG `gen_exit_tb` writes ALL fixed globals unconditionally, overwriting values set by
helpers (entry's window rotation). Workaround: Python hooks fix a2/a8 at prepare_data.
Proper fix needs TCG-level change to skip unmodified globals in gen_exit_tb.

---

## Boot Script Workarounds (both `meadow-dual.resc` and `esp32-meadow-boot-no-retw-hooks.resc`)

| Workaround | Address | Reason |
|------------|---------|--------|
| Thread::Task null method guard | `0x400e154c` | Race: priority-24 task preempts constructor |
| vListInsertEnd pxIndex validation | `0x400941bb` | Window spill writes IRAM addrs to list fields |
| SPI ISR freeze_cs NOP | `0x4008bc32` | IO_MUX returns null function pointers |
| SPI ISR restore_cs branch NOP | `0x4008bd48` | Same null pointer issue |
| prepare_data a2 fixup | `0x4009f02b` | TCG stale-global: HAL context pointer wrong |
| prepare_data a8 fixup | `0x4009f031` | TCG stale-global: hw pointer wrong |
| Window init via register API | (replaces trampoline) | Trampoline at 0x4009F000 overwrote SPI HAL |

---

## Research Findings

### Renode Xtensa/ESP32 Support
- CPU types: `sample_controller`, `esp32`, `esp32s2`, `esp32s3`
- ESP32_UART is the ONLY dedicated ESP32 peripheral — everything else stubbed
- No upstream Xtensa window bugs reported — our bugs are novel
- tlib Xtensa port from QEMU has minimal bug fixes since Dec 2021

### Meadow ESP32 Firmware
- ESP-IDF 4.2.4, SPI on VSPI_HOST (SPI3), Mode 3
- ESPCP: 27-byte header + payload, max 4092 bytes per SPI frame
- Source: `Meadow-ESP32/` repo (github.com/WildernessLabs/Meadow-ESP32) — legacy standalone
- Build: `cd Meadow-ESP32 && source ~/esp/esp-idf-v4.2.4/export.sh && ./build.sh`
- Output: `Meadow-ESP32/build/MeadowComms.elf`

### Key Paths
- Renode fork: `/Users/bryancostanich/Git_Repos/Wilderness_Labs/renode/`
- tlib: `renode/src/Infrastructure/src/Emulator/Cores/tlib/arch/xtensa/`
- ESP-IDF: `~/esp/esp-idf-v4.2.4/`
- Meadow CLI: `Meadow.CLI/Source/Meadow.CLI/bin/Debug/meadow.dll`
