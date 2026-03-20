#!/bin/bash
set -e

SCRIPT_DIR="$( cd "$(dirname "$0")" ; pwd -P )"
MEADOW_OS_DIR="$SCRIPT_DIR/../Meadow.OS"
NUTTX_DIR="$MEADOW_OS_DIR/nuttx"
RUNTIME_DIR="$SCRIPT_DIR/../runtime"

# Load config for defconfig name and runtime selection
source "$SCRIPT_DIR/scripts/read-config.sh" "$@" 2>/dev/null || true
CONFIG="${1:-${EMU_DEFCONFIG:-meadow_os_emu}}"

# Select build output directory based on runtime
if [ "$EMU_RUNTIME" = "legacy-os_modern-dotnet" ]; then
    BUILD_DIR="$SCRIPT_DIR/build/dotnet10"
else
    BUILD_DIR="$SCRIPT_DIR/build"
fi

# Validate Meadow.OS sibling directory exists
if [ ! -d "$NUTTX_DIR" ]; then
    echo "ERROR: Meadow.OS not found at $MEADOW_OS_DIR"
    echo "       Meadow.OS.Emulator must be a sibling directory to Meadow.OS/"
    exit 1
fi

# Add emulator tools to PATH (includes kconfig-tweak shim)
export PATH="$SCRIPT_DIR/tools:$PATH"

# NuttX requires GNU Make 4.x+ (macOS ships 3.81)
if command -v gmake &> /dev/null; then
    MAKE=gmake
else
    MAKE=make
fi

# Check for ARM toolchain
if ! command -v arm-none-eabi-gcc &> /dev/null; then
    # Try Meadow.OS bundled toolchain
    if [ -d "$MEADOW_OS_DIR/toolchain/macos" ]; then
        export PATH="$MEADOW_OS_DIR/toolchain/macos:$PATH"
    else
        echo "ERROR: arm-none-eabi-gcc not found"
        echo "       Install with: brew install --cask gcc-arm-embedded"
        echo "       Or place toolchain in $MEADOW_OS_DIR/toolchain/macos/"
        exit 1
    fi
fi

# -------------------------------------------------------------------------
# .NET 10 Mono runtime setup
# -------------------------------------------------------------------------
MONO_MAKE_VARS=""
if [ "$EMU_RUNTIME" = "legacy-os_modern-dotnet" ]; then
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
    # Pass MONO_AOBJS on make command line — overrides any Makefile assignment.
    # The kernel/Makefile already references $(MONO_AOBJS) in the linker command.
    MONO_MAKE_VARS="MONO_AOBJS=$MONO_LIB"
fi

echo "=== Building NuttX for Renode (config: $CONFIG, runtime: ${EMU_RUNTIME:-none}) ==="

# Configure NuttX
echo "--- Configuring NuttX with stm32f777zit6-meadow:$CONFIG ---"
cd "$NUTTX_DIR"

# Clean previous build if .config exists with different config
if [ -f ".config" ]; then
    echo "--- Cleaning previous build ---"
    $MAKE distclean 2>/dev/null || true
fi

# configure.sh may exit non-zero due to a cosmetic Kconfig warning about
# ARCH_BOARD_CUSTOM — the .config is written successfully before that step.
./tools/configure.sh "stm32f777zit6-meadow:$CONFIG" || {
    if [ -f "$NUTTX_DIR/.config" ]; then
        echo "WARNING: configure.sh reported an error but .config was written — continuing"
    else
        echo "ERROR: configure.sh failed and no .config was produced"
        exit 1
    fi
}

# Apply emulator-specific config tweaks (only if not using the _emu defconfig,
# which already has these baked in)
NUTTX_CONFIG="$NUTTX_DIR/.config"

if [[ "$CONFIG" != *_emu* ]]; then
    echo "--- Applying emulator config tweaks ---"

    # Disable USB (Renode can't emulate USB device controllers)
    kconfig-tweak --file "$NUTTX_CONFIG" --disable CONFIG_CDCACM
    kconfig-tweak --file "$NUTTX_CONFIG" --disable CONFIG_BOARDCTL_USBDEVCTRL
    kconfig-tweak --file "$NUTTX_CONFIG" --disable CONFIG_STM32F7_OTGFS
    kconfig-tweak --file "$NUTTX_CONFIG" --disable CONFIG_USBDEV

    # Enable UART4 for HCOM (replaces USB CDC-ACM)
    kconfig-tweak --file "$NUTTX_CONFIG" --enable CONFIG_STM32F7_UART4
    kconfig-tweak --file "$NUTTX_CONFIG" --set-str CONFIG_HCOM_COMMS_DEVICE_NAME /dev/ttyS1
    kconfig-tweak --file "$NUTTX_CONFIG" --set-val CONFIG_UART4_BAUD 115200
    kconfig-tweak --file "$NUTTX_CONFIG" --set-val CONFIG_UART4_BITS 8
    kconfig-tweak --file "$NUTTX_CONFIG" --set-val CONFIG_UART4_PARITY 0
    kconfig-tweak --file "$NUTTX_CONFIG" --set-val CONFIG_UART4_2STOP 0
    kconfig-tweak --file "$NUTTX_CONFIG" --set-val CONFIG_UART4_RXBUFSIZE 512
    kconfig-tweak --file "$NUTTX_CONFIG" --set-val CONFIG_UART4_TXBUFSIZE 512

    # Route syslog to serial console instead of RAM log
    kconfig-tweak --file "$NUTTX_CONFIG" --enable CONFIG_DEV_CONSOLE
    kconfig-tweak --file "$NUTTX_CONFIG" --enable CONFIG_SYSLOG_SERIAL_CONSOLE
    kconfig-tweak --file "$NUTTX_CONFIG" --disable CONFIG_RAMLOG_SYSLOG

    # Prevent reset loops in emulator
    kconfig-tweak --file "$NUTTX_CONFIG" --set-val CONFIG_BOARD_RESET_ON_ASSERT 0

    # Resolve kconfig dependencies after tweaks
    $MAKE olddefconfig 2>/dev/null || true
else
    echo "--- Using pre-configured emulator defconfig (skipping kconfig tweaks) ---"
fi

# Inject version placeholders in hcom_nuttx_shared.h
# The production build.sh does this via version_methods.sh — we use defaults
SHARED_HEADER="$MEADOW_OS_DIR/Meadow.OS/include/meadow/hcom_nuttx_shared.h"
if grep -q '###VERSION_MAJOR###' "$SHARED_HEADER" 2>/dev/null; then
    echo "--- Injecting version placeholders ---"
    cp "$SHARED_HEADER" "$SHARED_HEADER.emubak"
    GIT_HASH=$(git -C "$MEADOW_OS_DIR" rev-parse --short HEAD 2>/dev/null || echo "00000000")
    sed -i.bak \
        -e 's/###VERSION_MAJOR###/0/g' \
        -e 's/###VERSION_MINOR###/0/g' \
        -e 's/###VERSION_REVISION###/0/g' \
        -e 's/###VERSION_BUILD###/0/g' \
        -e "s/###BUILD_DAY###/$((10#$(date -u +%d)))/g" \
        -e "s/###BUILD_TWO_DIGIT_DAY###/$(date -u +%d)/g" \
        -e "s/###BUILD_MONTH###/$((10#$(date -u +%m)))/g" \
        -e "s/###BUILD_TWO_DIGIT_MONTH###/$(date -u +%m)/g" \
        -e "s/###BUILD_MONTH_NAME###/$(date -u +%b)/g" \
        -e "s/###BUILD_YEAR###/$((10#$(date -u +%y)))/g" \
        -e "s/###BUILD_HOUR###/$((10#$(date -u +%H)))/g" \
        -e "s/###BUILD_TWO_DIGIT_HOUR###/$(date -u +%H)/g" \
        -e "s/###BUILD_MINUTE###/$((10#$(date -u +%M)))/g" \
        -e "s/###BUILD_TWO_DIGIT_MINUTE###/$(date -u +%M)/g" \
        -e "s/###BUILD_SECOND###/$((10#$(date -u +%S)))/g" \
        -e "s/###BUILD_TWO_DIGIT_SECOND###/$(date -u +%S)/g" \
        -e "s/###BUILD_HASH###/0x${GIT_HASH}/g" \
        -e "s/###BUILD_HASH_STRING###/${GIT_HASH}/g" \
        -e "s/###MEADOW_GIT_REF###/emulator/g" \
        -e "s/###BUILD_EPOCH_TIME###/$(date -u +%s)/g" \
        "$SHARED_HEADER"
    rm -f "$SHARED_HEADER.bak"
    RESTORE_SHARED_HEADER=true
fi

# Cleanup function to restore modified files
cleanup() {
    if [ "$RESTORE_SHARED_HEADER" = true ] && [ -f "$SHARED_HEADER.emubak" ]; then
        mv "$SHARED_HEADER.emubak" "$SHARED_HEADER"
    fi
}
trap cleanup EXIT

# Parallel job count
JOBS="-j$(sysctl -n hw.ncpu 2>/dev/null || echo 4)"

# Build
if [ "$CONFIG" = "nsh" ]; then
    # Flat mode — single binary
    echo "--- Building (FLAT mode) ---"
    $MAKE $JOBS MEADOW_OS=1 $MONO_MAKE_VARS
else
    # Protected mode — two-pass build
    echo "--- Building (PROTECTED mode, pass 2) ---"
    $MAKE $JOBS MEADOW_OS=1 $MONO_MAKE_VARS pass2

    # Build mbedTLS after pass2 (needs nuttx/config.h which pass2 generates)
    if [ ! -f "$MEADOW_OS_DIR/mbedtls/library/libmbedtls.a" ]; then
        echo "--- Building mbedTLS ---"
        cd "$MEADOW_OS_DIR"
        bash build-mbedtls.sh
        cd "$NUTTX_DIR"
    fi

    echo "--- Building (PROTECTED mode, pass 1) ---"
    $MAKE $JOBS MEADOW_OS=1 $MONO_MAKE_VARS pass1

    echo "--- Building final image ---"
    $MAKE $JOBS MEADOW_OS=1 $MONO_MAKE_VARS nuttx
fi

# Copy artifacts
mkdir -p "$BUILD_DIR"
cp "$NUTTX_DIR/nuttx.bin" "$BUILD_DIR/"
echo "--- Copied nuttx.bin to $BUILD_DIR/"

if [ -f "$NUTTX_DIR/nuttx_user.bin" ]; then
    cp "$NUTTX_DIR/nuttx_user.bin" "$BUILD_DIR/"
    echo "--- Copied nuttx_user.bin to $BUILD_DIR/"
fi

# Copy ELFs for GDB debugging
if [ -f "$NUTTX_DIR/nuttx" ]; then
    cp "$NUTTX_DIR/nuttx" "$BUILD_DIR/nuttx.elf"
    echo "--- Copied nuttx ELF to $BUILD_DIR/nuttx.elf"
fi
if [ -f "$NUTTX_DIR/nuttx_user.elf" ]; then
    cp "$NUTTX_DIR/nuttx_user.elf" "$BUILD_DIR/nuttx_user.elf"
    echo "--- Copied nuttx_user ELF to $BUILD_DIR/nuttx_user.elf"
fi

# Extract hook addresses from ELF files (if both kernel and user ELFs exist)
if [ -f "$BUILD_DIR/nuttx.elf" ] && [ -f "$BUILD_DIR/nuttx_user.elf" ]; then
    echo "--- Extracting hook addresses for Renode ---"
    python3 "$SCRIPT_DIR/tools/extract-hook-addresses.py" \
        "$BUILD_DIR/nuttx.elf" "$BUILD_DIR/nuttx_user.elf" || \
        echo "WARNING: Hook address extraction failed (non-fatal — new symbols may differ)"
fi

echo "=== Build complete (output: $BUILD_DIR/) ==="
ls -lh "$BUILD_DIR/"
