# Tech Stack

## Core OS
- **RTOS Kernel**: Apache NuttX (full source, heavily customized)
- **Languages**: C (kernel), C++ (some components)
- **Build System**: Make (NuttX), Bash scripts (orchestration), CMake (ESP32/IDF)
- **Configuration**: Kconfig (`defconfig` + `kconfig-tweak`)

## Toolchain
- `arm-none-eabi-gcc` (ARM Cortex-M7)
- `xtensa-esp32s3-elf-gcc` (ESP32-S3, via ESP-IDF)
- STM32 Cube IDE toolchain (bootloader)
- Platform-specific paths: `toolchain/{macos,linux,windows}`

## Hardware Abstraction
- **Primary MCU**: STM32F777ZIT6 — ARM Cortex-M7 @ ~216MHz
  - 384KB SRAM + 32MB external SDRAM (FMC)
  - 32MB QSPI Flash
  - MPU, DCACHE (write-through), ICACHE, DTCM
- **Coprocessor**: ESP32-S3 (WiFi/BLE)

## Build Artifacts
- `nuttx.bin` — Kernel binary
- `nuttx_user.bin` — Userspace binary
- `Meadow.OS.bin` — Combined bootloader + OS image
- `Meadow.OS.Update.bin` — OTA update binary (with CRC)
- `Meadow.OS.Runtime.bin` — Runtime binary

## Debugging
- OpenOCD + GDB (`arm-none-eabi-gdb`)
- ST-Link support
- Renode emulator (STM32F7 platform, Cortex-M7)

## Key Paths
- NuttX kernel: `nuttx/`
- Board config: `nuttx/boards/arm/stm32f7/stm32f777zit6-meadow/`
- Meadow OS layer: `Meadow.OS/`
- Applications: `apps/`
- Bootloader: `bootloader/`
- ESP32 coprocessor: `esp32/`
- Build scripts: `build.sh`, `build-bootloader.sh`, `build-esp32.sh`, `build-mbedtls.sh`
- Debug scripts: `debug.sh`, `debug.cfg`, `flash.sh`, `flash.cfg`
