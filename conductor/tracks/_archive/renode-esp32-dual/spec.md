# Specification: Dual-Machine ESP32 Emulation

## Track ID
`renode-esp32-dual`

## Problem Statement
The current ESPCP SPI stub (`renode-esp32` track) fakes ESP32 responses in a C# peripheral. This works for basic boot and polling but can't exercise the real ESP32 firmware — WiFi stack, BLE stack, NVS, message encoding, and the SPI slave transport. Running the actual Meadow ESP32 firmware on an emulated Xtensa core alongside the STM32 would give full-fidelity testing of the inter-processor communication path.

## Goal
Run the Meadow ESP32 firmware (ESP-IDF 4.2.4, standard ESP32) on a Renode Xtensa core as a second machine, connected to the STM32 machine via a virtual SPI bridge. Both processors execute their real firmware and communicate over the ESPCP protocol.

## Dependencies
- **`renode-esp32`** — ESPCP stub track (complete, provides protocol knowledge)
- **`renode-mono`** — Mono runtime (needed to trigger WiFi/network operations end-to-end)
- ESP-IDF 4.2.4 toolchain (Xtensa GCC) for building ESP32 firmware
- Meadow ESP32 source at `Meadow.OS/esp32/`

## Key Challenges

### 1. No SPI Inter-Machine Hub in Renode
Renode has UART hubs and CAN hubs but no SPI equivalent. A custom C# bridge peripheral is needed to forward SPI frames between machines.

### 2. WiFi/BLE Hardware
ESP-IDF's WiFi and BLE stacks talk to real radio hardware that Renode can't emulate. Options:
- Stub at ESP-IDF API level (`esp_wifi_*`, `esp_bt_*`) to return mock data
- Build a modified firmware variant with mock WiFi
- Use Renode's BLE medium for BLE (partial support exists)

### 3. ESP32 Platform Definition
No pre-built ESP32 board definition in Renode. Must create `.repl` from scratch with:
- Xtensa CPU (`cpuType: "esp32"`)
- Memory regions (IRAM, DRAM, flash-mapped)
- SPI slave peripheral, UART, GPIO
- Partition table (NVS, app, LittleFS)

### 4. ESP-IDF Bootloader
Second-stage bootloader does hardware init. May need to skip it and load app image directly.

## Architecture

```
┌─────────────────────┐              ┌─────────────────────┐
│ Machine: STM32F7    │              │ Machine: ESP32      │
│                     │              │                     │
│  NuttX + ESPCP      │    SPI       │  ESP-IDF + ESPCP    │
│  driver (master)    │◄────────────►│  slave transport    │
│                     │  C# Bridge   │                     │
│  UART4 → HCOM      │              │  WiFi handler       │
│  USART1 → Console  │              │  BLE handler        │
│                     │   GPIO       │  System handler     │
│  PF7 (Reset) ──────│─────────────►│  Reset              │
│  PI10 ◄─────────────│──────────── │  Ready (GPIO0)      │
│  PI0  ◄─────────────│──────────── │  MsgWaiting (GPIO3) │
└─────────────────────┘              └─────────────────────┘
```

## ESP32 Firmware Details
- **Chip:** Standard ESP32 (not S2/S3)
- **ESP-IDF:** 4.2.4
- **SPI:** VSPI_HOST (SPI3), Mode 3, CS=GPIO5, CLK=GPIO18, MOSI=GPIO23, MISO=GPIO19
- **Handshake:** GPIO0 (SPI Ready), GPIO3 (Message Waiting)
- **Entry:** `AppMain.cpp` → `SpiTransportProvider` → `MessageDispatcher`
- **Source:** `Meadow.OS/esp32/`
- **Build:** `Meadow.OS/build-esp32.sh`

## Success Criteria
- [ ] ESP32 Xtensa core boots in Renode (hello-world or bootloader)
- [ ] ESP32 platform definition created with correct memory map
- [ ] SPI bridge peripheral connects both machines
- [ ] ESPCP handshake completes between real STM32 and real ESP32 firmware
- [ ] WiFi start/connect flow works (with stubbed radio hardware)
- [ ] System stable with both machines running simultaneously
