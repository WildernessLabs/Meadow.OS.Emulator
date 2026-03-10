# Plan: Network Stack End-to-End Testing

## Track ID: `renode-network`
## Status: `planned` (blocked on `renode-mono`)
## Repo: `Meadow.OS.Emulator`

---

## 1 — Bridge Socket Operations in EspcpStub

### 1.1 — Socket Lifecycle
- [ ] Map ESPCP socket FDs to real `System.Net.Sockets.Socket` objects in EspcpStub
- [ ] `WiFiSocket` → create real socket, return FD
- [ ] `WiFiClose` → close real socket, release FD
- [ ] Track socket state (created, connected, bound, listening)

### 1.2 — TCP Client
- [ ] `WiFiConnect` → real TCP connect (async to avoid blocking SPI)
- [ ] `WiFiSend`/`WiFiWrite` → real send, return bytes sent
- [ ] `WiFiRecv`/`WiFiRead` → real recv, return data
- [ ] `WiFiPoll` → check real socket readability/writability

### 1.3 — TCP Server
- [ ] `WiFiBind` → real bind
- [ ] `WiFiListen` → real listen
- [ ] `WiFiAccept` → real accept, return new FD

### 1.4 — UDP
- [ ] `WiFiSendTo` → real sendto
- [ ] `WiFiRecvFrom` → real recvfrom with source address

### 1.5 — DNS
- [ ] `WiFiGetAddrInfo` → real DNS resolution via `System.Net.Dns`

## 2 — WiFi State Machine
- [ ] `WiFiStartInterface` → set "WiFi enabled" state, fire event
- [ ] `WiFiConnectToDefaultAP` / `WiFiConnectToAP` → set "connected" state, fire event with host's IP info
- [ ] `WiFiDisconnectFromAP` → reset state
- [ ] Report host machine's actual IP/subnet/gateway in connect event

## 3 — Testing
- [ ] Test with simple Meadow.NET HTTP client app
- [ ] Test with Meadow.NET TCP server app
- [ ] Test connection failure scenarios
- [ ] Test DNS resolution

---

## Decision Log

| Date       | Decision | Rationale |
|------------|----------|-----------|
| 2026-03-10 | Separate track from renode-esp32 | Network bridge is blocked on Mono and is a distinct feature from the ESPCP stub |
| 2026-03-10 | Bridge at EspcpStub level first | Simpler than dual-machine approach, works with existing stub |
