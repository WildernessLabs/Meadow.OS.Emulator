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

# Toolchain setup — mirrors Meadow/build.sh
# toolchain/macos contains ld.lld (LLVM linker) required by kernel Makefile
case "$(uname -s)" in
    Darwin)
        export PATH="$MEADOW_DIR/toolchain/macos:$PATH"
        # ARM GCC 10.3 from /Applications/ARM (legacy NuttX requires ~10.x)
        if [ -d "/Applications/ARM/bin" ]; then
            export PATH="/Applications/ARM/bin:$PATH"
        fi
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
    echo "       Install ARM GCC 10.3: https://developer.arm.com/downloads/-/gnu-rm"
    exit 1
fi

# Show which toolchain we're using
echo "Using: $(arm-none-eabi-gcc --version | head -1)"
echo "Linker: $(which ld.lld) — $(ld.lld --version 2>&1 | head -1)"

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
# Note: keep CONFIG_STM32F7_OTGFS enabled — pwrmgmt code references OTG registers unconditionally
kconfig-tweak --file "$NUTTX_CONFIG" --disable CONFIG_CDCACM
kconfig-tweak --file "$NUTTX_CONFIG" --disable CONFIG_BOARDCTL_USBDEVCTRL
kconfig-tweak --file "$NUTTX_CONFIG" --disable CONFIG_USBDEV

# Redirect HCOM from USB CDC-ACM to UART4
# Device numbering: ttyS0=USART1 (console), ttyS1=UART4, ttyS2=UART5
kconfig-tweak --file "$NUTTX_CONFIG" --set-str CONFIG_HCOM_COMMS_DEVICE_NAME /dev/ttyS1

# Route syslog to serial console on USART1
# Keep RAMLOG buffer alive via CONFIG_RAMLOG_CONSOLE (fault handler needs ramlog_get_sysbuffer_pointer)
kconfig-tweak --file "$NUTTX_CONFIG" --enable CONFIG_DEV_CONSOLE
kconfig-tweak --file "$NUTTX_CONFIG" --enable CONFIG_USART1_SERIAL_CONSOLE
kconfig-tweak --file "$NUTTX_CONFIG" --disable CONFIG_RAMLOG_SYSLOG
kconfig-tweak --file "$NUTTX_CONFIG" --enable CONFIG_RAMLOG_CONSOLE
kconfig-tweak --file "$NUTTX_CONFIG" --enable CONFIG_SYSLOG_CONSOLE
kconfig-tweak --file "$NUTTX_CONFIG" --enable CONFIG_SYSLOG_SERIAL_CONSOLE
kconfig-tweak --file "$NUTTX_CONFIG" --enable CONFIG_SYSLOG_WRITE

# Verify Mono runtime libraries exist (must be pre-built via Meadow/build-mono.sh)
MONO_DIR="$MEADOW_DIR/mono/mono"
if [ ! -f "$MONO_DIR/sgen/.libs/libmonosgen.a" ]; then
    echo "ERROR: Mono runtime libraries not found at $MONO_DIR/sgen/.libs/"
    echo "       Build Mono first: cd $MEADOW_DIR && ./build-mono.sh"
    exit 1
fi
echo "--- Mono runtime libraries found ---"

# Prevent reset loops in emulator
kconfig-tweak --file "$NUTTX_CONFIG" --set-val CONFIG_BOARD_RESET_ON_ASSERT 0

# Generate config.h and set up include directories (needed before mbedtls build)
echo "--- Generating NuttX headers (make context) ---"
$MAKE context

# Build mbedtls for NuttX (same flags as Meadow/build-mbedtls.sh)
MBEDTLS_DIR="$MEADOW_DIR/mbedtls"
if [ -d "$MBEDTLS_DIR/library" ] && [ ! -f "$MBEDTLS_DIR/library/libmbedcrypto.a" ]; then
    echo "--- Building mbedtls ---"
    make -C "$MBEDTLS_DIR/library" -j$(sysctl -n hw.ncpu 2>/dev/null || echo 4) \
        CC="$(which arm-none-eabi-gcc)" \
        AR="$(which arm-none-eabi-ar)" \
        CFLAGS="-g -mthumb -mcpu=cortex-m7 -mfloat-abi=hard -mfpu=fpv5-d16 \
            -D_POSIX_VERSION=201112L -D__NuttX__=1 -DSA_RESTART=0 \
            -I$NUTTX_DIR/include -I$NUTTX_DIR/include/nuttx/lib \
            -I$NUTTX_DIR/include/sys -nostdinc -nostdlib -fno-builtin -fno-common -Os"
fi

# Version — read from Meadow/version.txt (e.g. "2.5.7") to match real firmware
if [ -f "$MEADOW_DIR/version.txt" ]; then
    IFS='.' read -r EMU_VERSION_MAJOR EMU_VERSION_MINOR EMU_VERSION_REVISION <<< "$(cat "$MEADOW_DIR/version.txt")"
else
    EMU_VERSION_MAJOR=0; EMU_VERSION_MINOR=0; EMU_VERSION_REVISION=0
fi
EMU_VERSION_BUILD=${EMU_VERSION_BUILD:-0}
echo "--- Version: $EMU_VERSION_MAJOR.$EMU_VERSION_MINOR.$EMU_VERSION_REVISION.$EMU_VERSION_BUILD ---"

# Compute git hash for version injection
GIT_HASH=$(git -C "$MEADOW_DIR" rev-parse --short HEAD 2>/dev/null || echo "00000000")

# Inject version placeholders in hcom_nuttx_shared.h (same approach as Meadow/scripts/version_methods.sh)
SHARED_HEADER="$NUTTX_DIR/include/meadow/hcom_nuttx_shared.h"
if grep -q '###VERSION_MAJOR###' "$SHARED_HEADER" 2>/dev/null; then
    echo "--- Injecting version placeholders ---"
    cp "$SHARED_HEADER" "$SHARED_HEADER.emubak"
    sed -i.bak \
        -e "s/###VERSION_MAJOR###/$EMU_VERSION_MAJOR/g" \
        -e "s/###VERSION_MINOR###/$EMU_VERSION_MINOR/g" \
        -e "s/###VERSION_REVISION###/$EMU_VERSION_REVISION/g" \
        -e "s/###VERSION_BUILD###/$EMU_VERSION_BUILD/g" \
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

# Inject version placeholders in user-space linker script
USER_LD="$NUTTX_DIR/configs/stm32f777zit6-meadow/scripts/user-space.ld"
if grep -q '###VERSION_MAJOR###' "$USER_LD" 2>/dev/null; then
    echo "--- Injecting version placeholders in user-space.ld ---"
    cp "$USER_LD" "$USER_LD.emubak"
    sed -i.bak \
        -e "s/###VERSION_MAJOR###/$EMU_VERSION_MAJOR/g" \
        -e "s/###VERSION_MINOR###/$EMU_VERSION_MINOR/g" \
        -e "s/###VERSION_REVISION###/$EMU_VERSION_REVISION/g" \
        -e "s/###VERSION_BUILD###/$EMU_VERSION_BUILD/g" \
        -e "s/###BUILD_DAY###/$((10#$(date -u +%d)))/g" \
        -e "s/###BUILD_MONTH###/$((10#$(date -u +%m)))/g" \
        -e "s/###BUILD_YEAR###/$((10#$(date -u +%y)))/g" \
        -e "s/###BUILD_HOUR###/$((10#$(date -u +%H)))/g" \
        -e "s/###BUILD_MINUTE###/$((10#$(date -u +%M)))/g" \
        -e "s/###BUILD_SECOND###/$((10#$(date -u +%S)))/g" \
        -e "s/###BUILD_HASH###/0x${GIT_HASH:-00000000}/g" \
        -e "s/###MONO_GIT_REF###/$(git -C "$MEADOW_DIR/mono" rev-parse --short HEAD 2>/dev/null || echo 'emulator')/g" \
        "$USER_LD"
    rm -f "$USER_LD.bak"
    RESTORE_USER_LD=true
fi

# Cleanup function to restore modified files
cleanup() {
    if [ "$RESTORE_SHARED_HEADER" = true ] && [ -f "$SHARED_HEADER.emubak" ]; then
        mv "$SHARED_HEADER.emubak" "$SHARED_HEADER"
    fi
    if [ "$RESTORE_USER_LD" = true ] && [ -f "$USER_LD.emubak" ]; then
        mv "$USER_LD.emubak" "$USER_LD"
    fi
}
trap cleanup EXIT

# Parallel job count — set MEADOW_ADDITIONAL_MAKE_OPTIONS=-j1 if all passes should be single-threaded
JOBS="${MEADOW_ADDITIONAL_MAKE_OPTIONS:--j$(sysctl -n hw.ncpu 2>/dev/null || echo 4)}"

# Build — legacy uses protected mode (pass2 → pass1deps → pass1)
echo "--- Building mksyscall ---"
$MAKE -C "$NUTTX_DIR/tools" -f Makefile.host mksyscall 2>/dev/null || true

# pass2 uses -j1 to avoid ar race condition when building mono example into libapps.a
echo "--- Building (PROTECTED mode, pass 2) ---"
$MAKE -j1 pass2

echo "--- Building pass1 dependencies ---"
$MAKE $JOBS pass1deps

echo "--- Building (PROTECTED mode, pass 1) ---"
$MAKE $JOBS pass1

# Copy artifacts
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
