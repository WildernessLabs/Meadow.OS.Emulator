# Replace the Renode Xtensa ESP32 machine with esp-emu

## Context

Dual-machine mode (`coprocessor: dual-machine`, `scripts/meadow-dual.resc`, Legacy only) runs
Meadow-ESP32's `MeadowComms.elf` on a second Renode machine with an Xtensa core. That needs a
hand-built ESP32 platform (`platforms/meadow-esp32.repl`, `ESP32_DPORT.cs`, `ESP32_RTC_CNTL.cs`,
`ESP32_TimerGroup.cs`, `ESP32_SPI.cs`), ROM `retw` hooks, `XtensaHotPathHooks.cs` and FreeRTOS
fix-ups. Even after the [ESP_Emu_Perf](ESP_Emu_Perf.md) work, every new IDF feature the firmware
uses is another peripheral or hook to write.

Espressif's **esp-emu** emulates the chips properly (radio, BLE, timers, SPI, UART, EN). Until
18 Nov 2026 it could not stand in here: its co-simulation socket only had the chip as an SPI
**master**, and Meadow's ESPCP needs the ESP as an SPI **slave**. **esp-emu v1.2.0
(20 Nov 2026, installed at `~/.local/bin/esp-emu`) removes that blocker:**

- `SpiMasterClaim` / `SpiMasterTransfer` / `SpiMasterResult` in `proto/esp_cosim.proto`: a
  consumer is the SPI master and the chip is a slave served by IDF's stock `spi_slave` driver,
  on all seven chips. Full duplex only (`spi_slave_hd` is not modelled).
- `TimeClaim` / `TimeGrant`: a consumer can hold the chip's clock, so Renode and esp-emu run in
  lockstep.
- `tools/renode/EspEmuSpiBridge.cs`: an official Renode `ISPIPeripheral` that does this over
  `--cosim-tcp`, with an optional `quantumUs` lockstep. Tested by Espressif with Renode 1.17
  and STM32F746 against ESP32-C6 only.

## What Meadow's ESP firmware needs (checked in `../Meadow-ESP32`, branch `ChangeLogging`)

| Need | Meadow-ESP32 | esp-emu 1.2.0 |
|---|---|---|
| Chip | `CONFIG_IDF_TARGET="esp32"` (classic) | **No classic ESP32.** C3, C5, C6, H2, P4, S3, S31. S3 is the nearest (Xtensa LX7, dual core, BLE + Wi-Fi) |
| SPI slave | `spi_slave_initialize(SPI3_HOST, …)`, full duplex, DMA | Supported (`spi_slave`) |
| SPI pins | MISO 19, MOSI 23, SCLK 18, CS 5 | Pads named by the claim; **GPIO 23 does not exist on S3** and 19/20 are its USB pins, so an S3 build needs a different pin map |
| ESP→STM32 signalling | No handshake GPIO. `+++RDY`, `+++MW`, `+++RST,n` text over a UART (`TransportProviderBase::SendControlMessageToStm32`), received on STM32 UART5 (`esp32ctrl`, port 4243 today) | UART claims (`UartTx`/`UartRx`) on the same cosim socket, or `--uart-tcp` / `--uart1-tcp` |
| CS interrupt | `spi_slave` post-setup callback; CS is a plain input | Bytes cross, not pad edges: a GPIO interrupt on CS does not fire unless the consumer also drives the pad via `GpioClaim.drive_pins` |
| Reset from STM32 | EN line | `EnClaim` / `EnDrive` |

So the bridge in `EspEmuSpiBridge.cs` does not fit as-is: Meadow has no handshake GPIO but does
have a control UART, and the STM32 side already bypasses SPI DMA (`EspcpSpiBypass.cs` calls the
bridge with whole buffers).

## Proposal

### 1. Meadow-ESP32: an ESP32-S3 build target (in `../Meadow-ESP32`)

- Add an `esp32s3` target / `sdkconfig.defaults.esp32s3` and a per-target pin map in
  `components/F7Services/Gpio.hpp` (`#if CONFIG_IDF_TARGET_ESP32S3`). Pick S3 pads that exist
  and are not USB/flash/PSRAM pins for MISO/MOSI/SCLK/CS and the logging UART.
- Build `merged_flash.bin` for esp-emu. The classic-ESP32 build for real boards is unchanged.
- Risk to check early: anything classic-only (BT controller config, `esp32.svd`-based code,
  IRAM/DRAM layout, `CONFIG_SPI_SLAVE_ISR_IN_IRAM`).

### 2. Emulator: a Meadow-specific bridge, `peripherals/EspEmuBridge.cs`

Start from Espressif's `EspEmuSpiBridge.cs` (Apache-2.0, keep the header) and change it to:

- **Expose the `SpiBridge` surface `EspcpSpiBypass` uses**, so a whole ESPCP buffer goes as one
  `SpiMasterTransfer` (one socket round trip per transfer, not one per byte). Keep the per-byte
  `Transmit` path for anything not bypassed.
- **Claim the control UART** in the same `Register` and present it to Renode as a UART
  (`IUART`) connected to STM32 `uart5`, replacing the `esp32ctrl` socket terminal. Fallback if
  that is awkward: esp-emu `--uart1-tcp` plus a small relay to Renode's port 4243.
- **Claim EN** and take an STM32 GPIO input for it, so OS-side ESP resets work.
- Drop the handshake GPIO (or make it optional) — Meadow signals over UART.
- Use `quantumUs` lockstep by default (start at 100 µs; measure).

### 3. Emulator wiring

- New `platforms/meadow-f7-espemu.repl`: `meadow-f7-dual.repl` with `EspEmuBridge @ spi2` in
  place of `SpiBridge`, CS from the STM32 GPIO wired to the bridge (the STM32 SPI model does
  not signal end of transfer), UART5 and the EN GPIO wired to it.
- New `scripts/meadow-espemu.resc`: the STM32 half of `meadow-dual.resc` only — no second
  Renode machine, no ESP32 peripherals or Xtensa hooks.
- `run.sh`: a `coprocessor: esp-emu` value (and `--esp-emu`) that starts
  `esp-emu --chip esp32s3 --firmware … --cosim-tcp 127.0.0.1:5590` first, waits for the port,
  then starts Renode, and stops esp-emu on exit. esp-emu's console goes to a log or port 4245
  as today. Firmware path via `MEADOW_EMULATOR_ESP_DIR` / `ESP32_FW_PATH` as now.
- `setup.sh`: check for `esp-emu` ≥ 1.2.0.

### 4. Verify

1. Espressif's own check first: `tools/renode/spi_slave_demo.py --chip esp32s3` against the
   WildernessLabs Renode fork, to confirm the bridge compiles there and S3 works (only C6 is
   tested upstream).
   **Done 2026-11-22** (esp-emu 1.2.0, fork `meadow-emulator-fixes` f90313d0, IDF 6.1.1
   `spi_slave/receiver` built for esp32s3, run through a `dotnet Renode.dll` wrapper):

   | Mode | Frame size | Result |
   |---|---|---|
   | free-running | 128 B ×3 | ok |
   | lockstep `--quantum-us 100` | 128 B ×3 | ok, clocks in step, 4.4 s |
   | lockstep `--quantum-us 100` | 4092 B ×3 | ok, clocks in step, 20.5 s |
   | free-running | 4092 B ×3 | **fails**: about 60 ms per byte, then esp-emu stops answering pieces (`did not answer SPI transfer 4612 within 5000 ms`) part-way through frame 1. A second run did not finish frame 0 in 120 s |

   So use lockstep. The free-running stall is worth reporting upstream (GH #67). The Meadow
   bridge's whole-buffer transfers avoid sending one piece per byte anyway.
2. Meadow-ESP32 S3 build boots in esp-emu alone; `+++RST` / `+++RDY` appear on its UART.

   **Done 2026-11-22**, branch `SupportEspEmu` (from `develop` c2236a4b) in the worktree
   `../Meadow-ESP32-EspEmu`, not committed. `./build.sh --emulator --build` builds
   `build-esp32s3/merged_flash.bin`. In `esp-emu --chip esp32s3` it prints `+++RST,1`,
   `+++RDY`, `+++SIR`, `+++MW` on UART0 and runs 30 s with no aborts, resets or errors. The
   ESP32 build and the `test/` build still build. Changes needed for the S3:

   - `Gpio.hpp`: an S3 pin map (UART0 TX 43 / RX 44; SPI MISO 13, MOSI 11, SCLK 12, CS 10).
   - `SpiTransportProvider.cpp`: `SPI_DMA_CH_AUTO` instead of channel 1 (GDMA chips reject a
     channel number; it aborted in `spi_slave_initialize`).
   - `SystemRequestHandler.cpp`: curve-fitting ADC calibration where line fitting is absent.
   - `sdkconfig.defaults.esp32s3`: BLE 4.2 (legacy advertising) features instead of 5.0.
   - Build: `Emulator-ESP-IDF 6.1` CMake preset (`build-esp32s3/`, own `sdkconfig`), own
     `dependencies.lock.esp32s3`, `build/config` includes now `${CMAKE_BINARY_DIR}/config`.
3. `./run.sh --legacy --esp-emu`: STM32 sees `+++RDY`, ESPCP frames flow both ways, Meadow.CLI
   `device info` reports the coprocessor version.

   **Done 2026-11-22**, emulator branch `SupportEspEmu`, not committed. With
   `configs/legacy-ccm.yaml` and `MEADOW_EMULATOR_ESP_DIR=../Meadow-ESP32-EspEmu`, `device
   info` reports `Coprocessor: 2.999.57.38` (the S3 build's version, which the OS only gets
   over ESPCP). 24 exchanges in about 45 s, none timed out. Lockstep runs at about 0.9× real
   time (51.8 s emulated in 57 s). The stub mode still boots. What it took:

   - `peripherals/EspEmuBridge.cs` (from Espressif's `EspEmuSpiBridge.cs`):
     - `TransferBuffer` sends one co-sim transfer per ESPCP buffer; `EspcpSpiBypass` uses it.
     - `AttachUart` joins esp-emu's `--uart-tcp` UART0 to STM32 UART5, so the OS's
       `espcp_uart_monitor` gets the real `+++` lines. A copy goes to `EspLogFile`.
     - It claims EN, holds it low from attach, and follows PF7, so the ESP boots when the OS
       releases it, with UART5 already listening.
   - Three problems found and fixed in the bridge:
     - **The ESP must say `+++SIR` before each exchange.** The shared Legacy hooks make
       `espcp_spi_interface_lock` a no-op (the stub sends no `+++SIR`), so the STM32 clocked
       the ESP before it had loaded a transaction and read back its own previous packet. The
       bridge now waits for `+++SIR` (not `+++RDY`, which comes before anything is loaded)
       since the last transfer.
     - **A transfer can wait on the ESP's clock.** A transfer starts when the previous one
       ends on the ESP's clock. With the clock held, the ESP can be stopped at its quantum
       boundary while the STM32 waits inside a CPU hook and grants nothing. So while waiting,
       the bridge grants the ESP another quantum (`ExtraGrants` counts them, about 40 a boot).
     - **Unstamped messages.** Transfers and EN changes go unstamped (`sim_time_ns` 0, start at
       once) rather than stamped with the ESP time last heard, which can be ahead of the new
       timeline after an EN restart.
   - `run.sh`: `--esp-emu` / `coprocessor: esp-emu`, Legacy only. It checks for esp-emu 1.2.0
     or later and for the firmware, starts esp-emu (log `build/legacy/esp-emu.log`), waits for
     both servers by its log, and stops it on exit.
   - Not done: `setup.sh` does not install esp-emu, and Rebase has no esp-emu script yet.
   - Found on the way: Meadow-ESP32's `--tracemessages` build does not compile on `develop`
     (`Encoders.cpp` lacks `SpiTransportProvider.hpp`; `AccessPoint.cpp` uses `MACSTR`).
4. A BLE app from Meadow samples (esp-emu serves BLE GATT on S3), then Wi-Fi via
   `--net user` if the OS path supports it.

   **Wi-Fi done 2026-11-22** (not committed). `run.sh` now starts esp-emu with
   `--net user,net=10.0.2.0/24` and its soft AP (`myssid` / `mypassword`; override with
   `ESPEMU_NET`, `ESPEMU_WIFI_SSID`, `ESPEMU_WIFI_PASSWORD`). A copy of Meadow.Samples'
   `WiFi_Basics` retargeted to net10.0 against Meadow.Core meadow-3.0 (SSID/password changed,
   one HTTP and one HTTPS GET instead of the loop), deployed with `meadow app run` on
   `configs/legacy-ccm.yaml`:

   - Scan lists `myssid`, -30 dBm, channel 1; `Connect` raises NetworkConnected.
   - `GET http://example.com/` returns the page; `GET https://postman-echo.com/get?...` returns
     its JSON echo (TLS through the ESP's sockets and esp-emu's NAT). About 50 s per request.
   - Bridge after the run: 1510 exchanges, 62 KB, 2409 extra grants, no SPI-ready timeouts.
   - Not emulator problems, as on a board with this ESP branch:
     - The NetworkConnected event carries IP `255.255.255.255`, and `NetworkInterface` shows
       wlan0 Down with MTU 0. The ESP raises it on `WIFI_EVENT_STA_CONNECTED`, before DHCP.
     - The ESP's `NetworkGotIpEvent` (48, added 52ce1102, 2024-08) is logged by Meadow.Core
       as `WiFi event 48 not implemented`.
     - One `ioctl Esp32Command returned -1` / `SendingCommand returned: Failure` during
       `Connect`, which still succeeds.

   **BLE done 2026-11-22** (not committed). A net10.0 copy of Meadow.Samples'
   `Bluetooth_Basics` (device name `MeadowEmu`), booted from an image (`make-lfs-image.sh
   --legacy`, `run.sh --mount`), with `tools/ble-peer.py` (Bumble) as the phone and
   `ESPEMU_BLE_HCI=tcp:localhost:9544`: the ESP advertises service 0x00FD, the peer connects,
   discovers the GATT table (Device Name reads `MeadowEmu`), reads all four characteristics and
   writes the three writable ones with no errors; the app logs each write (`On_Off: True`,
   `My Number` 42, `My Text` "emulator"). What it took:

   - `run.sh` passes the firmware ELF (`--elf`, `build-esp32s3/MeadowComms.elf`, or
     `ESPEMU_ELF`) so esp-emu can intercept the controller, and `--ble-hci $ESPEMU_BLE_HCI`
     when set; unset, esp-emu's built-in controller is used.
   - **Meadow-ESP32, S3 build only:** esp-emu replaces `esp_bt_controller_init/enable/
     disable/deinit` but not `esp_bt_controller_get_status`, so Bluedroid saw an idle
     controller and `esp_bluedroid_init` failed (`Controller not initialised`). IDF's own
     Bluedroid `gatt_server` example fails the same way on the S3 (esp-emu documents Bluedroid
     only for the S31). `main/EspEmuBtControllerStatus.c` keeps the status through linker
     `--wrap`s; with it `gatt_server` and Meadow both work. Worth reporting upstream.
   - **`tools/ble-peer.py`:** Bumble's virtual controller sends no Command Complete for
     commands it does not parse. Bluedroid sends an Espressif vendor command (OGF 0x3F,
     OCF 0x109) while Meadow's GATT server starts and waited on it for ever. The peer answers
     vendor commands with success and other unknown commands with "unknown command". It also
     scans with no time limit, so it can wait through a deploy.
   - Seen on the way, Meadow.Core, not emulator problems:
     - `UpdateDefinitionHandles` asks the ESP for the GATT handles a fixed 100 ms after
       `Start` (`Esp32Coprocessor_IBlutoothAdapter.cs`, "yes. this is ugly."). While the
       stack was stalled the answer was empty or short, and the app faulted with
       `An item with the same key has already been added. Key: 0` or `Index was outside the
       bounds of the array`. It can happen on a board whenever the stack is slow.
     - Reads after a write return the old value (zeros); the write reaches the app, which
       calls `SetValue`, but the read-back came first or the value is not pushed to the ESP.
       Not investigated.
     - The advertising data has no name (it carries flags, TX power, service 0x00FD and the
       connection interval); the name is readable from Generic Access.
5. Compare speed against today's dual-machine mode (0.57x) with `quantumUs` 100 / 1000 / 0.

### 5. Afterwards

Once the esp-emu path works for Legacy, add it for Rebase (no dual-machine mode exists for
Rebase today), then decide whether to retire `meadow-dual.resc`, `meadow-esp32.repl` and the
`ESP32_*.cs` / Xtensa hook files.

## Open questions

- Is an ESP32-S3 build of Meadow-ESP32 acceptable, given boards use a classic ESP32? The ESPCP
  protocol and services are the same; the pin map and target config differ.
- ~~Which UART carries `+++RDY`/`+++MW`?~~ UART0, GPIO 1 (TX) and 3 (RX), per `Gpio.hpp`.
  Neither pad is UART0 on the S3 (its UART0 is 43/44), so the S3 pin map must choose them.
- Does ESPCP rely on a CS-edge GPIO interrupt on the ESP side? If so the bridge must also drive
  the CS pad (`GpioClaim.drive_pins`).
- `EspEmuSpiBridge.cs` does not reconnect: esp-emu must start before Renode and stay up. A reset
  of the ESP should go through EN, not by restarting esp-emu.
