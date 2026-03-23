# Specification: HCOM Bring-Up over Emulated UART

## Track ID
`renode-hcom`

## Problem Statement
HCOM (Host Communication) is the primary interface between the Meadow device and host tools. On real hardware, HCOM uses USB CDC-ACM (`/dev/ttyACM0`), but Renode cannot emulate USB device controllers. HCOM needs to be redirected to a UART for emulation.

## Goal
Get HCOM protocol fully functional over an emulated UART in Renode, enabling:
1. Host-to-device communication via socket (replacing USB CDC-ACM)
2. Command/response cycle validation
3. File transfer operations (to QSPI flash)
4. Foundation for CI-based protocol testing

## Dependencies
- **`renode-platform`** track must complete Phase 1B.5 (QSPI flash + `/dev/nxupd` registration)
- HCOM requires `/dev/nxupd` to be registered during `board_late_initialize()`

## Architecture

### Transport Redirect
- **Real hardware:** HCOM opens `CONFIG_HCOM_COMMS_DEVICE_NAME` (default `/dev/ttyACM0` via USB CDC-ACM)
- **Emulation:** Redirect to `/dev/ttyS1` (USART2) via `meadow_os_emu` defconfig
- **Host side:** Renode exposes USART2 via `CreateServerSocketTerminal` — connect with `nc`, Python script, or test harness

### Dual UART Setup
- **USART1** — Console/syslog output (existing, working)
- **USART2** — HCOM transport (new, replaces USB CDC-ACM)

## Key Source Files
- `Meadow.OS/apps/system/hcom/hcom_startup_manager.c` — `hcom_main` entry, opens `/dev/nxupd` and comms device
- `Meadow.OS/Meadow.OS/hcom_nx/hcom_nx_startup_mgr.c` — `hcom_nx_setup_mgr()`, registers `/dev/nxupd`
- `Meadow.OS/nuttx/boards/arm/stm32f7/stm32f777zit6-meadow/configs/meadow_os_emu/defconfig` — emulator defconfig

## Success Criteria
- [ ] `hcom_main` initializes and listens on USART2
- [ ] Basic HCOM commands work via socket (e.g., device info query)
- [ ] File transfer to QSPI flash works
- [ ] No regressions in kernel boot or console output on USART1
