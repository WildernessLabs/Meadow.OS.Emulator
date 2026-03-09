# Plan: HCOM Bring-Up over Emulated UART

## Track ID: `renode-hcom`
## Status: `blocked` (waiting on `renode-platform` Phase 1B.5)
## Repo: `Meadow.OS.Emulator` (sibling to `Meadow.OS/`)

---

## 1 — Update Emulation Defconfig (`meadow_os_emu`)
- [ ] Disable USB CDC-ACM: `CONFIG_CDCACM=n`, `CONFIG_USBDEV=n`
- [ ] Redirect HCOM to UART: `CONFIG_HCOM_COMMS_DEVICE_NAME="/dev/ttyS1"` (USART2)
- [ ] Enable USART2: `CONFIG_STM32F7_USART2=y`, add USART2 serial driver configs
- [ ] Add USART2 pin definitions to board.h if needed (PA2/PA3 or PD5/PD6)
- [ ] Keep all other PROTECTED mode settings identical to `meadow_os`
- [ ] Verify build succeeds

## 2 — HCOM Serial Configuration in Renode
- [ ] Expose HCOM UART via Renode socket: `emulation CreateServerSocketTerminal 1234 "hcom"`
- [ ] Connect USART2 to socket terminal in `.resc` script
- [ ] USART1 stays as console/syslog output

## 3 — HCOM Protocol Testing
- [ ] Verify `hcom_main` initializes and listens on the redirected UART
- [ ] Send basic HCOM commands via socket (e.g., using `nc` or a test script)
- [ ] Verify command/response cycle
- [ ] Test file transfer operations (uses QSPI flash from `renode-platform` Phase 1B.5)

## 4 — GDB Debugging Workflow ✅ COMPLETE
- [x] GDB server works via `machine StartGdbServer 3333` (meadow-os-gdb.resc)
- [x] `arm-none-eabi-gdb build/nuttx.elf -ex "target remote :3333"` connects successfully
- [x] Breakpoints, stepi, backtrace, register inspection all work
- [x] Memory reads (`x/Nxw addr`) work correctly
- [x] NuttX data structures readable (e.g., `(struct tcb_s *)g_readytorun.head`)
- [x] UART output captured to `/tmp/renode-uart.txt` for monitoring
- [x] Documented in README.md

---

## Decision Log

| Date       | Decision | Rationale |
|------------|----------|-----------|
| 2026-03-08 | HCOM needs emulation-specific defconfig | HCOM uses USB CDC-ACM (`/dev/ttyACM0`) which Renode can't emulate. Create `meadow_os_emu` defconfig that redirects HCOM to a UART. |
| 2026-03-08 | USART2 for HCOM transport | USART1 is already used for console/syslog. USART2 is the next available UART on the STM32F777. |
