# Tracks

| Track ID           | Title                                    | Status      |
|-------------------|------------------------------------------|-------------|
| renode-platform   | Renode Platform Definition & Boot Bring-Up | `complete` (full boot chain working through `/dev/nxupd`) |
| renode-hcom       | HCOM Bring-Up over Emulated UART          | `complete` (UART4→socket on port 4242, GET_DEVICE_INFO/NAME verified) |
| meadow-cli-emu    | Meadow.CLI Emulator Connection (SocketConnection) | `complete` (SocketConnection + socket:// route parsing, PR on feature/TCP_Socket_Connection_for_EMU) |
| renode-legacy-os  | Emulator Support for Production Meadow OS (Legacy NuttX) | `complete` (build, boot, HCOM, Meadow.CLI all verified) |
| renode-esp32      | ESP32 Coprocessor Emulation (SPI Stub)    | `complete` (EspcpStub.cs — boot + post-boot polling, all ESPCP interfaces stubbed) |
| renode-mono       | Mono Runtime Bring-Up in Emulator         | `complete` (Mono JIT boots, compiles assemblies, QSPI/LFS working. BLOCKER: emulation speed — 0.00025x real-time for JIT workload, ~16h for full Mono boot) |
| renode-esp32-dual | Dual-Machine ESP32 Emulation (Full Fidelity) | `complete` (PR #2 merged. SpiBridge + ESP32_SPI, ESPCP bidirectional, CLI device info works. 15 tlib Xtensa fixes upstreamed to fork) |
| esp-emu-perf      | ESP32 Emulation Performance               | `complete` (Phases 1-3 done: 35 trace hooks removed, native CCOMPARE0 timer fixed, 8 hot-path hooks migrated to C#. IronPython hooks: 46→3. See [ESP_Emu_Perf.md](../ESP_Emu_Perf.md)) |
| renode-network    | Network Stack End-to-End Testing          | `planned` (bridge ESPCP socket ops to host) |
| renode-ci         | CI Integration for Emulated Testing       | `planned` |
| ux-improvements   | UX Improvements (setup.sh, README)        | `complete` (setup.sh checks/installs all prereqs, README updated with Quick Start) |
| renode-upstream   | Upstream Contributions to Renode          | `planned` (U.1–U.9 identified; 15 Xtensa tlib fixes on WildernessLabs fork) |
