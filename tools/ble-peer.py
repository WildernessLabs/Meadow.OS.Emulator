#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Espressif Systems (Shanghai) CO LTD
# SPDX-FileCopyrightText: 2026 Wilderness Labs
#
# SPDX-License-Identifier: Apache-2.0
#
# Based on tools/bumble_test.py from espressif/esp-emulator.

"""
ble-peer.py: a Bluetooth LE peer (a "phone") for the ESP in esp-emu.

It starts a Bumble virtual controller on a TCP port for esp-emu
(--ble-hci tcp:localhost:PORT) and a second controller on the same virtual
link, used as a GATT client.  It scans until a connectable device advertises
(matching --name if given), connects, discovers the GATT services, reads every
readable characteristic, writes the writable ones and reads them back, then
disconnects and exits.  The scan has no fixed length, so the peer can wait
while the Meadow app is deployed.

Usage, with Bumble installed (pip3 install "bumble>=0.0.220,<0.1"):

    tools/ble-peer.py [--port 9544] [--name NAME] [--timeout SECONDS] &
    ESPEMU_BLE_HCI=tcp:localhost:9544 ./run.sh --esp-emu ...

Write values: 4-byte characteristics get the int32 42, 1-byte ones 0x01, and
any others the UTF-8 text "emulator".

Exit status: 0 if it connected and completed GATT discovery with no read or
write errors, 1 otherwise.
"""

import argparse
import asyncio
import logging
import sys


def patch_controller(Controller, hci):
    """Answer HCI commands Bumble's virtual controller does not implement.

    From Espressif's bumble_test.py: hosts send LL-privacy housekeeping and
    Read RSSI, which Bumble either rejects or, for unparsed commands, does not
    answer at all, so the host stalls waiting for the Command Complete.
    """

    def ack_ok(self, command):
        params = hci.HCI_GenericReturnParameters(data=bytes([0]))
        if isinstance(command, hci.HCI_SyncCommand):
            return params
        self.send_hci_packet(
            hci.HCI_Command_Complete_Event(
                num_hci_command_packets=1,
                command_opcode=command.op_code,
                return_parameters=params,
            )
        )
        return None

    for name in (
        "on_hci_le_set_privacy_mode_command",
        "on_hci_le_clear_resolving_list_command",
        "on_hci_le_add_device_to_resolving_list_command",
        "on_hci_le_remove_device_from_resolving_list_command",
    ):
        if not hasattr(Controller, name):
            setattr(Controller, name, ack_ok)

    def read_rssi(self, command):
        if hasattr(command, "connection_handle"):
            handle = command.connection_handle
        else:
            handle = int.from_bytes(command.parameters[:2], "little")
        data = bytes([0]) + handle.to_bytes(2, "little") + (-60 & 0xFF).to_bytes(1, "little")
        params = hci.HCI_GenericReturnParameters(data=data)
        if isinstance(command, hci.HCI_SyncCommand):
            return params
        self.send_hci_packet(
            hci.HCI_Command_Complete_Event(
                num_hci_command_packets=1,
                command_opcode=command.op_code,
                return_parameters=params,
            )
        )
        return None

    if not hasattr(Controller, "on_hci_read_rssi_command"):
        Controller.on_hci_read_rssi_command = read_rssi

    # Commands Bumble has no handler for go to on_hci_command, which returns a
    # status but, for commands it did not parse, sends no Command Complete, so
    # the host waits for ever.  Bluedroid sends Espressif vendor commands
    # (OGF 0x3F), for example OCF 0x109 while Meadow's GATT server starts:
    # answer those with success, as Espressif's controller would, and any other
    # unknown command with "unknown HCI command".
    unsupported = Controller.on_hci_command

    def on_hci_command(self, command):
        result = unsupported(self, command)
        if isinstance(command, hci.HCI_SyncCommand):
            return result
        status = 0x00 if (command.op_code >> 10) == 0x3F else 0x01
        self.send_hci_packet(
            hci.HCI_Command_Complete_Event(
                num_hci_command_packets=1,
                command_opcode=command.op_code,
                return_parameters=hci.HCI_GenericReturnParameters(data=bytes([status])),
            )
        )
        return None

    Controller.on_hci_command = on_hci_command


def write_value_for(current):
    if len(current) == 4:
        return (42).to_bytes(4, "little")
    if len(current) == 1:
        return b"\x01"
    return b"emulator"


async def run(args):
    try:
        from bumble import hci
        from bumble.controller import Controller
        from bumble.device import Device, Peer
        from bumble.host import Host
        from bumble.link import LocalLink
        from bumble.transport import open_transport
    except ImportError:
        print('ERROR: Bumble is not installed: pip3 install "bumble>=0.0.220,<0.1"')
        return 1

    patch_controller(Controller, hci)
    link = LocalLink()

    print(f"[*] Controller for esp-emu on tcp-server:_:{args.port}", flush=True)
    emu_transport = await open_transport(f"tcp-server:_:{args.port}")
    emu_controller = Controller(
        "emu-controller",
        host_source=emu_transport.source,
        host_sink=emu_transport.sink,
        link=link,
        public_address="00:11:22:33:44:55",
    )
    # LocalLink routes ACL data by the sender's random address; make it the
    # public one, which is the address the peer connects to.
    emu_controller.random_address = hci.Address("00:11:22:33:44:55", hci.Address.PUBLIC_DEVICE_ADDRESS)

    phone_controller = Controller("phone-controller", link=link, public_address="AA:BB:CC:DD:EE:FF")
    phone_host = Host()
    phone_host.controller = phone_controller
    phone_controller.host = phone_host
    phone = Device(name="ble-peer", host=phone_host)
    await phone.power_on()

    found = asyncio.get_running_loop().create_future()
    seen = set()

    def on_advertisement(adv):
        raw = adv.data.get(0x09) or adv.data.get(0x08) or b""
        name = raw.decode("utf-8", errors="replace") if isinstance(raw, bytes) else str(raw)
        key = (str(adv.address), name)
        if key not in seen:
            seen.add(key)
            print(f"[+] Advertising: {adv.address} name='{name}' connectable={adv.is_connectable} data={bytes(adv.data).hex()}", flush=True)
        if adv.is_connectable and (args.name is None or args.name in name) and not found.done():
            found.set_result(adv)

    phone.on("advertisement", on_advertisement)
    await phone.start_scanning()
    print(f"[*] Scanning (up to {args.timeout} s)...", flush=True)
    try:
        target = await asyncio.wait_for(found, timeout=args.timeout)
    except asyncio.TimeoutError:
        print("[-] No matching device advertised", flush=True)
        return 1
    await phone.stop_scanning()

    errors = 0
    print(f"[*] Connecting to {target.address}...", flush=True)
    connection = await phone.connect(target.address, timeout=30)
    print("[+] Connected", flush=True)
    peer = Peer(connection)
    await peer.discover_services()
    for service in peer.services:
        print(f"  Service {service.uuid}", flush=True)
        await service.discover_characteristics()
        for char in service.characteristics:
            props = [n for bit, n in ((0x02, "READ"), (0x08, "WRITE"), (0x10, "NOTIFY")) if char.properties & bit]
            print(f"    Characteristic {char.uuid} [{', '.join(props)}]", flush=True)
            current = b""
            if char.properties & 0x02:
                try:
                    current = bytes(await char.read_value())
                    print(f"      read: {current.hex()}", flush=True)
                except Exception as e:  # noqa: BLE001 - report and carry on
                    errors += 1
                    print(f"      read error: {e}", flush=True)
            if char.properties & 0x08:
                value = write_value_for(current)
                try:
                    await char.write_value(value, with_response=True)
                    print(f"      wrote: {value.hex()}", flush=True)
                    if char.properties & 0x02:
                        back = bytes(await char.read_value())
                        print(f"      read back: {back.hex()}", flush=True)
                except Exception as e:  # noqa: BLE001
                    errors += 1
                    print(f"      write error: {e}", flush=True)
    print(f"[+] GATT done, {errors} error(s)", flush=True)
    await asyncio.sleep(1)
    await connection.disconnect()
    print("[*] Disconnected", flush=True)
    return 0 if errors == 0 else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--port", type=int, default=9544, help="TCP port for esp-emu's --ble-hci (default 9544)")
    parser.add_argument("--name", help="connect only to a device whose advertised name contains NAME")
    parser.add_argument("--timeout", type=float, default=900, help="seconds to scan before giving up (default 900)")
    parser.add_argument("--verbose", action="store_true", help="Bumble debug logging")
    args = parser.parse_args()
    logging.basicConfig(level=logging.DEBUG if args.verbose else logging.WARNING)
    sys.exit(asyncio.run(run(args)))


if __name__ == "__main__":
    main()
