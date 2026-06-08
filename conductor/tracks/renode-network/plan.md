# Plan: Network Stack End-to-End Testing

## Track ID: `renode-network`
## Status: `in-progress`
## Branch: `track/renode-network`
## Repo: `Meadow.OS.Emulator`
## Spec: [spec.md](spec.md)

---

## Phase 1 — Socket Bridge Core

**Goal:** Replace mock socket handlers in `EspcpStub.cs` with real host socket operations for the critical path: socket → connect → send → recv → close.

### N.1 — Socket Manager Class ✅
- [x] Created `SocketBridge` as nested private class inside EspcpStub.cs (Renode ad-hoc compilation requires same compilation unit)
- [x] `Dictionary<int, Socket>` mapping ESPCP FDs to `System.Net.Sockets.Socket` objects
- [x] FD allocator (incrementing int, starting from 1)
- [x] Logging via Renode's `parent.Log()` extension method
- [x] `CloseAll()` for reset/cleanup

### N.2 — Payload Decode/Encode Helpers ✅
- [x] All decode helpers inline in handler methods (payload fields read directly)
- [x] `DecodeSockAddr` → `IPEndPoint` with NBO port byte-swap
- [x] `EncodeSockAddr` → 31-byte espcp_sock_addr_t with NBO port encoding
- [x] `EncodeIntegerAndErrno`, `EncodeReadResponse`, `EncodeRecvFromResponse`, `EncodePollResponse`, `EncodeAcceptResponse`
- [x] SocketError → POSIX errno mapping table

### N.3 — Socket Create/Close ✅
- [x] `HandleSocket`: decode domain/type/protocol → `new Socket(af, st, pt)` with `Blocking = false`
- [x] `HandleClose`: look up FD → `Socket.Close()` → remove from map
- [x] Domain/type/protocol mapping (AF_INET, SOCK_STREAM, SOCK_DGRAM, IPPROTO_TCP, IPPROTO_UDP)
- [x] Error handling: invalid FD → EBADF, unsupported domain → EAFNOSUPPORT

### N.4 — TCP Connect ✅
- [x] `HandleConnect`: decode sockaddr → `Socket.Connect(IPEndPoint)` (non-blocking)
- [x] WouldBlock/InProgress → EINPROGRESS (firmware polls for completion)
- [x] ConnectionRefused, TimedOut, and other errors mapped via `MapSocketError`

### N.5 — Send/Write ✅
- [x] `HandleSend`: unified handler for WiFiSend/WiFiWrite/WiFiSendTo
- [x] Decodes buffer + optional destination address (sendto)
- [x] Returns bytes_sent or EAGAIN/ECONNRESET/EPIPE on error

### N.6 — Recv/Read ✅
- [x] `HandleRead`: decode socket_handle + count → `Socket.Receive` → EncodeReadResponse
- [x] `HandleRecvFrom`: decode with flags + get_source_address → `Socket.ReceiveFrom` → EncodeRecvFromResponse
- [x] EOF (received=0) returns result=0 (not an error)
- [x] Cap at 4000 bytes (MaxReadWriteBuffer)

### N.7 — Poll ✅
- [x] `HandlePoll`: two-phase poll (setup=1 registers, setup=0 teardown+check)
- [x] POLLIN/POLLOUT/POLLERR/POLLHUP/POLLNVAL flags
- [x] Poll setup: store registration with `setup_message_id` + socket FD + events
- [x] Poll teardown: remove registration, do point-in-time Socket.Poll check
- [x] `CheckPollRegistrations()`: called during transport/send_response polling
- [x] Queues `interrupt_poll_response` events (func=18, 18-byte payload) when registered sockets become ready
- [x] Cleanup: poll registrations removed on socket close and Reset()

---

## Phase 2 — DNS and WiFi State

**Goal:** Real DNS resolution and realistic WiFi connection state.

### N.8 — DNS Resolution ✅ (RESOLVED — no GetAddrInfo needed)
**Discovery:** `espcp_usrsock_getaddrinfo` is **not implemented** in the firmware — returns -1. DNS resolution goes through NuttX's built-in DNS client (`lib_dns`), which sends UDP queries to the DNS server. The DNS server IP comes from the `got_ip` event (func=48). With the UDP socket bridge in place, DNS queries flow automatically: NuttX DNS client → UDP socket → SocketBridge → real DNS server.

- [x] Queue `network_got_ip_event` (func=48) with host's DNS server address after WiFi connect
- [x] Use `NetworkInterface.GetIPProperties().DnsAddresses` to find host DNS server
- [x] Fallback to 8.8.8.8 (Google DNS) if no host DNS found
- [x] GetAddrInfo mock left in place as dead code (firmware never calls it)

### N.9 — WiFi Connect Event with Real IP ✅
- [x] `BuildNetworkConnectedPayload()` now uses proper 57-byte `espcp_connect_event_data_t` format
- [x] Queries host via `NetworkInterface.GetAllNetworkInterfaces()` for actual IP/subnet/gateway
- [x] Falls back to 192.168.1.100/255.255.255.0/192.168.1.1 if no active interface found
- [x] SSID set to "Emulator" in the connect event
- [x] IP address bytes written in network byte order (matches firmware's espcp_encode/extract_uint32 convention)

---

## Phase 3 — TCP Server and UDP

**Goal:** Server-side and datagram socket operations.

### N.10 — TCP Server (Bind/Listen/Accept) ✅
Implemented in Phase 1 (SocketBridge includes HandleBind, HandleListen, HandleAccept).
- [x] `HandleBind`: decode sockaddr → `Socket.Bind(IPEndPoint)`
- [x] `HandleListen`: decode backlog → `Socket.Listen(backlog)`
- [x] `HandleAccept`: non-blocking `Socket.Accept()` → new FD + peer address
- [x] EAGAIN if no pending connection

### N.11 — UDP SendTo/RecvFrom ✅
Implemented in Phase 1 — HandleSend and HandleRecvFrom handle both TCP and UDP.
- [x] SendTo path handles destination address for UDP
- [x] RecvFrom path handles source address for UDP
- [x] Unconnected UDP sockets (sendto without connect) supported via destination address

---

## Phase 4 — Integration Testing

**Goal:** End-to-end verification with real Meadow.NET applications.

### N.12 — Standalone Socket Test
Deferred — the end-to-end path through firmware is more valuable than isolated testing.

### N.13 — Meadow.NET App Test ✅ PARTIALLY WORKING
- [x] `wifi_emulated_basics` sample created (stripped-down WiFi_Basics, F7FeatherV2 only)
- [x] Pre-loaded in LittleFS image (avoids CLI deploy timeout issues)
- [x] Mono boots, app initializes, WiFi connect command reaches EspcpStub
- [x] UDP socket created for DNS resolution (SocketBridge fd=1, SOCK_DGRAM)
- [ ] WiFi connect response/event delivery — IN PROGRESS (see ESPCP response path below)
- [ ] HTTP GET end-to-end — blocked on WiFi connect completing

### N.14 — Error Scenario Testing
Deferred until WiFi connect + HTTP GET works end-to-end.

---

## Phase 5 — Polish

### N.15 — Socket Cleanup ✅
Implemented in Phase 1.
- [x] `SocketBridge.CloseAll()` closes all open sockets and resets FD counter
- [x] Called from `EspcpStub.Reset()` and `OnGPIO` reset handler

### N.16 — Setsockopt/Getsockopt (if needed)
- [ ] Implement common options: SO_REUSEADDR, SO_KEEPALIVE, TCP_NODELAY, SO_RCVTIMEO, SO_SNDTIMEO
- [ ] Defer uncommon options (return success, log warning)

---

## Verification

### Tier 1 — Smoke Test
Existing hcom_test.py still passes (ESPCP polling + HCOM unaffected).

### Tier 2 — Socket Bridge Isolation Test
New test script (`tools/network_test.py` or inline in hcom_test.py) that:
1. Starts a local TCP echo server
2. Injects ESPCP messages via the existing SPI DMA bypass path
3. Verifies: socket create → connect → send → recv → close cycle works
4. Verifies: DNS resolution returns real IP

### Tier 3 — Meadow.NET App
`wifi_emulated_basics` app connects, resolves DNS, and fetches HTTP data through the socket bridge.
**Note:** Mono JIT boot takes several minutes of virtual time. WiFi connect typically fires ~2 min after simulation start.

---

## Implementation Notes

### File Changes
- `peripherals/EspcpStub.cs` — replace mock WiFi socket handlers with SocketBridge calls
- `peripherals/SocketBridge.cs` (new) — socket manager, payload decode/encode, host socket lifecycle
- `tools/network_test.py` (new) — integration test for socket bridge

### Key Design Decisions
1. **Separate SocketBridge class** — keeps EspcpStub.cs focused on ESPCP protocol; SocketBridge handles host networking. EspcpStub calls SocketBridge methods from its HandleWiFi dispatch.
2. **Non-blocking sockets** — all host sockets set to non-blocking. Operations that would block return EAGAIN. The firmware's poll-based I/O model handles this naturally.
3. **Errno mapping** — .NET `SocketException.SocketErrorCode` maps to POSIX errno values that the firmware expects. Need a mapping table (SocketError.ConnectionRefused → ECONNREFUSED=111, etc.).
4. **No threading** — everything runs on the Renode simulation thread. Non-blocking I/O ensures we never stall.

### ESPCP Function Code Reference (from espcp_shared_enums.h)
| Operation | Code | Handler | Response Encoder |
|-----------|------|---------|-----------------|
| socket | 8 | HandleSocket | EncodeIntegerAndErrno |
| connect | 9 | HandleConnect | EncodeIntegerAndErrno |
| write | 10 | HandleSend | EncodeIntegerAndErrno |
| setsockopt | 11 | HandleSetSockOpt (stub) | EncodeIntegerAndErrno |
| getsockopt | 12 | HandleGetSockOpt (stub) | EncodeIntegerAndErrno |
| read | 13 | HandleRead | EncodeReadResponse |
| close | 14 | HandleClose | EncodeIntegerAndErrno |
| send_to | 15 | HandleSend | EncodeIntegerAndErrno |
| recv_from | 16 | HandleRecvFrom | EncodeRecvFromResponse |
| poll | 17 | HandlePoll | EncodePollResponse |
| send | 19 | HandleSend | EncodeIntegerAndErrno |
| bind | 20 | HandleBind | EncodeIntegerAndErrno |
| listen | 21 | HandleListen | EncodeIntegerAndErrno |
| accept | 22 | HandleAccept | EncodeAcceptResponse |
| ioctl | 23 | HandleIoctl (stub) | EncodeIntegerAndErrno |
| getaddrinfo | 27 | (mock, firmware never calls) | — |

**Note:** Previous versions had wrong function codes: WiFiRecv=15 (actually send_to),
WiFiSendTo=24 (actually get_sock_name), WiFiIoctl=39 (actually error_event). Fixed.

---

## Decision Log

| Date | Decision | Rationale |
|------|----------|-----------|
| 2026-03-10 | Separate track from renode-esp32 | Network bridge is blocked on Mono and is a distinct feature from the ESPCP stub |
| 2026-03-10 | Bridge at EspcpStub level first | Simpler than dual-machine approach, works with existing stub |
| 2026-03-23 | SocketBridge as separate class | Keeps ESPCP protocol handling separate from host networking logic |
| 2026-03-23 | Non-blocking I/O throughout | Prevents Renode simulation stalls; matches real ESP32 lwIP behavior |
| 2026-03-23 | Phase 1 = TCP client critical path | socket/connect/send/recv/close covers HTTP client, the most common use case |
| 2026-03-23 | Defer setsockopt/ioctl to Phase 5 | Not needed for basic HTTP/TCP/DNS — can return success stubs |
| 2026-03-23 | Standalone test before Meadow.NET | Can validate bridge without Tier 3 dependency |
| 2026-03-23 | SocketBridge as nested class (not separate file) | Renode ad-hoc compilation compiles each .cs independently; nested class avoids cross-file type references |
| 2026-03-23 | DNS via UDP socket bridge, not GetAddrInfo | Firmware's `espcp_usrsock_getaddrinfo` is unimplemented (returns -1). DNS goes through NuttX DNS client → UDP sockets → SocketBridge. Just need `got_ip` event with valid DNS server. |
| 2026-03-23 | NBO port byte-swap in sockaddr | Firmware stores `sin_port` (NBO) directly in `espcp_sock_addr_t.port`. Wire format carries NBO values via LE encoding. Must swap bytes between NBO and .NET's host-order `IPEndPoint.Port`. |
| 2026-03-23 | Network-order IP bytes written directly | `GetAddressBytes()` returns NBO. Firmware's encode/extract round-trips NBO uint32 through LE encoding. Writing NBO bytes directly to the payload matches the firmware convention. |
| 2026-03-24 | Message ID echo in PrepareResponse | PrepareResponse was generating new IDs instead of echoing request's MessageId. Firmware matches responses by ID — mismatch = response discarded, caller blocks forever. |
| 2026-03-24 | Mono version: patch deep copy, not original config | hcom_nx_config_init takes YAML path (skips hardcoded version writes) when meadow.config.yaml exists. Must patch the deep copy in start_mono_main. |
| 2026-03-24 | LittleFS assemblies at root (not /mono/4.5/) | Firmware's are_needed_files_here checks /meadow0/mscorlib.dll. LFS image had them at /meadow0/mono/4.5/. |
| 2026-03-24 | esp_not_responding cleared at upd_handle_esp32_command | Flag initialized true, cleared by GPIO ISR path (bypassed by mq_intercept). Hook clears it where R0 = real config pointer from espcp_get_configuration. |
| 2026-03-24 | Poll enable at upd_handle_esp32_command (before mq_send) | Must be enabled BEFORE espcp_queue_message sends the command. If enabled after espcp_thread blocks in mq_receive, deadlock: nobody injects the send_response message to wake it. |
| 2026-03-26 | Priority response buffer fix | pendingCmdResponse buffer in EspcpStub survives intervening polls. WiFi connect response delivered correctly. |
| 2026-03-26 | MqReceiveIntercept sleep-when-poll-off | Redirect espcp_thread to usleep when poll=0, preventing kernel mq_receive deadlock. |

## ESPCP Response Delivery Path (Key Finding)

The firmware's espcp_send_message sends a command and gets an ACK in one SPI exchange.
The actual response payload comes via a SEPARATE transport/send_response SPI exchange
(espcp_get_message), triggered by espcp_thread's main loop processing a send_response
queue message. The MqReceiveIntercept hook injects these send_response messages when
poll=1. Without polling, espcp_thread blocks in mq_receive and the response is never
fetched — the managed caller blocks forever on its response semaphore.

**Critical timing**: poll must be enabled BEFORE espcp_queue_message calls mq_send,
because espcp_thread needs poll=1 when it loops back to mq_receive after processing
the command. The hook at upd_handle_esp32_command fires before espcp_queue_message,
ensuring correct timing.

## Resolved Questions
1. ~~**GetAddrInfo response format:**~~ RESOLVED — firmware never calls it. DNS goes through NuttX DNS client → UDP sockets.
2. ~~**Poll two-phase model:**~~ RESOLVED — point-in-time query works regardless of setup flag. Firmware retries poll via its own loop. No need to track registered interests.
3. **Max payload size:** Capped recv at 4000 bytes (`MaxReadWriteBuffer`), matching the firmware's `MAXIMUM_READ_WRITE_BUFFER_SIZE`. Multi-packet framing not needed.

---

## DNS Resolution Debugging Plan

### Current State (2026-03-28)
App reaches `HttpClient.GetAsync("http://postman-echo.com/get?request=1")` and BLOCKS
indefinitely. No native DNS function is ever called (verified with Renode hooks added at
startup on all 11 DNS-related functions). The app previously failed instantly with "No such
host is known" but now blocks after the loopback-as-WiFi configuration.

### Complete DNS Call Chain (verified from source code)

```
HttpClient.GetAsync(url)
  → MonoWebRequestHandler.SendAsync() [MonoWebRequestHandler.cs:495]
    → wrequest.GetResponseAsync()
      → ServicePoint.HostEntry [ServicePoint.cs:270]
        → Dns.GetHostEntry(hostname) [Dns.cs:397]
          → Dns.GetHostByName(hostname) [Dns.cs:439]
            → GetHostByName_internal() [P/Invoke to Mono native]
              → ves_icall_System_Net_Dns_GetHostByName_internal [w32socket.c:2636]
                → mono_get_address_info(hostname, ...) [networking-posix.c:66]
                  → getaddrinfo(hostname, service, &hints, &info) [networking-posix.c:96]
                    → gethostbyname_r() [lib_gethostbynamer.c:790]
                      1. lib_numeric_address() → quick, no network
                      2. lib_localhost() → quick, local check
                      3. lib_find_answer() → DNS cache check
                      4. lib_dns_lookup() → BLOCKING network I/O
                        → lib_dns_query() [lib_gethostbynamer.c:565]
                          → dns_bind() → socket(PF_INET, SOCK_DGRAM, 0) [lib_dnsbind.c:96]
                            → psock_socket() → ROUTES TO USRSOCK [socket.c:139-149]
                              → espcp_usrsock_socket() → espcp_queue_message(blocking=true)
                          → dns_query(sd, hostname, ...) [lib_dnsquery.c:920]
                            → dns_foreach_nameserver(callback, &query)
                              → fopen("/meadow0/dns.conf", "rb") [lib_dnsforeach.c:104]
                              → for each "nameserver" line:
                                → dns_query_callback() [lib_dnsquery.c:678]
                                  → dns_send_query(sd, hostname, &addr, ...) [lib_dnsquery.c:712]
                                    → sendto(sd, buffer, len, 0, &addr, addrlen) [lib_dnsquery.c:320]
                                      → psock_sendto() → si_sendto [sendto.c:162]
                                        → espcp_usrsock_sendto() [espcp_usrsock_sockif.c:1958]
                                          → CHECK: esp_not_responding → -ENETDOWN [line 1964]
                                          → espcp_queue_message(sendto_msg, true) [BLOCKS]
                                  → dns_recv_response(sd, ...) [lib_dnsquery.c:~730]
                                    → recvfrom(sd, ...) with SO_RCVTIMEO timeout
                                      → espcp_usrsock_recvfrom() → espcp_queue_message(blocking=true)
```

### Checkpoints to Verify (in order)

Each checkpoint tests one link in the chain. Test from TOP DOWN — the first failure
identifies the blocking point. For each checkpoint, set a Renode hook AT STARTUP (before
`start`) to avoid translation cache issues.

| # | Function | Address | What to check | Expected |
|---|----------|---------|---------------|----------|
| C1 | ServicePoint.HostEntry | N/A (managed) | Does HCOM show HTTP request? | "GET http://..." ✓ confirmed |
| C2 | GetHostByName_internal | 0xC011704C | Renode hook fires? | Should fire if managed DNS path works |
| C3 | mono_get_address_info | 0xC015CF40 | Renode hook fires? | Should fire (called from C2) |
| C4 | getaddrinfo (user) | 0x080BAAF2 | Renode hook fires? | Should fire (called from C3 via thunk) |
| C5 | gethostbyname_r (kernel) | 0x0815B8BC | Renode hook fires? | Should fire (called from C4) |
| C6 | dns_bind (user) | 0x080B2D44 | Renode hook fires? | Should fire (creates DNS UDP socket) |
| C7 | dns_foreach_nameserver | 0x080B30BC / 0x0815C1D4 | Hook fires? | Should fire (reads dns.conf) |
| C8 | sendto (kernel) | find psock_sendto addr | Hook fires? | Should fire (sends DNS query) |
| C9 | espcp_usrsock_sendto | 0x08148FF8 | Hook fires? | Should fire (routes to ESPCP) |
| C10 | espcp_queue_message | 0x081484E4 | Hook fires? | Should fire (blocks on semaphore) |

**Verified facts:**
- C1: ✓ HCOM shows "[1] GET http://postman-echo.com/..."
- C2: ✗ Hook at 0xC011704C NEVER fires
- C3: ✗ Hook at 0xC015CF40 NEVER fires
- C4-C10: ✗ None fire

**Conclusion:** The blockage is BETWEEN C1 and C2 — in the MANAGED code between
`HttpClient.GetAsync` and `GetHostByName_internal`. The managed code never reaches
the native DNS icall.

### Possible Managed-Level Blocking Points

1. **ServicePoint.HostEntry lock** (ServicePoint.cs:245) — `lock(hostE)` contention.
   If another thread holds this lock, the HTTP request blocks. Unlikely on first request.

2. **ServicePoint cache with null host** — If a previous DNS attempt set `host = null`
   (from the catch block at line 271), and `HasTimedOut` returns false, the cached null
   is returned without retrying DNS. CHECK: ServicePointManager.DnsRefreshTimeout.

3. **Socket.FamilyHint** throws — The `Socket.FamilyHint` property at Dns.cs:439 is
   passed to GetHostByName_internal. If this property throws (e.g., accessing network
   config), the exception propagates before the icall.

4. **HttpWebRequest.GetResponseAsync blocks before DNS** — The request might block
   on connection pool, proxy resolution, or other pre-DNS checks.

5. **MONO_ENTER_GC_SAFE deadlock** — mono_get_address_info wraps getaddrinfo in
   GC_SAFE region. If Mono's GC is stuck (e.g., waiting for another thread), the
   GC transition blocks indefinitely.

6. **Managed Socket constructor** — HttpWebRequest might create a managed Socket
   before DNS. If `new Socket(AF_INET, SOCK_STREAM, TCP)` blocks (goes through
   usrsock → ESPCP → blocks on semaphore), the request never reaches DNS.

### Next Steps

1. **Verify C2 with startup hooks** — Add hooks to the .resc file (not dynamically)
   for `ves_icall_System_Net_Dns_GetHostByName_internal` at 0xC011704C. Also hook
   `Socket.FamilyHint` icall if identifiable.

2. **Check #6 (managed Socket before DNS)** — Set hooks on ALL usrsock socket operations
   at startup. If `espcp_usrsock_socket` fires AFTER the HTTP request, a managed TCP
   socket is being created before DNS → blocks on ESPCP response delivery.

3. **Check #4 (HttpWebRequest blocking)** — Look at Mono source for what
   `HttpWebRequest.GetResponseAsync` does before ServicePoint.HostEntry. Trace the
   managed code path to find the exact blocking point.

4. **Build app with Console.WriteLine** — Add debug output to the Meadow app at each
   step of the HTTP request to narrow down where it blocks in managed code.
