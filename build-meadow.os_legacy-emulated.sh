#!/bin/bash
set -e

SCRIPT_DIR="$( cd "$(dirname "$0")" ; pwd -P )"
MEADOW_DIR="$SCRIPT_DIR/../Meadow"
NUTTX_DIR="$MEADOW_DIR/nuttx"
BUILD_DIR="$SCRIPT_DIR/build/legacy"
CONFIG="${1:-mono}"

# Validate Meadow sibling directory exists
if [ ! -d "$NUTTX_DIR" ]; then
    echo "ERROR: Meadow (legacy) not found at $MEADOW_DIR"
    echo "       Meadow.OS.Emulator must be a sibling directory to Meadow/"
    exit 1
fi

# Add emulator tools to PATH (includes kconfig-tweak shim)
export PATH="$SCRIPT_DIR/tools:$PATH"

# Legacy repo bundles its own toolchain
case "$(uname -s)" in
    Darwin)
        export PATH="$MEADOW_DIR/toolchain/macos:$PATH"
        ;;
    Linux)
        export PATH="$MEADOW_DIR/toolchain/linux:$PATH"
        ;;
esac

# NuttX requires GNU Make 4.x+ (macOS ships 3.81)
if command -v gmake &> /dev/null; then
    MAKE=gmake
else
    MAKE=make
fi

# Check for ARM toolchain
if ! command -v arm-none-eabi-gcc &> /dev/null; then
    echo "ERROR: arm-none-eabi-gcc not found"
    echo "       Install with: brew install --cask gcc-arm-embedded"
    exit 1
fi

echo "=== Building Legacy Meadow OS for Renode (config: $CONFIG) ==="

# Configure NuttX
echo "--- Configuring NuttX with stm32f777zit6-meadow/$CONFIG ---"
cd "$NUTTX_DIR"

# Clean previous build if .config exists
if [ -f ".config" ]; then
    echo "--- Cleaning previous build ---"
    $MAKE distclean 2>/dev/null || true
fi

# Legacy NuttX uses slash separator: stm32f777zit6-meadow/mono
./tools/configure.sh "stm32f777zit6-meadow/$CONFIG"

# Apply emulator-specific config tweaks
NUTTX_CONFIG="$NUTTX_DIR/.config"

# Disable USB (Renode can't emulate USB device controllers)
kconfig-tweak --file "$NUTTX_CONFIG" --disable CONFIG_CDCACM
kconfig-tweak --file "$NUTTX_CONFIG" --disable CONFIG_BOARDCTL_USBDEVCTRL
kconfig-tweak --file "$NUTTX_CONFIG" --disable CONFIG_STM32F7_OTGFS
kconfig-tweak --file "$NUTTX_CONFIG" --disable CONFIG_USBDEV

# Redirect HCOM from USB CDC-ACM to UART4
# (UART4 is already enabled in legacy mono defconfig)
kconfig-tweak --file "$NUTTX_CONFIG" --set-str CONFIG_HCOM_COMMS_DEVICE_NAME /dev/ttyS1

# Route syslog to serial console instead of RAM log
kconfig-tweak --file "$NUTTX_CONFIG" --enable CONFIG_DEV_CONSOLE
kconfig-tweak --file "$NUTTX_CONFIG" --enable CONFIG_SYSLOG_SERIAL_CONSOLE
kconfig-tweak --file "$NUTTX_CONFIG" --disable CONFIG_RAMLOG_SYSLOG

# Prevent reset loops in emulator
kconfig-tweak --file "$NUTTX_CONFIG" --set-val CONFIG_BOARD_RESET_ON_ASSERT 0

# Inject version placeholders in hcom_nuttx_shared.h
SHARED_HEADER="$NUTTX_DIR/include/meadow/hcom_nuttx_shared.h"
if grep -q '###VERSION_MAJOR###' "$SHARED_HEADER" 2>/dev/null; then
    echo "--- Injecting version placeholders ---"
    cp "$SHARED_HEADER" "$SHARED_HEADER.emubak"
    GIT_HASH=$(git -C "$MEADOW_DIR" rev-parse --short HEAD 2>/dev/null || echo "00000000")
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

# Build — legacy uses protected mode (pass2 → pass1deps → pass1 → nuttx)
echo "--- Building mksyscall ---"
$MAKE -C "$NUTTX_DIR/tools" -f Makefile.host mksyscall 2>/dev/null || true

echo "--- Building (PROTECTED mode, pass 2) ---"
$MAKE -j$(sysctl -n hw.ncpu 2>/dev/null || echo 4) pass2

echo "--- Building pass1 dependencies ---"
$MAKE -j$(sysctl -n hw.ncpu 2>/dev/null || echo 4) pass1deps

echo "--- Building (PROTECTED mode, pass 1) ---"
$MAKE -j$(sysctl -n hw.ncpu 2>/dev/null || echo 4) pass1

echo "--- Building final image ---"
$MAKE -j$(sysctl -n hw.ncpu 2>/dev/null || echo 4) nuttx

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

echo "=== Legacy build complete ==="
ls -la "$BUILD_DIR/"
