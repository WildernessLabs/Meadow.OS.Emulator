# Tracks

| Track ID           | Title                                    | Status      |
|-------------------|------------------------------------------|-------------|
| renode-platform   | Renode Platform Definition & Boot Bring-Up | `complete` (1A ✅, 1B ✅, 1B+ ✅, 1B.5 ✅ — full boot chain working through `/dev/nxupd`) |
| renode-hcom       | HCOM Bring-Up over Emulated UART          | `complete` (UART4→socket on port 4242, GET_DEVICE_INFO/NAME verified) |
| meadow-cli-emu    | Meadow.CLI Emulator Connection (SocketConnection) | `complete` (SocketConnection + socket:// route parsing, PR on feature/TCP_Socket_Connection_for_EMU) |
| renode-legacy-os  | Emulator Support for Production Meadow OS (Legacy NuttX) | `complete` (build, boot, HCOM, Meadow.CLI all verified) |
| renode-esp32      | ESP32 Coprocessor Emulation (SPI Stub)    | `complete` (EspcpStub.cs — boot + post-boot polling, all ESPCP interfaces stubbed) |
| renode-mono       | Mono Runtime Bring-Up in Emulator         | `in-progress` (Phase 2.2: Mono task starts, crashes in assembly path setup) |
| renode-esp32-dual | Dual-Machine ESP32 Emulation (Full Fidelity) | `planned` (real ESP32 firmware on Xtensa + SPI bridge) |
| renode-network    | Network Stack End-to-End Testing          | `planned` (bridge ESPCP socket ops to host; blocked on `renode-mono`) |
| renode-ci         | CI Integration for Emulated Testing       | `planned` |
| ux-improvements   | UX Improvements (setup.sh, README)        | `complete` (setup.sh checks/installs all prereqs, README updated with Quick Start) |
| renode-upstream   | Upstream Contributions to Renode          | `planned` (U.1–U.6 identified; U.6 reclassified as incomplete peripheral, not bug) |
