# Plan: Dual-Machine ESP32 Emulation

## Track ID: `renode-esp32-dual`
## Status: `planned`
## Repo: `Meadow.OS.Emulator` (sibling to `Meadow.OS/`)

---

## Phase 1: ESP32 Boot Spike

**Goal:** Determine if Renode's Xtensa/ESP32 support can boot anything at all.

### 1.1 — Minimal ESP32 Platform Definition
- [ ] Create `platforms/meadow-esp32.repl` with Xtensa CPU (`cpuType: "esp32"`)
- [ ] Add memory regions: IRAM (0x40080000), DRAM (0x3FFB0000), RTC (0x50000000)
- [ ] Add flash-mapped region (0x3F400000) for SPI flash
- [ ] Add ESP32_UART for debug output
- [ ] Reference Renode's `xtensa-sample-controller.repl` as starting point

### 1.2 — Boot Test: Bare-Metal Hello World
- [ ] Build a minimal Xtensa hello-world (ESP-IDF or bare-metal)
- [ ] Load into Renode ESP32 machine
- [ ] Verify CPU executes instructions and UART outputs text
- [ ] Document what peripherals/registers cause crashes or hangs

### 1.3 — Boot Test: ESP-IDF Application
- [ ] Build Meadow ESP32 firmware (`build-esp32.sh`)
- [ ] Attempt to load app image (skip bootloader, load at 0x10000)
- [ ] Catalog all hardware accesses that fail (WiFi, BLE, flash controller, etc.)
- [ ] Determine minimum set of peripheral stubs needed

## Phase 2: SPI Bridge Peripheral

**Goal:** Connect SPI buses across two Renode machines.

### 2.1 — Design SPI Bridge
- [ ] C# peripheral implementing ISPIPeripheral on both sides
- [ ] Machine A (STM32): appears as SPI slave on SPI2 (replaces EspcpStub)
- [ ] Machine B (ESP32): appears as SPI master or hooks into SPI slave driver
- [ ] Frame buffering: capture full MOSI frame, forward to other machine, return MISO
- [ ] GPIO forwarding: Ready, MessageWaiting, Reset signals between machines

### 2.2 — Implement and Test Bridge
- [ ] Create `peripherals/SpiBridge.cs`
- [ ] Test with loopback (single machine, bridge to self)
- [ ] Test with two machines (STM32 ↔ ESP32)
- [ ] Verify frame integrity (CRC checks pass on both sides)

## Phase 3: ESP32 Peripheral Stubs

**Goal:** Stub hardware that ESP-IDF requires but Renode can't emulate.

### 3.1 — WiFi Hardware Stub
- [ ] Identify WiFi hardware registers accessed during `esp_wifi_init`/`esp_wifi_start`
- [ ] Create pydev or C# stub that returns success for WiFi hardware init
- [ ] OR: build ESP32 firmware variant with compile-time WiFi mock

### 3.2 — BLE Hardware Stub
- [ ] Same approach as WiFi — stub Bluetooth controller registers
- [ ] OR: disable BLE in ESP32 build for initial bring-up

### 3.3 — Flash/NVS Stub
- [ ] Stub SPI flash controller for NVS reads/writes
- [ ] Provide initial NVS partition data (device name, MAC, config)

## Phase 4: Integration

**Goal:** Both machines running real firmware, communicating via ESPCP.

### 4.1 — Two-Machine Script
- [ ] Create `scripts/meadow-dual.resc` (both machines + bridge + GPIO wiring)
- [ ] STM32 side: same as current `meadow-legacy.resc` but without EspcpStub
- [ ] ESP32 side: new machine with ESP32 firmware + peripheral stubs

### 4.2 — Validation
- [ ] ESPCP boot handshake completes (STM32 ↔ real ESP32 firmware)
- [ ] System config exchange works
- [ ] WiFi start/connect works (with stubbed radio)
- [ ] Compare SPI traces with stub approach to verify protocol fidelity
- [ ] HCOM commands work alongside dual-machine ESPCP

---

## Research Findings

### Renode Xtensa/ESP32 Support
- Xtensa CPU types available: `sample_controller`, `esp32`, `esp32s2`, `esp32s3`
- `ESP32_UART` peripheral exists in Renode
- No pre-built ESP32 board definition — must create from scratch
- BLE medium support exists (`CreateBLEMedium`)

### Meadow ESP32 Firmware
- Standard ESP32, ESP-IDF 4.2.4
- SPI slave on VSPI_HOST (SPI3), Mode 3
- Pins: CS=GPIO5, CLK=GPIO18, MOSI=GPIO23, MISO=GPIO19
- Handshake: GPIO0 (SPI Ready), GPIO3 (Message Waiting)
- Components: SpiTransportProvider, MessageDispatcher, WiFi/BLE/System handlers
- Source: `Meadow.OS/esp32/`, build: `Meadow.OS/build-esp32.sh`
- Partition table: NVS(0x9000), PHY(0xF000), App(0x10000), CoreDump(0x3B0000), LittleFS(0x3C0000)

### Inter-Machine SPI
- No SPI hub in Renode (only UART, CAN, LIN hubs)
- Custom C# bridge peripheral required
- Alternative: hook SPI at firmware level (like current DMA bypass) and forward via shared buffer

## Decision Log

| Date       | Decision | Rationale |
|------------|----------|-----------|
| 2026-03-10 | Separate track from renode-esp32 | Phase A (stub) is complete and working. Dual-machine is a distinct, larger effort. |
| 2026-03-10 | Spike-first approach | Unknown how well Renode's Xtensa/ESP32 support works. Validate before investing in bridge/stubs. |
