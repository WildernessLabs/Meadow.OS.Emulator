# Product Definition: Meadow.OS

## Overview
Meadow.OS is a POSIX-based embedded real-time operating system built on Apache NuttX, designed for the Wilderness Labs Meadow hardware platform. It enables .NET developers to build IoT applications on microcontrollers.

## Primary Hardware Targets
- **Meadow F7 Micro** — STM32F777ZIT6 (ARM Cortex-M7, ARMv7-M)
- **Meadow ESP32** — ESP32-S3 (Xtensa dual-core)

## Key Capabilities
- Protected build mode (kernel/user separation via `CONFIG_BUILD_PROTECTED`)
- HCOM protocol for host communication (USB CDC-ACM)
- QSPI flash (32MB W25QXXXJV)
- Ethernet (LAN8742A PHY), WiFi (via ESP32 coprocessor)
- FAT + LittleFS filesystem support
- ELF loading for user applications
- Extensive peripheral support: I2C, SPI, PWM, ADC, DAC, SDMMC, RTC, GPIO

## Users
- Wilderness Labs engineering team (OS development)
- .NET embedded developers (application development)

## Current Version
1.99.0 (active rebase onto updated NuttX)
