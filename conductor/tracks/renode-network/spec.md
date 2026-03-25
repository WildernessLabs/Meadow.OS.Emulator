# Specification: Network Stack End-to-End Testing

## Track ID
`renode-network`

## Problem Statement
The ESPCP stub (`peripherals/EspcpStub.cs`, completed in the `renode-esp32` track) implements all WiFi/BLE message handlers, but every socket operation returns canned data: `WiFiSocket` returns a monotonically incrementing fake FD, `WiFiSend`/`WiFiWrite` ACK and discard the payload, `WiFiRecv`/`WiFiRead` return 4 zero bytes, `WiFiPoll` returns no-events-ready, and `WiFiGetAddrInfo` returns a mock address. There is no way for a Meadow.NET application running in the emulator to make a real network connection.

For meaningful testing of Meadow networking APIs (HTTP client, MQTT, TCP server, DNS resolution), the emulator must bridge ESPCP socket operations to the host OS network stack so that emulated firmware can exchange real data with the outside world.

## Goal
Enable Meadow.NET applications running in the emulator to make real network connections (HTTP, TCP, UDP, DNS) by replacing the mock socket handlers in `EspcpStub.cs` with a bridge that maps ESPCP socket operations to real `System.Net.Sockets.Socket` operations on the host.

## Dependencies
- **`renode-esp32`** — ESPCP stub (complete: boot + polling working)
- **`renode-mono`** — Mono runtime (complete: JIT boots, apps can run)
- **Tier 3 verification** — Requires Mono runtime to start successfully with deployed app assemblies. Flash persistence (W.10) means deployed files are lost on restart.

## Architecture

### Approach: Bridge at ESPCP Stub Level (Option A from original spec)

The existing `EspcpStub.cs` already handles SPI frame parsing, ESPCP header decode/encode, CRC, message routing, and the complete transport/system/WiFi/BLE interface dispatch. The change is surgical: replace the 16 mock WiFi socket handler methods with implementations that delegate to real host sockets.

```
Meadow.NET App
    │ socket(), connect(), send(), recv(), close()
    ▼
NuttX usrsock driver (espcp_usrsock_sockif.c)
    │ espcp_encode_*_request() → ESPCP message → SPI
    ▼
EspcpStub.cs (Renode C# peripheral)
    │ DecodeMessage → HandleWiFi → socket handler
    │    ┌───────────────────────────────────┐
    │    │  NEW: SocketBridge (this track)   │
    │    │                                   │
    │    │  ESPCP FD ←→ System.Net.Socket    │
    │    │  Decode request payloads          │
    │    │  Perform real socket ops          │
    │    │  Encode response payloads         │
    │    └───────────────────────────────────┘
    ▼
Host OS TCP/IP Stack → Internet
```

### Why Not Option B (Dual-Machine Bridge)
The dual-machine ESP32 emulation (`renode-esp32-dual`) runs real ESP32 firmware but would require bridging at the lwIP/`esp_netif` level, which means either modifying the ESP32 firmware or implementing a Renode TAP interface — both significantly more complex for no additional coverage of the STM32 ESPCP code path. Option A gives us real network access with changes isolated to a single file.

## ESPCP Wire Format for Socket Operations

All socket operations use **WiFi interface (1)**. The firmware encodes requests using `espcp_encode_*` functions (little-endian integers, length-prefixed byte arrays). The stub must decode these payloads and encode matching responses.

### Address Format (espcp_sock_addr_t — 31 bytes)
| Offset | Size | Field |
|--------|------|-------|
| 0 | 1 | family (AF_INET=2) |
| 1 | 2 | port (LE) |
| 3 | 4 | ip4_address (LE) |
| 7 | 4 | flow_info |
| 11 | 16 | ip6_address |
| 27 | 4 | scope_id |

### Common Response: integer_and_errno (8 bytes)
| Offset | Size | Field |
|--------|------|-------|
| 0 | 4 | result (int32 LE) |
| 4 | 4 | errno (int32 LE) |

Used by: socket, connect, write, send, sendto, bind, listen, close, setsockopt.

### Socket Operations — Request/Response Payloads

**Socket (func=8):**
- Request: address_info(4) + domain(4) + type(4) + protocol(4) = 16 bytes
- Response: integer_and_errno (result = socket FD on success)

**Connect (func=9):**
- Request: socket_handle(4) + addr_length(4) + addr(addr_length)
- Response: integer_and_errno

**Send/Write (func=19/10):**
- Request: socket_handle(4) + buffer_length(4) + buffer(N) + length(4) + flags(4) + dest_addr_length(4) + dest_addr(M)
- Response: integer_and_errno (result = bytes sent)

**Read (func=13):**
- Request: socket_handle(4) + count(4)
- Response: buffer_length(4) + buffer(N) + result(4) + errno(4)

**RecvFrom (func=16):**
- Request: socket_handle(4) + length(4) + flags(4) + get_source_address(4)
- Response: buffer_length(4) + buffer(N) + result(4) + errno(4) + source_addr_length(4) + source_addr(M) + source_addr_len(4)

**Poll (func=17):**
- Request: socket_handle(4) + events(2) + timeout(4) + setup(4) + setup_message_id(4)
- Response: returned_events(2) + result(4) + errno(4)

**Bind (func=20):**
- Request: socket_handle(4) + addr_length(4) + addr(N)
- Response: integer_and_errno

**Listen (func=21):**
- Request: socket_handle(4) + back_log(4)
- Response: integer_and_errno

**Accept (func=22):**
- Request: socket_handle(4) + addr_length(4) + addr(N)
- Response: addr_length(4) + addr(N) + result(4) + errno(4)

**Close (func=14):**
- Request: socket_handle(4)
- Response: integer_and_errno

**GetAddrInfo (func=27):**
- Request: node_name(null-terminated string) + serv_name(null-terminated string) + hints_length(4) + hints(N)
- Response: errno(4) + res_length(4) + res(N)

**SetSockOpt (func=11) / GetSockOpt (func=12) / Ioctl (func=23):**
- Stubbed initially (return success) — not needed for basic HTTP/TCP/DNS

### Constraints
- Max read/write buffer: 4000 bytes (MAXIMUM_READ_WRITE_BUFFER_SIZE)
- Max SPI frame payload: 4061 bytes (4092 - 27 header - 4 overhead)
- Max message payload: 8192 bytes (multi-packet for larger payloads)
- Integers are little-endian (espcp_encode_int32/uint32 use LE)

## WiFi State Machine

The firmware expects a WiFi connection flow before socket operations:
1. `WiFiStartInterface (func=0)` → EspcpStub fires `start_wi_fi_interface_event (34)`
2. `WiFiConnectToDefaultAP (func=3)` or `WiFiConnectToAP (func=2)` → EspcpStub fires `network_connected_event (36)` with IP configuration
3. Socket operations proceed

The current stub already handles steps 1-2 with mock data. The network track should make the connect event report the host machine's actual IP/subnet/gateway so the firmware has realistic network configuration.

## Blocking I/O Considerations

Socket operations like `connect`, `send`, `recv`, and `accept` can block for extended periods on real networks. Since `EspcpStub.cs` runs in the Renode simulation thread, blocking operations would halt the entire emulation.

**Strategy:** Use non-blocking socket operations with `Socket.Poll()` for readiness checks. For operations that would block (connect in progress, recv with no data), return EAGAIN/EWOULDBLOCK and let the firmware's poll loop retry. This matches how the real ESP32 firmware handles its lwIP sockets.

## Success Criteria
- [ ] DNS resolution: NuttX DNS client sends UDP queries through SocketBridge to real DNS server (firmware's `getaddrinfo` is unimplemented; DNS server IP comes from `got_ip` event)
- [ ] TCP client: Meadow app can `socket → connect → send → recv → close` to a real TCP server
- [ ] HTTP GET: Meadow app can fetch a URL (depends on TCP + DNS)
- [ ] UDP send/recv: `WiFiSendTo` / `WiFiRecvFrom` exchange real UDP datagrams
- [ ] Error propagation: connection refused, timeout, DNS failure return correct errno values
- [ ] TCP server: `bind → listen → accept` creates a real listening socket on the host
- [ ] Concurrent sockets: multiple sockets open simultaneously work correctly
- [ ] No emulation stall: socket operations don't block the Renode simulation thread

## Out of Scope
- TLS/SSL (handled by Mono's SslStream, not at the socket level)
- WiFi scanning (no host-OS equivalent without real radio hardware; remains an empty stub)
- BLE operations
- IPv6 (stub the fields, only implement IPv4 initially)
- Multi-packet messages (payloads > 4061 bytes — defer until needed)

## Decision Log

| Date | Decision | Rationale |
|------|----------|-----------|
| 2026-03-10 | Separate track from renode-esp32 | Network bridge is blocked on Mono and is a distinct feature from the ESPCP stub |
| 2026-03-10 | Bridge at EspcpStub level (Option A) | Simpler than dual-machine approach, works with existing stub |
| 2026-03-23 | Non-blocking I/O with EAGAIN | Prevents blocking the Renode simulation thread; matches real ESP32 behavior |
| 2026-03-23 | IPv4 only initially | IPv6 not needed for current test scenarios; address struct supports it for future |
| 2026-03-23 | Defer setsockopt/getsockopt/ioctl | Return success stubs — not needed for basic HTTP/TCP/DNS flows |
