# Specification: Mono Runtime Bring-Up in Emulator

## Track ID
`renode-mono`

## Problem Statement
Meadow.OS runs .NET applications via the Mono runtime. The current emulator build disables Mono (`CONFIG_EXAMPLES_MONO=n`) and provides a stub `mono_main()`. Without Mono, no Meadow.NET application code can run, which means WiFi/network, BLE, GPIO, and all managed-code functionality is untestable.

## Goal
Get the Mono runtime running in the Renode emulator so Meadow.NET applications can execute. This unlocks:
1. End-to-end testing of Meadow.NET applications
2. WiFi/network operations (via usrsock → ESPCP)
3. BLE operations
4. GPIO/I2C/SPI from managed code
5. Application lifecycle testing

## Dependencies
- **`renode-legacy-os`** — Legacy OS boot (complete)
- **`renode-esp32`** — ESPCP stub (complete, needed for WiFi/network once Mono triggers it)
- Cross-compiled Mono runtime for ARM Cortex-M7 (from Meadow build system)
- Mono BCL (Base Class Library) assemblies for Meadow

## Key Questions
1. How is Mono built for Meadow? (cross-compilation toolchain, build scripts)
2. Where does Mono live in the memory map? (SDRAM at 0xC0000000?)
3. How are .NET assemblies deployed to the device? (QSPI flash? LittleFS?)
4. What hardware does Mono interact with directly? (FPU, memory protection, timers?)
5. Does Renode need SDRAM emulation for the Mono heap?
6. Are there Mono-specific NuttX syscalls or kernel interfaces?

## Architecture

```
┌─────────────────────────────────────────────┐
│ Meadow.OS (NuttX) on Renode                 │
│                                             │
│  Kernel space:                              │
│    NuttX kernel + drivers                   │
│    ESPCP manager                            │
│    HCOM manager                             │
│                                             │
│  User space:                                │
│    HCOM app (hcom_main)                     │
│    Mono runtime (mono_main) ← THIS TRACK    │
│      └─ Meadow.NET app (.dll)              │
│           └─ WiFi/BLE → usrsock → ESPCP    │
│           └─ GPIO/I2C/SPI → NuttX drivers  │
│                                             │
│  Memory:                                    │
│    0x08040000 — User flash                  │
│    0x08140000 — Kernel flash                │
│    0xC0000000 — SDRAM (Mono heap?)          │
│    0x90000000 — QSPI flash (assemblies?)    │
└─────────────────────────────────────────────┘
```

## Success Criteria
- [ ] Mono runtime initializes and prints version/status
- [ ] Simple Meadow.NET application loads and runs (e.g., Console.WriteLine)
- [ ] Managed-code GPIO operations work
- [ ] Application lifecycle (start, run, exception handling) works
- [ ] Foundation for WiFi/network testing (Mono triggers usrsock → ESPCP)
