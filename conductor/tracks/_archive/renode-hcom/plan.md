# Plan: HCOM Bring-Up over Emulated UART

## Track ID: `renode-hcom`
## Status: `complete`
## Repo: `Meadow.OS.Emulator` (sibling to `Meadow.OS/`)

---

## 1 — Update Emulation Defconfig (`meadow_os_emu`) ✅ COMPLETE
- [x] Disable USB CDC-ACM: `CONFIG_CDCACM`, `CONFIG_USBDEV`, `CONFIG_STM32F7_OTGFS` disabled
- [x] Redirect HCOM to UART4: `CONFIG_HCOM_COMMS_DEVICE_NAME="/dev/ttyS1"`
- [x] Enable UART4: `CONFIG_STM32F7_UART4=y`, serial driver configs (115200/8N1, 512B buffers)
- [x] UART4 pin definitions already in board.h (PI9/PH13)
- [x] All PROTECTED mode settings identical to `meadow_os`
- [x] Build succeeds; `build-emulator.sh` defaults to `meadow_os_emu`

## 2 — HCOM Serial Configuration in Renode ✅ COMPLETE
- [x] Expose HCOM UART via socket: `emulation CreateServerSocketTerminal 4242 "hcom" false`
- [x] Connect UART4 to socket terminal: `connector Connect sysbus.uart4 hcom`
- [x] USART1 stays as console/syslog output
- [x] All four .resc scripts updated (meadow-os, headless, gdb, diag)

## 3 — HCOM Protocol Testing ✅ COMPLETE
- [x] `hcom_main` initializes and opens `/dev/ttyS1` (UART4 CR1=0x2D confirms init)
- [x] `tools/hcom_test.py` test tool created with COBS encode/decode, HCOM protocol
- [x] GET_DEVICE_INFORMATION returns full device info (Product, Model, OSVersion, etc.)
- [x] GET_DEVICE_NAME returns "MeadowF7"
- [x] Command/response cycle works correctly over socket

## 4 — GDB Debugging Workflow ✅ COMPLETE
- [x] GDB server works via `machine StartGdbServer 3333` (meadow-os-gdb.resc)
- [x] `arm-none-eabi-gdb build/nuttx.elf -ex "target remote :3333"` connects successfully
- [x] Breakpoints, stepi, backtrace, register inspection all work
- [x] Memory reads (`x/Nxw addr`) work correctly
- [x] NuttX data structures readable (e.g., `(struct tcb_s *)g_readytorun.head`)
- [x] UART output captured to `/tmp/renode-uart.txt` for monitoring
- [x] Documented in README.md

## 5 — Additional Workarounds ✅ COMPLETE
- [x] RCC pydev updated: LSION→LSIRDY (CSR offset 0x74), LSEON→LSERDY (BDCR offset 0x70)
- [x] LSI clock calibration thread skipped (hook at 0x0800e07c) — Timer 5 capture not emulated

---

## Decision Log

| Date       | Decision | Rationale |
|------------|----------|-----------|
| 2026-03-08 | HCOM needs emulation-specific defconfig | HCOM uses USB CDC-ACM (`/dev/ttyACM0`) which Renode can't emulate. Create `meadow_os_emu` defconfig that redirects HCOM to a UART. |
| 2026-03-08 | UART4 for HCOM transport | USART1 is console/syslog. UART4 already has pin definitions in board.h (PI9/PH13) and maps to `/dev/ttyS1` when only USART1+UART4 are enabled. |
| 2026-03-08 | Port 4242 for HCOM socket | Arbitrary port choice; easy to remember. Configurable in .resc scripts. |
| 2026-03-08 | Skip LSI calibration thread | `pwrmgmt_lsi_calc_prep_thread_func` polls Timer 5 capture/compare for LSI clock measurement. Renode doesn't emulate TIM5 input capture, causing infinite busy-wait. |
