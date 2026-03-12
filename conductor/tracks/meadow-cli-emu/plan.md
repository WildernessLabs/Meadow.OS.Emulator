# Plan: Meadow.CLI Emulator Connection

## Track ID: `meadow-cli-emu`
## Status: `done` (SocketConnection working, deploy in progress under renode-mono)
## Repo: `Meadow.CLI`

---

## 1 — SocketConnection Core
- [x] Create `SocketConnection.cs` inheriting from `ConnectionBase`
- [x] Constructor takes `host` and `port` parameters
- [x] Implement `Open()` — create `TcpClient`, connect, get `NetworkStream`
- [x] Implement `Close()` — dispose stream and client
- [x] Implement `EncodeAndSendPacket()` — COBS encode + 0x00 delimiters, write to `NetworkStream`
- [x] Implement `EnqueueRequest()` / CommandManager thread — same pattern as SerialConnection
- [x] Implement `Name` property (e.g., `"socket://host:port"`)

## 2 — SocketConnection Receive Thread
- [x] Create `SocketConnection.ListenerProc.cs` (partial class)
- [x] Read from `NetworkStream` into `CircularBuffer<byte>`
- [x] Split on 0x00 delimiter, COBS-decode with `CobsTools.CobsDecoding()`
- [x] Parse with `SerialResponse.Parse()` and dispatch to handlers
- [x] Port all response handlers from SerialConnection.ListenerProc (DeviceInfo, TextInfo, FileRead/Write, etc.)
- [x] Handle `IOException` / `SocketException` with reconnect logic

## 3 — IMeadowConnection Method Implementation
- [x] Implement all device info methods (`GetDeviceInfo`, `GetDeviceName`, etc.)
- [x] Implement file operations (`WriteFile`, `ReadFileString`, `DeleteFile`, `GetFileList`)
- [x] Implement runtime control (`RuntimeEnable/Disable`, `ResetDevice`)
- [x] Implement `Attach()` / `Detach()` / `WaitForMeadowAttach()`
- [x] `ReconnectRequiredResponse` handler: close, delay, reopen, signal concluded

## 4 — Route Integration
- [x] Update `MeadowConnectionManager.GetConnection()` to detect `socket://` prefix
- [x] Parse `socket://host:port` into host and port
- [x] Return `new SocketConnection(host, port, logger)` for socket routes
- [x] Test with `-s socket://localhost:4242` flag
- [x] Test with `meadow config route socket://localhost:4242`

## 5 — End-to-End Testing
- [x] Start Renode with `meadow-legacy-headless.resc`
- [x] Run `meadow device info -s socket://localhost:4242` — verified device info (version 2.5.7.0)
- [x] Deploy command gets past MONO_DISABLE + reconnect
- [ ] Full deploy flow blocked by BBR persistence (tracked under `renode-mono` Phase 2.9)

---

## Implementation Notes

- Branch: `feature/TCP_Socket_Connection_for_EMU` in Meadow.CLI repo
- Published to `/tmp/meadow-cli/` via `dotnet publish`
- SocketConnection is ~80% copy of SerialConnection with transport swapped
- COBS framing, response dispatch, request queuing, threading model identical
- Key difference: `NetworkStream.Read()` blocks until data available (vs SerialPort timeout)
- HCOM uses 0x00 as packet terminator, not leading+trailing delimiter pair

## Decision Log

| Date       | Decision | Rationale |
|------------|----------|-----------|
| 2026-03-09 | `socket://` URI scheme | Distinguishes from HTTP-based TcpConnection which uses `http://`. Clear intent. |
| 2026-03-09 | Separate class, not refactor SerialConnection | Keeps USB serial path untouched. Lower risk. |
| 2026-03-11 | ReconnectRequiredResponse handler | CLI deploy sends MONO_DISABLE → device restarts → CLI must reconnect and signal concluded. |
