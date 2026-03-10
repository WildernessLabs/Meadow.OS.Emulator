# Specification: Network Stack End-to-End Testing

## Track ID
`renode-network`

## Problem Statement
The ESPCP stub (`renode-esp32` track) implements all WiFi/BLE message handlers, but they return canned responses. There's no way to make real network connections from emulated Meadow apps. For meaningful testing of Meadow.NET networking APIs, the emulator needs to bridge network operations to the host.

## Goal
Enable Meadow.NET applications running in the emulator to make real network connections (HTTP, TCP, UDP, DNS) by bridging ESPCP socket operations to the host OS network stack.

## Dependencies
- **`renode-esp32`** — ESPCP stub (complete)
- **`renode-mono`** — Mono runtime (required — WiFi/network is triggered by Mono/.NET app socket calls via NuttX usrsock → ESPCP)

## Architecture

Two possible approaches:

### Option A: Bridge at ESPCP Stub Level
Enhance `EspcpStub.cs` to create real host sockets when the firmware sends WiFi socket operations:
- `WiFiSocket` → `System.Net.Sockets.Socket()` on host
- `WiFiConnect` → real TCP connect
- `WiFiSend`/`WiFiWrite` → real send
- `WiFiRecv`/`WiFiRead` → real recv
- `WiFiClose` → real close
- `WiFiGetAddrInfo` → real DNS resolution

### Option B: Bridge at ESP32 Dual-Machine Level
If `renode-esp32-dual` is complete, the real ESP32 firmware handles socket operations. Bridge at the ESP-IDF level (`lwip` or `esp_netif`) to host networking via Renode's TAP/virtual network interface.

## Success Criteria
- [ ] Meadow.NET app can resolve DNS names
- [ ] Meadow.NET app can make HTTP GET requests
- [ ] Meadow.NET app can open TCP connections
- [ ] Meadow.NET app can send/receive UDP packets
- [ ] Network errors propagate correctly (connection refused, timeout, etc.)
