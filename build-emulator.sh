#!/bin/bash
set -e

SCRIPT_DIR="$( cd "$(dirname "$0")" ; pwd -P )"
MEADOW_OS_DIR="$SCRIPT_DIR/../Meadow.OS"
NUTTX_DIR="$MEADOW_OS_DIR/nuttx"
BUILD_DIR="$SCRIPT_DIR/build"
CONFIG="${1:-meadow_os_emu}"

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

echo "=== Building NuttX for Renode (config: $CONFIG) ==="

# Configure NuttX
echo "--- Configuring NuttX with stm32f777zit6-meadow:$CONFIG ---"
cd "$NUTTX_DIR"

# Clean previous build if .config exists with different config
if [ -f ".config" ]; then
    echo "--- Cleaning previous build ---"
    $MAKE distclean 2>/dev/null || true
fi

./tools/configure.sh "stm32f777zit6-meadow:$CONFIG"

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

# Build
if [ "$CONFIG" = "nsh" ]; then
    # Flat mode — single binary
    echo "--- Building (FLAT mode) ---"
    $MAKE -j$(sysctl -n hw.ncpu 2>/dev/null || echo 4) MEADOW_OS=1
else
    # Protected mode — two-pass build
    echo "--- Building (PROTECTED mode, pass 2) ---"
    $MAKE -j$(sysctl -n hw.ncpu 2>/dev/null || echo 4) MEADOW_OS=1 pass2

    # Build mbedTLS after pass2 (needs nuttx/config.h which pass2 generates)
    if [ ! -f "$MEADOW_OS_DIR/mbedtls/library/libmbedtls.a" ]; then
        echo "--- Building mbedTLS ---"
        cd "$MEADOW_OS_DIR"
        bash build-mbedtls.sh
        cd "$NUTTX_DIR"
    fi

    echo "--- Building (PROTECTED mode, pass 1) ---"
    $MAKE -j$(sysctl -n hw.ncpu 2>/dev/null || echo 4) MEADOW_OS=1 pass1

    echo "--- Building final image ---"
    $MAKE -j$(sysctl -n hw.ncpu 2>/dev/null || echo 4) MEADOW_OS=1 nuttx
fi

# Copy artifacts
mkdir -p "$BUILD_DIR"
cp "$NUTTX_DIR/nuttx.bin" "$BUILD_DIR/"
echo "--- Copied nuttx.bin to $BUILD_DIR/"

if [ -f "$NUTTX_DIR/nuttx_user.bin" ]; then
    cp "$NUTTX_DIR/nuttx_user.bin" "$BUILD_DIR/"
    echo "--- Copied nuttx_user.bin to $BUILD_DIR/"
fi

# Copy ELF for GDB debugging
if [ -f "$NUTTX_DIR/nuttx" ]; then
    cp "$NUTTX_DIR/nuttx" "$BUILD_DIR/nuttx.elf"
    echo "--- Copied nuttx ELF to $BUILD_DIR/nuttx.elf"
fi

echo "=== Build complete ==="
ls -la "$BUILD_DIR/"
