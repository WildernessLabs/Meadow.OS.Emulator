# Plan: Meadow.CLI Emulator Connection

## Track ID: `meadow-cli-emu`
## Status: `ready`
## Repo: `Meadow.CLI`

---

## 1 — SocketConnection Core
- [ ] Create `SocketConnection.cs` inheriting from `ConnectionBase`
- [ ] Constructor takes `host` and `port` parameters
- [ ] Implement `Open()` — create `TcpClient`, connect, get `NetworkStream`
- [ ] Implement `Close()` — dispose stream and client
- [ ] Implement `EncodeAndSendPacket()` — COBS encode + 0x00 delimiters, write to `NetworkStream`
- [ ] Implement `EnqueueRequest()` / CommandManager thread — same pattern as SerialConnection
- [ ] Implement `Name` property (e.g., `"socket://host:port"`)

## 2 — SocketConnection Receive Thread
- [ ] Create `SocketConnection.ListenerProc.cs` (partial class)
- [ ] Read from `NetworkStream` into `CircularBuffer<byte>`
- [ ] Split on 0x00 delimiter, COBS-decode with `CobsTools.CobsDecoding()`
- [ ] Parse with `SerialResponse.Parse()` and dispatch to handlers
- [ ] Port all response handlers from SerialConnection.ListenerProc (DeviceInfo, TextInfo, FileRead/Write, etc.)
- [ ] Handle `IOException` / `SocketException` with reconnect logic

## 3 — IMeadowConnection Method Implementation
- [ ] Implement all device info methods (`GetDeviceInfo`, `GetDeviceName`, etc.)
- [ ] Implement file operations (`WriteFile`, `ReadFileString`, `DeleteFile`, `GetFileList`)
- [ ] Implement runtime control (`RuntimeEnable/Disable`, `ResetDevice`)
- [ ] Implement trace/debug methods
- [ ] Implement `Attach()` / `Detach()` / `WaitForMeadowAttach()`
- [ ] No-op or throw for inapplicable methods (e.g., ESP32-specific ops)

## 4 — Route Integration
- [ ] Update `MeadowConnectionManager.GetConnection()` to detect `socket://` prefix
- [ ] Parse `socket://host:port` into host and port
- [ ] Return `new SocketConnection(host, port, logger)` for socket routes
- [ ] Test with `-s socket://localhost:4242` flag
- [ ] Test with `meadow config route socket://localhost:4242`

## 5 — End-to-End Testing
- [ ] Start Renode with `meadow-os-headless.resc`
- [ ] Run `meadow device info -s socket://localhost:4242`
- [ ] Verify device info matches hcom_test.py output
- [ ] Test `meadow device list` with socket route
- [ ] Test file operations if applicable

---

## Implementation Notes

- `SocketConnection` should be ~80% copy of `SerialConnection` with transport swapped
- The COBS framing, response dispatch, request queuing, and threading model are identical
- Key difference: `NetworkStream.Read()` blocks until data available (vs SerialPort timeout)
- Reconnect logic should handle TCP-specific errors (ConnectionRefused, ConnectionReset)
- Consider: HCOM uses 0x00 as packet terminator, not leading+trailing delimiter pair (learned from Renode testing)

## Decision Log

| Date       | Decision | Rationale |
|------------|----------|-----------|
| 2026-03-09 | `socket://` URI scheme | Distinguishes from HTTP-based TcpConnection which uses `http://`. Clear intent. |
| 2026-03-09 | Separate class, not refactor SerialConnection | Keeps USB serial path untouched. Lower risk. |
