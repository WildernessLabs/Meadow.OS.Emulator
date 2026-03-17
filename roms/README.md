# ESP32 ROM Binaries

The ESP32 contains a proprietary mask ROM at `0x40000000` that provides exception
vectors, window spill/fill handlers, and utility functions (ets_printf, cache/MMU
control, libgcc intrinsics, newlib, etc.). The ROM binary is required for full
ESP32 emulation in Renode.

## How to Obtain

### Option 1: Download from Espressif's QEMU Repository (Recommended)

Espressif distributes ROM binaries in their official QEMU fork:

```bash
# PRO CPU ROM (required)
curl -L -o roms/esp32-v3-rom.bin \
  https://github.com/espressif/qemu/raw/esp-develop/pc-bios/esp32-v3-rom.bin

# APP CPU ROM (only needed for dual-core emulation)
curl -L -o roms/esp32-v3-rom-app.bin \
  https://github.com/espressif/qemu/raw/esp-develop/pc-bios/esp32-v3-rom-app.bin
```

Source: https://github.com/espressif/qemu (branch: `esp-develop`, directory: `pc-bios/`)

### Option 2: Dump from Physical Hardware

If you have an ESP32 dev board, use esptool to dump the ROM:

```bash
# PRO CPU ROM (0x40000000, 776KB covers full ROM address space)
esptool.py --chip esp32 -b 921600 -p /dev/ttyUSB0 \
  dump_mem 0x40000000 0x000C2000 roms/esp32-v3-rom.bin

# Data ROM (0x3FF90000, 64KB)
esptool.py --chip esp32 -b 921600 -p /dev/ttyUSB0 \
  dump_mem 0x3FF90000 0x00010000 roms/esp32-rom1-data.bin
```

## Memory Mapping

| File                   | Load Address  | Size   | Description           |
|------------------------|---------------|--------|-----------------------|
| esp32-v3-rom.bin       | 0x40000000    | ~445KB | PRO CPU ROM (code)    |
| esp32-v3-rom-app.bin   | 0x40000000    | ~445KB | APP CPU ROM (code)    |
| esp32-rom1-data.bin    | 0x3FF90000    | 64KB   | ROM data segment      |

## Notes

- "v3" refers to ESP32 silicon revision 3 (the most common revision in production)
- The ROM binary is loaded into the `rom` MappedMemory region in `meadow-esp32.repl`
- These files are `.gitignore`d because they are proprietary Espressif binaries
