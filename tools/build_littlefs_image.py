#!/usr/bin/env python3
"""Build a LittleFS image containing Mono BCL assemblies for the Meadow emulator.

The firmware mounts LittleFS at /meadow0/ over the QSPI flash File System partition.
Mono expects to find mscorlib.dll and other BCL assemblies there.

LittleFS parameters must match what NuttX's lfs_vfs.c configures based on the
W25Q SPI flash MTD geometry:
  - MTD blocksize (page) = 256 bytes
  - MTD erasesize (sector) = 4096 bytes
  - CONFIG_FS_LITTLEFS_READ_SIZE_FACTOR = 4 (default)
  - CONFIG_FS_LITTLEFS_PROGRAM_SIZE_FACTOR = 4 (default)
  - CONFIG_FS_LITTLEFS_BLOCK_SIZE_FACTOR = 1 (default)

Usage:
  python3 build_littlefs_image.py <assembly_dir> <output.bin>
  python3 build_littlefs_image.py  # uses defaults
"""

import sys
import os
import littlefs

# LittleFS parameters matching NuttX's W25Q flash configuration.
# Verified by dumping lfs_config at runtime:
#   read_size=256, prog_size=256, block_size=4096, block_count=14336
BLOCK_SIZE = 4096
READ_SIZE = 256
PROG_SIZE = 256
BLOCK_COUNT = 14336

# Default assembly source directory
DEFAULT_ASM_DIR = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
    "build", "legacy", "assemblies"
)

DEFAULT_OUTPUT = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
    "build", "legacy", "littlefs.bin"
)


def build_image(asm_dir, output_path):
    """Create a LittleFS image containing all DLLs from asm_dir."""
    if not os.path.isdir(asm_dir):
        print(f"ERROR: Assembly directory not found: {asm_dir}")
        sys.exit(1)

    # Collect DLL files
    dlls = sorted(f for f in os.listdir(asm_dir) if f.lower().endswith('.dll'))
    if not dlls:
        print(f"ERROR: No .dll files found in {asm_dir}")
        sys.exit(1)

    total_size = sum(os.path.getsize(os.path.join(asm_dir, f)) for f in dlls)
    print(f"Found {len(dlls)} assemblies ({total_size / 1024:.0f} KB total)")

    # Create LittleFS filesystem
    # NuttX uses LittleFS v2.5 with disk_version 2.0 (0x00020000).
    # littlefs-python 0.17.1 defaults to disk_version 2.1 which NuttX can't mount.
    fs = littlefs.LittleFS(
        block_size=BLOCK_SIZE,
        block_count=BLOCK_COUNT,
        read_size=READ_SIZE,
        prog_size=PROG_SIZE,
        disk_version=0x00020000,
    )

    # Mono looks for BCL assemblies at $libdir/mono/4.5/ where $libdir is patched
    # from /usr/local/lib to /meadow0 in the .resc scripts. So assemblies go at /mono/4.5/.
    # Also put copies at the root for the firmware's assembly existence check.
    fs.makedirs('/mono/4.5', exist_ok=True)

    for dll in dlls:
        src = os.path.join(asm_dir, dll)
        size = os.path.getsize(src)
        with open(src, 'rb') as f:
            data = f.read()
        # Write to /mono/4.5/ for Mono runtime
        with fs.open(f'/mono/4.5/{dll}', 'wb') as f:
            f.write(data)
        # Also write to root for firmware's assembly check
        with fs.open(dll, 'wb') as f:
            f.write(data)
        print(f"  {dll}: {size} bytes")

    # Verify
    files = fs.listdir('/')
    print(f"\nFilesystem contents: {files}")

    # Extract raw image
    img = fs.context.buffer
    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    with open(output_path, 'wb') as f:
        f.write(img)

    print(f"\nWrote {len(img)} bytes to {output_path}")
    print(f"Block size: {BLOCK_SIZE}, Block count: {BLOCK_COUNT}")
    print(f"Load into QSPI at offset 0x00500000 (Renode address 0x90500000)")


if __name__ == '__main__':
    if len(sys.argv) >= 3:
        asm_dir = sys.argv[1]
        output = sys.argv[2]
    elif len(sys.argv) == 2:
        asm_dir = sys.argv[1]
        output = DEFAULT_OUTPUT
    else:
        asm_dir = DEFAULT_ASM_DIR
        output = DEFAULT_OUTPUT
    build_image(asm_dir, output)
