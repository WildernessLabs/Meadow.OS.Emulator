#!/usr/bin/env python3
"""
HCOM protocol test tool for Meadow.OS emulator.

Connects to the Renode HCOM socket (UART4 on port 4242) and sends
basic HCOM commands. Used to verify HCOM bring-up over emulated UART.

Usage:
    python3 tools/hcom_test.py [command] [--port PORT]

Commands:
    info        Send GET_DEVICE_INFORMATION (default)
    name        Send GET_DEVICE_NAME
    listen      Just connect and print any data received
"""

import argparse
import socket
import struct
import sys
import time

# HCOM protocol constants
HCOM_PROTOCOL_VERSION = 0x0008
HCOM_SEQ_COMMAND = 0x0000
HCOM_COBS_DELIMITER = 0x00

# Request types (host → device)
HCOM_MDOW_REQUEST_GET_DEVICE_INFORMATION = 0x0112
HCOM_MDOW_REQUEST_GET_DEVICE_NAME = 0x011F

# Response types (device → host)
HCOM_HOST_REQUEST_TEXT_DEVICE_INFO = 0x030A
HCOM_HOST_REQUEST_TEXT_ACCEPTED = 0x0302
HCOM_HOST_REQUEST_TEXT_REJECTED = 0x0301
HCOM_HOST_REQUEST_TEXT_ERROR = 0x0304
HCOM_HOST_REQUEST_TEXT_TRACE_MSG = 0x030B

RESPONSE_NAMES = {
    0x0101: "UPLOAD_FILE_COMPLETED",
    0x0301: "TEXT_REJECTED",
    0x0302: "TEXT_ACCEPTED",
    0x0304: "TEXT_ERROR",
    0x030A: "TEXT_DEVICE_INFO",
    0x030B: "TEXT_TRACE_MSG",
}


def cobs_encode(data: bytes) -> bytes:
    """COBS encode a byte sequence (matching HCOM's encoder)."""
    output = bytearray()
    output.append(0)  # placeholder for first block length
    replace_idx = 0
    replacement = 1

    for b in data:
        if b == 0x00:
            output[replace_idx] = replacement
            replace_idx = len(output)
            output.append(0)  # placeholder
            replacement = 1
        else:
            output.append(b)
            replacement += 1
            if replacement == 0xFF:
                output[replace_idx] = 0xFF
                replace_idx = len(output)
                output.append(0)  # placeholder
                replacement = 1

    output[replace_idx] = replacement
    return bytes(output)


def cobs_decode(data: bytes) -> bytes:
    """COBS decode a byte sequence."""
    output = bytearray()
    i = 0
    while i < len(data):
        code = data[i]
        i += 1
        for _ in range(1, code):
            if i >= len(data):
                break
            output.append(data[i])
            i += 1
        if code < 0xFF and i < len(data):
            output.append(0)
    return bytes(output)


def build_header(request_type: int, extra_data: int = 0, user_data: int = 0) -> bytes:
    """Build a 12-byte HCOM protocol header (little-endian)."""
    return struct.pack('<HHHHI',
                       HCOM_SEQ_COMMAND,
                       HCOM_PROTOCOL_VERSION,
                       request_type,
                       extra_data,
                       user_data)


def parse_header(data: bytes):
    """Parse a 12-byte HCOM header. Returns (seq, version, rqstType, extra, userData)."""
    if len(data) < 12:
        return None
    return struct.unpack('<HHHHI', data[:12])


def build_packet(request_type: int, extra_data: int = 0, user_data: int = 0,
                 payload: bytes = b'') -> bytes:
    """Build a complete HCOM wire packet (COBS + 0x00 terminator)."""
    header = build_header(request_type, extra_data, user_data)
    message = header + payload
    encoded = cobs_encode(message)
    return encoded + bytes([HCOM_COBS_DELIMITER])


def receive_packets(sock: socket.socket, timeout: float = 5.0):
    """Receive and decode HCOM packets from socket. Yields (header, payload) tuples.

    HCOM uses 0x00 as a packet terminator (not a start/end framing pair).
    Each packet is: [COBS-encoded-data] [0x00].
    """
    sock.settimeout(timeout)
    buf = bytearray()

    try:
        while True:
            try:
                chunk = sock.recv(4096)
                if not chunk:
                    break
                buf.extend(chunk)
            except socket.timeout:
                break

            # Process complete packets (terminated by 0x00)
            while True:
                try:
                    end = buf.index(0x00)
                except ValueError:
                    break  # no complete packet yet

                # Extract COBS-encoded data before the delimiter
                encoded = bytes(buf[:end])
                buf = buf[end + 1:]

                if len(encoded) == 0:
                    continue

                decoded = cobs_decode(encoded)
                if len(decoded) >= 12:
                    header = parse_header(decoded)
                    payload = decoded[12:]
                    yield header, payload

    except socket.timeout:
        pass


def cmd_info(sock: socket.socket):
    """Send GET_DEVICE_INFORMATION and print response."""
    packet = build_packet(HCOM_MDOW_REQUEST_GET_DEVICE_INFORMATION)
    print(f"Sending GET_DEVICE_INFORMATION ({len(packet)} bytes on wire)")
    print(f"  Wire: {packet.hex()}")
    sock.sendall(packet)

    print("Waiting for response...")
    for header, payload in receive_packets(sock, timeout=5.0):
        seq, ver, rtype, extra, udata = header
        name = RESPONSE_NAMES.get(rtype, f"0x{rtype:04X}")
        print(f"  Response: type={name} seq={seq} ver={ver} extra={extra} userData=0x{udata:08X}")
        if payload:
            try:
                print(f"  Payload (text): {payload.decode('utf-8', errors='replace')}")
            except Exception:
                print(f"  Payload (hex): {payload.hex()}")


def cmd_name(sock: socket.socket):
    """Send GET_DEVICE_NAME and print response."""
    packet = build_packet(HCOM_MDOW_REQUEST_GET_DEVICE_NAME)
    print(f"Sending GET_DEVICE_NAME ({len(packet)} bytes on wire)")
    sock.sendall(packet)

    print("Waiting for response...")
    for header, payload in receive_packets(sock, timeout=5.0):
        seq, ver, rtype, extra, udata = header
        name = RESPONSE_NAMES.get(rtype, f"0x{rtype:04X}")
        print(f"  Response: type={name} seq={seq} ver={ver} extra={extra} userData=0x{udata:08X}")
        if payload:
            try:
                print(f"  Payload (text): {payload.decode('utf-8', errors='replace')}")
            except Exception:
                print(f"  Payload (hex): {payload.hex()}")


def cmd_listen(sock: socket.socket):
    """Listen for any unsolicited messages from the device."""
    print("Listening for messages (Ctrl+C to stop)...")
    try:
        for header, payload in receive_packets(sock, timeout=60.0):
            seq, ver, rtype, extra, udata = header
            name = RESPONSE_NAMES.get(rtype, f"0x{rtype:04X}")
            print(f"  Message: type={name} seq={seq} ver={ver} userData=0x{udata:08X}")
            if payload:
                try:
                    print(f"  Payload: {payload.decode('utf-8', errors='replace')}")
                except Exception:
                    print(f"  Payload (hex): {payload.hex()}")
    except KeyboardInterrupt:
        print("\nStopped.")


def main():
    parser = argparse.ArgumentParser(description='HCOM protocol test tool')
    parser.add_argument('command', nargs='?', default='info',
                        choices=['info', 'name', 'listen'],
                        help='Command to send (default: info)')
    parser.add_argument('--port', type=int, default=4242,
                        help='HCOM socket port (default: 4242)')
    parser.add_argument('--host', default='localhost',
                        help='HCOM socket host (default: localhost)')
    args = parser.parse_args()

    print(f"Connecting to {args.host}:{args.port}...")
    try:
        sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        sock.connect((args.host, args.port))
        print("Connected!")
    except ConnectionRefusedError:
        print(f"ERROR: Could not connect to {args.host}:{args.port}")
        print("       Is Renode running with the HCOM socket enabled?")
        sys.exit(1)

    try:
        commands = {
            'info': cmd_info,
            'name': cmd_name,
            'listen': cmd_listen,
        }
        commands[args.command](sock)
    finally:
        sock.close()
        print("Connection closed.")


if __name__ == '__main__':
    main()
