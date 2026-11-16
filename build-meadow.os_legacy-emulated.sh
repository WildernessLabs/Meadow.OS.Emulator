#!/bin/bash
set -e

SCRIPT_DIR="$( cd "$(dirname "$0")" ; pwd -P )"

# Resolve sibling-repo locations (MEADOW_EMULATOR_OS_DIR, _CLI_DIR, _ESP_DIR, _SAMPLES_DIR)
source "$SCRIPT_DIR/scripts/resolve-locations.sh" legacy

MEADOW_DIR="$MEADOW_EMULATOR_OS_DIR"
NUTTX_DIR="$MEADOW_DIR/nuttx"
BUILD_DIR="$SCRIPT_DIR/build/legacy"

# Validate Meadow sibling directory exists
if [ ! -d "$NUTTX_DIR" ]; then
    echo "ERROR: Meadow (legacy) not found at $MEADOW_DIR"
    echo "       Meadow.OS.Emulator must be a sibling directory to Meadow/"
    exit 1
fi

# Add emulator tools to PATH (includes kconfig-tweak shim, used by the
# delegated build.sh below if kconfig-tweak isn't installed globally)
export PATH="$SCRIPT_DIR/tools:$PATH"

# Toolchain setup — mirrors Meadow/build.sh. Needed here for the arm-none-eabi-*
# tools used in the artifact post-processing step below.
case "$(uname -s)" in
    Darwin)
        export PATH="$MEADOW_DIR/toolchain/macos:$PATH"
        # Ensure ld.lld is available
        if [ ! -f "$MEADOW_DIR/toolchain/macos/ld.lld" ]; then
            echo "ERROR: ld.lld not found in $MEADOW_DIR/toolchain/macos"
            exit 1
        fi
        # ARM GCC 10.3 from /Applications/ARM (legacy NuttX requires ~10.x)
        if [ -d "/Applications/ARM/bin" ]; then
            export PATH="/Applications/ARM/bin:$PATH"
        fi
        ;;
    Linux)
        export PATH="$MEADOW_DIR/toolchain/linux:$PATH"
        ;;
esac

# Check for ARM toolchain
if ! command -v arm-none-eabi-gcc &> /dev/null; then
    echo "ERROR: arm-none-eabi-gcc not found"
    echo "       Install ARM GCC 10.3: https://developer.arm.com/downloads/-/gnu-rm"
    exit 1
fi

# Show which toolchain we're using
echo "Using: $(arm-none-eabi-gcc --version | head -1)"
echo "Linker: $(which ld.lld) — $(ld.lld --version 2>&1 | head -1)"

echo "=== Building Legacy Meadow OS for Renode ==="

# -------------------------------------------------------------------------
# Delegate configure/kconfig-tweaks/version-injection/build to Legacy's own
# build.sh --emulator (see UseRepositorBuildScripts.md Part 5). It reuses the
# existing mono defconfig as-is — no reconfigure, no defconfig selection —
# builds Mono itself, and produces nuttx.elf/nuttx_user.elf/nuttx.bin/
# nuttx_user.bin in $NUTTX_DIR exactly as before.
# -------------------------------------------------------------------------
if ! grep -q -- '--emulator' "$MEADOW_DIR/build.sh"; then
    echo "ERROR: $MEADOW_DIR/build.sh does not support --emulator."
    echo "       Update your Legacy checkout to its SupportEmulator branch (or later, once merged)."
    exit 1
fi

echo "--- Delegating build to $MEADOW_DIR/build.sh --emulator ---"
(cd "$MEADOW_DIR" && bash build.sh --emulator)

# -------------------------------------------------------------------------
# Copy artifacts
# -------------------------------------------------------------------------
mkdir -p "$BUILD_DIR"

# Legacy memory layout: vectors at 0x08040000 (uflash), kernel text at 0x08140000 (kflash),
# user code at 0x08040200+ (uflash after vector table). A single nuttx.bin would zero-fill
# the gap between vectors and kernel text, overwriting user code. Split into separate pieces.

# 1. Kernel vectors (interrupt vector table, ~504 bytes at 0x08040000)
arm-none-eabi-objcopy --only-section=.vectors -O binary \
    "$NUTTX_DIR/nuttx.elf" "$BUILD_DIR/nuttx_vectors.bin"
echo "--- Created nuttx_vectors.bin (vector table)"

# 2. Kernel text + data (at 0x08140000+, loaded separately)
arm-none-eabi-objcopy --remove-section=.vectors --remove-section=.comment \
    --remove-section=.ARM.attributes -O binary \
    "$NUTTX_DIR/nuttx.elf" "$BUILD_DIR/nuttx_kernel.bin"
echo "--- Created nuttx_kernel.bin (kernel code)"

# 3. User binary — strip Mono/SDRAM sections (at 0xC0000000) to avoid 3GB zero fill
arm-none-eabi-objcopy -O binary \
    --remove-section=.mono_signature \
    --remove-section=.mono \
    --remove-section=.mono_data \
    --remove-section=.mono_comment \
    --remove-section=.mono_bss \
    "$NUTTX_DIR/nuttx_user.elf" "$BUILD_DIR/nuttx_user.bin"
echo "--- Created nuttx_user.bin (mono sections stripped)"

# 3a. Extract Mono runtime sections for SDRAM loading (0xC0000000)
# These sections contain the Mono JIT/interpreter runtime linked at SDRAM addresses.
# Renode loads them separately into the emulated SDRAM region.
#
# Note: objcopy -O binary silently drops .mono_signature (0xC0000000) when extracting
# multiple sections — the output starts at .mono (0xC0000020). We extract the signature
# separately using dd from the ELF and prepend it to create a complete binary.
if arm-none-eabi-objdump -h "$NUTTX_DIR/nuttx_user.elf" | grep -q '\.mono '; then
    # Extract code sections (starts at 0xC0000020)
    arm-none-eabi-objcopy -O binary \
        --only-section=.mono \
        --only-section=.mono_data \
        --only-section=.mono_comment \
        "$NUTTX_DIR/nuttx_user.elf" "$BUILD_DIR/nuttx_mono_code.bin"

    # Extract .mono_signature (30 bytes at 0xC0000000) directly from ELF
    SIG_OFFSET=$(arm-none-eabi-readelf -S "$NUTTX_DIR/nuttx_user.elf" | \
        awk '/\.mono_signature/ {print "0x" $5}')
    SIG_SIZE=$(arm-none-eabi-readelf -S "$NUTTX_DIR/nuttx_user.elf" | \
        awk '/\.mono_signature/ {print "0x" $6}')
    python3 -c "
import sys
with open('$NUTTX_DIR/nuttx_user.elf', 'rb') as f:
    f.seek($SIG_OFFSET)
    sig = f.read($SIG_SIZE)
    # Pad to 32 bytes (0x20) to align with .mono section start
    sig = sig.ljust(0x20, b'\x00')
    with open('$BUILD_DIR/nuttx_mono_sig.bin', 'wb') as out:
        out.write(sig)
print(f'Signature: {sig[:4].hex()} version={int.from_bytes(sig[16:20], \"little\")}.{int.from_bytes(sig[12:16], \"little\")}.{int.from_bytes(sig[8:12], \"little\")}.{int.from_bytes(sig[4:8], \"little\")}')
"

    # Combine: signature (32 bytes) + code = complete SDRAM binary starting at 0xC0000000
    cat "$BUILD_DIR/nuttx_mono_sig.bin" "$BUILD_DIR/nuttx_mono_code.bin" > "$BUILD_DIR/nuttx_mono.bin"
    rm -f "$BUILD_DIR/nuttx_mono_code.bin" "$BUILD_DIR/nuttx_mono_sig.bin"
    echo "--- Created nuttx_mono.bin (Mono runtime for SDRAM at 0xC0000000, $(du -h "$BUILD_DIR/nuttx_mono.bin" | cut -f1))"
else
    echo "--- WARNING: No .mono sections found in nuttx_user.elf"
fi

# 4. Also keep the full nuttx.bin for reference (production-style single binary)
cp "$NUTTX_DIR/nuttx.bin" "$BUILD_DIR/nuttx.bin"

# 5. Copy ELFs for GDB debugging
cp "$NUTTX_DIR/nuttx.elf" "$BUILD_DIR/nuttx.elf"
cp "$NUTTX_DIR/nuttx_user.elf" "$BUILD_DIR/nuttx_user.elf"
echo "--- Copied ELFs for debugging"

# 6. Extract hook addresses from ELF files for Renode .resc scripts
echo "--- Extracting hook addresses for Renode ---"
python3 "$SCRIPT_DIR/tools/extract-hook-addresses.py" \
    "$BUILD_DIR/nuttx.elf" "$BUILD_DIR/nuttx_user.elf"

echo "=== Legacy build complete ==="
ls -lh "$BUILD_DIR/"
