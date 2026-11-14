#!/bin/bash
set -e

SCRIPT_DIR="$( cd "$(dirname "$0")" ; pwd -P )"

# Load config for EMU_OS, EMU_DEFCONFIG, EMU_RUNTIME, etc.
source "$SCRIPT_DIR/scripts/read-config.sh" "$@" 2>/dev/null || true

# Resolve sibling-repo locations (MEADOW_EMULATOR_OS_DIR, _CLI_DIR, _ESP_DIR, _SAMPLES_DIR)
source "$SCRIPT_DIR/scripts/resolve-locations.sh" "$EMU_OS"

# -------------------------------------------------------------------------
# OS-dependent path setup
# -------------------------------------------------------------------------
MEADOW_SRC_DIR="$MEADOW_EMULATOR_OS_DIR"
NUTTX_DIR="$MEADOW_SRC_DIR/nuttx"

# Build output directory
if [ "$EMU_RUNTIME" = "legacy-os_modern-dotnet" ]; then
    BUILD_DIR="$SCRIPT_DIR/build/dotnet10"
elif [ "$EMU_OS" = "legacy" ]; then
    BUILD_DIR="$SCRIPT_DIR/build/legacy"
else
    BUILD_DIR="$SCRIPT_DIR/build"
fi

# Validate source directory exists
if [ ! -d "$NUTTX_DIR" ]; then
    echo "ERROR: NuttX source not found at $NUTTX_DIR"
    echo "       Meadow.OS.Emulator must be a sibling directory to $(basename "$MEADOW_SRC_DIR")/"
    exit 1
fi

# Add emulator tools to PATH (includes kconfig-tweak shim)
export PATH="$SCRIPT_DIR/tools:$PATH"

# -------------------------------------------------------------------------
# Toolchain setup
# -------------------------------------------------------------------------
if [ "$EMU_OS" = "legacy" ]; then
    # Legacy: ld.lld from Meadow/toolchain + ARM GCC 10.3
    case "$(uname -s)" in
        Darwin)
            export PATH="$MEADOW_SRC_DIR/toolchain/macos:$PATH"
            if [ -d "/Applications/ARM/bin" ]; then
                export PATH="/Applications/ARM/bin:$PATH"
            fi
            ;;
        Linux)
            export PATH="$MEADOW_SRC_DIR/toolchain/linux:$PATH"
            ;;
    esac
else
    # Meadow.OS: bundled toolchain
    if ! command -v arm-none-eabi-gcc &> /dev/null; then
        if [ -d "$MEADOW_SRC_DIR/toolchain/macos" ]; then
            export PATH="$MEADOW_SRC_DIR/toolchain/macos:$PATH"
        fi
    fi
fi

# Check for ARM toolchain
if ! command -v arm-none-eabi-gcc &> /dev/null; then
    echo "ERROR: arm-none-eabi-gcc not found"
    echo "       Install ARM GCC: https://developer.arm.com/downloads/-/gnu-rm"
    exit 1
fi

echo "Using: $(arm-none-eabi-gcc --version | head -1)"
if command -v ld.lld &> /dev/null; then
    echo "Linker: $(which ld.lld) — $(ld.lld --version 2>&1 | head -1)"
fi

# -------------------------------------------------------------------------
# Mono runtime validation
# -------------------------------------------------------------------------
# Legacy no longer needs a manual pre-check — Legacy/build.sh builds Mono
# itself as part of its normal flow.
MONO_MAKE_VARS=""

if [ "$EMU_OS" != "legacy" ]; then
    # Meadow.OS: optionally link .NET 10 mono from ../runtime/
    if [ "$EMU_RUNTIME" = "legacy-os_modern-dotnet" ]; then
        RUNTIME_DIR="$SCRIPT_DIR/../runtime"
        MONO_LIB="$RUNTIME_DIR/src/mono/build-nuttx-debug/mono/mini/libmonosgen-2.0.a"
        if [ ! -f "$MONO_LIB" ]; then
            echo "ERROR: .NET 10 Mono runtime library not found at:"
            echo "       $MONO_LIB"
            echo ""
            echo "       Build it first:"
            echo "         cd $RUNTIME_DIR/src/mono"
            echo "         ./build-nuttx.sh Debug"
            exit 1
        fi
        echo "--- .NET 10 Mono runtime: $MONO_LIB ($(du -h "$MONO_LIB" | cut -f1)) ---"
        MONO_MAKE_VARS="MONO_AOBJS=$MONO_LIB"
    fi
fi

echo "=== Building NuttX for Renode (os: $EMU_OS, runtime: ${EMU_RUNTIME:-default}) ==="

# -------------------------------------------------------------------------
# Delegate configure/kconfig-tweaks/version-injection/build to the OS repo's
# own build.sh --emulator (see UseRepositorBuildScripts.md Parts 3 & 5). It
# reuses the existing defconfig as-is — no reconfigure, no defconfig
# selection — and produces nuttx.elf/nuttx_user.elf/nuttx.bin/nuttx_user.bin
# in $NUTTX_DIR exactly as before. Legacy also builds Mono itself.
# -------------------------------------------------------------------------
if ! grep -q -- '--emulator' "$MEADOW_SRC_DIR/build.sh"; then
    echo "ERROR: $MEADOW_SRC_DIR/build.sh does not support --emulator."
    echo "       Update your checkout to the SupportEmulator branch (or later, once merged)."
    exit 1
fi

echo "--- Delegating build to $MEADOW_SRC_DIR/build.sh --emulator ---"
(cd "$MEADOW_SRC_DIR" && bash build.sh --emulator)

# -------------------------------------------------------------------------
# Copy artifacts
# -------------------------------------------------------------------------
mkdir -p "$BUILD_DIR"

# Legacy and Meadow.OS (Rebase) share the same memory layout, which requires
# split binaries: vectors at 0x08040000, kernel at 0x08140000, user at
# 0x08040200+.  A flat nuttx.bin zero-fills the 1MB gap between the vectors and
# the kernel and would overwrite the user code (KnownIssues_and_Errata.md 6.3).
{

    # 1. Kernel vectors (interrupt vector table)
    arm-none-eabi-objcopy --only-section=.vectors -O binary \
        "$NUTTX_DIR/nuttx.elf" "$BUILD_DIR/nuttx_vectors.bin"
    echo "--- Created nuttx_vectors.bin (vector table)"

    # 2. Kernel text + data (at 0x08140000+)
    arm-none-eabi-objcopy --remove-section=.vectors --remove-section=.comment \
        --remove-section=.ARM.attributes -O binary \
        "$NUTTX_DIR/nuttx.elf" "$BUILD_DIR/nuttx_kernel.bin"
    echo "--- Created nuttx_kernel.bin (kernel code)"

    # 3. User binary — strip Mono/SDRAM sections to avoid 3GB zero fill
    arm-none-eabi-objcopy -O binary \
        --remove-section=.mono_signature \
        --remove-section=.mono \
        --remove-section=.mono_data \
        --remove-section=.mono_comment \
        --remove-section=.mono_bss \
        "$NUTTX_DIR/nuttx_user.elf" "$BUILD_DIR/nuttx_user.bin"
    echo "--- Created nuttx_user.bin (mono sections stripped)"

    # 3a. Extract Mono runtime sections for SDRAM loading (0xC0000000)
    if arm-none-eabi-objdump -h "$NUTTX_DIR/nuttx_user.elf" | grep -q '\.mono '; then
        arm-none-eabi-objcopy -O binary \
            --only-section=.mono \
            --only-section=.mono_data \
            --only-section=.mono_comment \
            "$NUTTX_DIR/nuttx_user.elf" "$BUILD_DIR/nuttx_mono_code.bin"

        # Extract .mono_signature directly from ELF
        SIG_OFFSET=$(arm-none-eabi-readelf -S "$NUTTX_DIR/nuttx_user.elf" | \
            awk '/\.mono_signature/ {print "0x" $5}')
        SIG_SIZE=$(arm-none-eabi-readelf -S "$NUTTX_DIR/nuttx_user.elf" | \
            awk '/\.mono_signature/ {print "0x" $6}')
        python3 -c "
import sys
with open('$NUTTX_DIR/nuttx_user.elf', 'rb') as f:
    f.seek($SIG_OFFSET)
    sig = f.read($SIG_SIZE)
    sig = sig.ljust(0x20, b'\x00')
    with open('$BUILD_DIR/nuttx_mono_sig.bin', 'wb') as out:
        out.write(sig)
print(f'Signature: {sig[:4].hex()} version={int.from_bytes(sig[16:20], \"little\")}.{int.from_bytes(sig[12:16], \"little\")}.{int.from_bytes(sig[8:12], \"little\")}.{int.from_bytes(sig[4:8], \"little\")}')
"
        cat "$BUILD_DIR/nuttx_mono_sig.bin" "$BUILD_DIR/nuttx_mono_code.bin" > "$BUILD_DIR/nuttx_mono.bin"
        rm -f "$BUILD_DIR/nuttx_mono_code.bin" "$BUILD_DIR/nuttx_mono_sig.bin"
        echo "--- Created nuttx_mono.bin (Mono runtime for SDRAM, $(du -h "$BUILD_DIR/nuttx_mono.bin" | cut -f1))"
    else
        echo "--- WARNING: No .mono sections found in nuttx_user.elf"
    fi

    # Keep full nuttx.bin for reference
    cp "$NUTTX_DIR/nuttx.bin" "$BUILD_DIR/nuttx.bin"

}

# Copy ELFs for GDB debugging and hook-address extraction.  Always overwrite:
# the hook addresses are taken from these copies, so a stale one makes every
# hook land on the wrong address.  Prefer "nuttx" where the OS build makes one,
# otherwise "nuttx.elf" (Meadow.OS only produces nuttx.elf).
if [ -f "$NUTTX_DIR/nuttx" ]; then
    cp "$NUTTX_DIR/nuttx" "$BUILD_DIR/nuttx.elf"
    echo "--- Copied nuttx ELF to $BUILD_DIR/nuttx.elf"
else
    if [ -f "$NUTTX_DIR/nuttx.elf" ]; then
        cp "$NUTTX_DIR/nuttx.elf" "$BUILD_DIR/nuttx.elf"
        echo "--- Copied nuttx.elf to $BUILD_DIR/nuttx.elf"
    fi
fi
if [ -f "$NUTTX_DIR/nuttx_user.elf" ]; then
    cp "$NUTTX_DIR/nuttx_user.elf" "$BUILD_DIR/nuttx_user.elf"
    echo "--- Copied nuttx_user ELF to $BUILD_DIR/nuttx_user.elf"
fi

# Extract hook addresses from ELF files
if [ -f "$BUILD_DIR/nuttx.elf" ] && [ -f "$BUILD_DIR/nuttx_user.elf" ]; then
    echo "--- Extracting hook addresses for Renode ---"
    EMU_OS="$EMU_OS" python3 "$SCRIPT_DIR/tools/extract-hook-addresses.py" \
        "$BUILD_DIR/nuttx.elf" "$BUILD_DIR/nuttx_user.elf" || \
        echo "WARNING: Hook address extraction failed (non-fatal — new symbols may differ)"
fi

echo "=== Build complete (output: $BUILD_DIR/) ==="
ls -lh "$BUILD_DIR/"
