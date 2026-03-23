# Spec: Meadow.CLI Emulator Connection

## Problem

Meadow.CLI communicates with Meadow devices via HCOM protocol over USB CDC-ACM serial ports. The Renode emulator exposes HCOM over a raw TCP socket (UART4 on port 4242). The CLI cannot connect to the emulator because:

1. `SerialConnection` validates ports against `SerialPort.GetPortNames()` — rejects non-OS serial paths
2. `TcpConnection` uses HTTP (`/api/info` endpoints) — wrong protocol for raw HCOM/COBS
3. No connection type exists for raw HCOM over TCP sockets

## Solution

Add a `SocketConnection` class to `Meadow.HCom` that speaks raw HCOM/COBS over a TCP socket, using the same protocol the firmware speaks on UART. This is the same wire format as `SerialConnection` but over `TcpClient`/`NetworkStream` instead of `SerialPort`.

## Architecture

### Connection Type

`SocketConnection` inherits from `ConnectionBase` and follows the `SerialConnection` pattern:

- **Transport**: `TcpClient` + `NetworkStream` (replaces `SerialPort`)
- **Receive thread**: Reads from `NetworkStream`, accumulates in `CircularBuffer<byte>`, splits on 0x00 delimiter, COBS-decodes, dispatches via `SerialResponse.Parse()`
- **Send thread**: Dequeues from `ConcurrentQueue<IRequest>`, COBS-encodes, writes to `NetworkStream`
- **COBS framing**: Identical to SerialConnection — `[0x00][COBS-data][0x00]` on send; split on 0x00 on receive

### Route Format

`socket://<host>:<port>` (e.g., `socket://localhost:4242`)

Detected in `MeadowConnectionManager.GetConnection()` before the IP address check.

### CLI Usage

```bash
meadow device info -s socket://localhost:4242
meadow config route socket://localhost:4242   # persist for session
```

## Key Files

| File | Change |
|------|--------|
| `Meadow.HCom/Connections/SocketConnection.cs` | New — TCP socket transport |
| `Meadow.HCom/Connections/SocketConnection.ListenerProc.cs` | New — receive thread (partial class) |
| `Meadow.Tooling.Core/Connection/MeadowConnectionManager.cs` | Modified — add `socket://` route parsing |

## Constraints

- No changes to HCOM protocol or COBS encoding
- No changes to SerialConnection (existing USB workflow unaffected)
- SocketConnection must implement all IMeadowConnection methods that SerialConnection implements
- Methods not applicable to TCP (e.g., baud rate) can be no-ops
