#!/bin/bash
set -e

SCRIPT_DIR="$( cd "$(dirname "$0")" ; pwd -P )"

# Parse build-specific flags before forwarding to read-config.sh
DO_CLEAN=false
REMAINING_ARGS=()
for arg in "$@"; do
    case "$arg" in
        --clean) DO_CLEAN=true ;;
        *) REMAINING_ARGS+=("$arg") ;;
    esac
done

# Load config for EMU_OS, EMU_DEFCONFIG, EMU_RUNTIME, etc.
source "$SCRIPT_DIR/scripts/read-config.sh" "${REMAINING_ARGS[@]}" 2>/dev/null || true

# -------------------------------------------------------------------------
# OS-dependent path setup
# -------------------------------------------------------------------------
if [ "$EMU_OS" = "legacy" ]; then
    MEADOW_SRC_DIR="$SCRIPT_DIR/../Meadow"
    NUTTX_DIR="$MEADOW_SRC_DIR/nuttx"
    CONFIG="${EMU_DEFCONFIG:-mono}"
    CONFIGURE_SEP="/"
    SHARED_HEADER="$NUTTX_DIR/include/meadow/hcom_nuttx_shared.h"
    VERSION_SOURCE="$MEADOW_SRC_DIR/version.txt"
else
    # meadow-os (default)
    MEADOW_SRC_DIR="$SCRIPT_DIR/../Meadow.OS"
    NUTTX_DIR="$MEADOW_SRC_DIR/nuttx"
    CONFIG="${EMU_DEFCONFIG:-meadow_os_emu}"
    CONFIGURE_SEP=":"
    SHARED_HEADER="$MEADOW_SRC_DIR/Meadow.OS/include/meadow/hcom_nuttx_shared.h"
    VERSION_SOURCE="$MEADOW_SRC_DIR/version.txt"
fi

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

# NuttX requires GNU Make 4.x+ (macOS ships 3.81)
if command -v gmake &> /dev/null; then
    MAKE=gmake
else
    MAKE=make
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
MONO_MAKE_VARS=""

if [ "$EMU_RUNTIME" = "legacy-os_modern-dotnet" ]; then
    # .NET 10 Mono runtime — used by both legacy and meadow-os OS variants
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
    # Legacy NuttX links via Make.defs (MONO_BUILD_DIR); meadow-os uses MONO_AOBJS
    if [ "$EMU_OS" != "legacy" ]; then
        MONO_MAKE_VARS="MONO_AOBJS=$MONO_LIB"
    fi
elif [ "$EMU_OS" = "legacy" ]; then
    MONO_DIR="$MEADOW_SRC_DIR/mono/mono"
    if [ ! -f "$MONO_DIR/sgen/.libs/libmonosgen.a" ]; then
        echo "ERROR: Mono runtime libraries not found at $MONO_DIR/sgen/.libs/"
        echo "       Build Mono first: cd $MEADOW_SRC_DIR && ./build-mono.sh"
        exit 1
    fi
    echo "--- Legacy Mono runtime libraries found ---"
fi

echo "=== Building NuttX for Renode (os: $EMU_OS, config: $CONFIG, runtime: ${EMU_RUNTIME:-default}) ==="

# -------------------------------------------------------------------------
# Configure NuttX
# -------------------------------------------------------------------------
echo "--- Configuring NuttX with stm32f777zit6-meadow${CONFIGURE_SEP}$CONFIG ---"
cd "$NUTTX_DIR"

# Clean build if --clean flag passed or no .config exists (first build)
if [ "$DO_CLEAN" = true ] || [ ! -f "$NUTTX_DIR/.config" ]; then
    echo "--- Cleaning previous build ---"
    $MAKE distclean 2>/dev/null || true

    if [ "$EMU_OS" = "legacy" ]; then
        # Legacy NuttX distclean doesn't clean board config subdirectory objects
        find "$NUTTX_DIR/configs/stm32f777zit6-meadow/src" -name "*.o" -delete 2>/dev/null || true

        # Legacy NuttX uses slash separator
        ./tools/configure.sh "stm32f777zit6-meadow/${CONFIG}"
    else
        # Meadow.OS uses colon separator; configure.sh may exit non-zero due to
        # cosmetic Kconfig warning — .config is written successfully before that.
        ./tools/configure.sh "stm32f777zit6-meadow:${CONFIG}" || {
            if [ -f "$NUTTX_DIR/.config" ]; then
                echo "WARNING: configure.sh reported an error but .config was written — continuing"
            else
                echo "ERROR: configure.sh failed and no .config was produced"
                exit 1
            fi
        }
    fi
else
    echo "--- Incremental build (skipping distclean + configure, use --clean for full rebuild) ---"
fi

# -------------------------------------------------------------------------
# Kconfig tweaks for emulator
# -------------------------------------------------------------------------
NUTTX_CONFIG="$NUTTX_DIR/.config"

if [ "$EMU_OS" = "legacy" ]; then
    echo "--- Applying emulator config tweaks (legacy) ---"

    # Disable USB (Renode can't emulate USB device controllers)
    # Keep CONFIG_STM32F7_OTGFS enabled — pwrmgmt code references OTG registers unconditionally
    kconfig-tweak --file "$NUTTX_CONFIG" --disable CONFIG_CDCACM
    kconfig-tweak --file "$NUTTX_CONFIG" --disable CONFIG_BOARDCTL_USBDEVCTRL
    kconfig-tweak --file "$NUTTX_CONFIG" --disable CONFIG_USBDEV

    # Redirect HCOM from USB CDC-ACM to UART4
    # Device numbering: ttyS0=USART1 (console), ttyS1=UART4, ttyS2=UART5
    kconfig-tweak --file "$NUTTX_CONFIG" --set-str CONFIG_HCOM_COMMS_DEVICE_NAME /dev/ttyS1

    # Route syslog to serial console on USART1
    # Keep RAMLOG buffer alive (fault handler needs ramlog_get_sysbuffer_pointer)
    kconfig-tweak --file "$NUTTX_CONFIG" --enable CONFIG_DEV_CONSOLE
    kconfig-tweak --file "$NUTTX_CONFIG" --enable CONFIG_USART1_SERIAL_CONSOLE
    kconfig-tweak --file "$NUTTX_CONFIG" --disable CONFIG_RAMLOG_SYSLOG
    kconfig-tweak --file "$NUTTX_CONFIG" --enable CONFIG_RAMLOG_CONSOLE
    kconfig-tweak --file "$NUTTX_CONFIG" --enable CONFIG_SYSLOG_CONSOLE
    kconfig-tweak --file "$NUTTX_CONFIG" --enable CONFIG_SYSLOG_SERIAL_CONSOLE
    kconfig-tweak --file "$NUTTX_CONFIG" --enable CONFIG_SYSLOG_WRITE

    # Prevent reset loops in emulator
    kconfig-tweak --file "$NUTTX_CONFIG" --set-val CONFIG_BOARD_RESET_ON_ASSERT 0

    # Enable tmpfs for assembly pre-caching (QSPI→RAM copy for fast Mono loading)
    kconfig-tweak --file "$NUTTX_CONFIG" --enable CONFIG_FS_TMPFS
    kconfig-tweak --file "$NUTTX_CONFIG" --set-val CONFIG_FS_TMPFS_BLOCKSIZE 512
    kconfig-tweak --file "$NUTTX_CONFIG" --set-val CONFIG_FS_TMPFS_DIRECTORY_ALLOCGUARD 64
    kconfig-tweak --file "$NUTTX_CONFIG" --set-val CONFIG_FS_TMPFS_DIRECTORY_FREEGUARD 64
    kconfig-tweak --file "$NUTTX_CONFIG" --set-val CONFIG_FS_TMPFS_FILE_ALLOCGUARD 512
    kconfig-tweak --file "$NUTTX_CONFIG" --set-val CONFIG_FS_TMPFS_FILE_FREEGUARD 512

elif [[ "$CONFIG" != *_emu* ]]; then
    echo "--- Applying emulator config tweaks (meadow-os) ---"

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

# -------------------------------------------------------------------------
# Version injection
# -------------------------------------------------------------------------
if [ -n "$VERSION_SOURCE" ] && [ -f "$VERSION_SOURCE" ]; then
    IFS='.' read -r EMU_VERSION_MAJOR EMU_VERSION_MINOR EMU_VERSION_REVISION <<< "$(cat "$VERSION_SOURCE")"
else
    EMU_VERSION_MAJOR=0; EMU_VERSION_MINOR=0; EMU_VERSION_REVISION=0
fi
# Compute git distance from last version.txt change (matches build.sh behavior)
if [ -z "$EMU_VERSION_BUILD" ] && [ -n "$VERSION_SOURCE" ] && [ -f "$VERSION_SOURCE" ]; then
    _ver_origin=$(git -C "$MEADOW_SRC_DIR" log -n 1 --format=%H -- "$VERSION_SOURCE" 2>/dev/null)
    if [ -n "$_ver_origin" ]; then
        EMU_VERSION_BUILD=$(git -C "$MEADOW_SRC_DIR" log --oneline "${_ver_origin}..HEAD" 2>/dev/null | wc -l | tr -d ' ')
    fi
fi
EMU_VERSION_BUILD=${EMU_VERSION_BUILD:-0}
echo "--- Version: $EMU_VERSION_MAJOR.$EMU_VERSION_MINOR.$EMU_VERSION_REVISION.$EMU_VERSION_BUILD ---"

GIT_HASH=$(git -C "$MEADOW_SRC_DIR" rev-parse --short=8 HEAD 2>/dev/null || echo "00000000")
GIT_HASH="${GIT_HASH:0:8}"

# Inject version placeholders in hcom_nuttx_shared.h
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

# Generate BYTE() directives for MONO_GIT_REF (matches build.sh behavior)
_mono_git_ref_bytes() {
    local ref
    ref=$(git -C "$MEADOW_SRC_DIR" rev-parse --abbrev-ref HEAD 2>/dev/null || echo "unknown")
    local result=""
    local count=0
    for b in $(xxd -p -c 1 <<<"$ref"); do
        if [ "$count" -lt 32 ] && [ "$b" != "0a" ]; then
            result+="BYTE(0x$b)"
            count=$((count+1))
        fi
    done
    result+="BYTE(00)"
    echo "$result"
}

# Legacy-only: inject version placeholders in user-space linker script
if [ "$EMU_OS" = "legacy" ]; then
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
            -e "s/###MONO_GIT_REF###/$(_mono_git_ref_bytes)/g" \
            "$USER_LD"
        rm -f "$USER_LD.bak"
        RESTORE_USER_LD=true
    fi
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

# Parallel job count
JOBS="${MEADOW_ADDITIONAL_MAKE_OPTIONS:--j$(sysctl -n hw.ncpu 2>/dev/null || echo 4)}"

# -------------------------------------------------------------------------
# Build
# -------------------------------------------------------------------------
if [ "$EMU_OS" = "legacy" ]; then
    # Legacy: context → mbedtls → mksyscall → pass2 → pass1deps → pass1
    echo "--- Generating NuttX headers (make context) ---"
    $MAKE context

    # Build mbedtls for NuttX (same flags as Meadow/build-mbedtls.sh)
    MBEDTLS_DIR="$MEADOW_SRC_DIR/mbedtls"
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

    echo "--- Building mksyscall ---"
    $MAKE -C "$NUTTX_DIR/tools" -f Makefile.host mksyscall 2>/dev/null || true

    echo "--- Building (PROTECTED mode, pass 2) ---"
    $MAKE $JOBS pass2

    echo "--- Building pass1 dependencies ---"
    $MAKE $JOBS pass1deps

    echo "--- Building (PROTECTED mode, pass 1) ---"
    $MAKE $JOBS pass1

else
    # Meadow.OS: pass2 → mbedtls → pass1 → nuttx
    if [ "$CONFIG" = "nsh" ]; then
        echo "--- Building (FLAT mode) ---"
        $MAKE $JOBS MEADOW_OS=1 $MONO_MAKE_VARS
    else
        echo "--- Building (PROTECTED mode, pass 2) ---"
        $MAKE $JOBS MEADOW_OS=1 $MONO_MAKE_VARS pass2

        # Build mbedTLS after pass2 (needs nuttx/config.h which pass2 generates)
        if [ ! -f "$MEADOW_SRC_DIR/mbedtls/library/libmbedtls.a" ]; then
            echo "--- Building mbedTLS ---"
            cd "$MEADOW_SRC_DIR"
            bash build-mbedtls.sh
            cd "$NUTTX_DIR"
        fi

        echo "--- Building (PROTECTED mode, pass 1) ---"
        $MAKE $JOBS MEADOW_OS=1 $MONO_MAKE_VARS pass1

        echo "--- Building final image ---"
        $MAKE $JOBS MEADOW_OS=1 $MONO_MAKE_VARS nuttx
    fi
fi

# -------------------------------------------------------------------------
# Copy artifacts
# -------------------------------------------------------------------------
mkdir -p "$BUILD_DIR"

if [ "$EMU_OS" = "legacy" ]; then
    # Legacy memory layout requires split binaries:
    # vectors at 0x08040000, kernel at 0x08140000, user at 0x08040200+

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

        # Extract .mono_signature directly from ELF and pad to match .mono start
        SIG_OFFSET=$(arm-none-eabi-readelf -S "$NUTTX_DIR/nuttx_user.elf" | \
            awk '/\.mono_signature/ {print "0x" $5}')
        SIG_SIZE=$(arm-none-eabi-readelf -S "$NUTTX_DIR/nuttx_user.elf" | \
            awk '/\.mono_signature/ {print "0x" $6}')
        # Get the VMA (load address) of .mono_signature and .mono sections
        # to compute the exact padding between them
        SIG_VMA=$(arm-none-eabi-readelf -S "$NUTTX_DIR/nuttx_user.elf" | \
            awk '/\.mono_signature/ {print "0x" $4}')
        MONO_VMA=$(arm-none-eabi-readelf -S "$NUTTX_DIR/nuttx_user.elf" | \
            awk '/[^_]\.mono / {print "0x" $4}')
        python3 -c "
import sys
sig_vma = $SIG_VMA
mono_vma = $MONO_VMA
pad_size = mono_vma - sig_vma  # exact distance from sig start to .mono start
with open('$NUTTX_DIR/nuttx_user.elf', 'rb') as f:
    f.seek($SIG_OFFSET)
    sig = f.read($SIG_SIZE)
    sig = sig.ljust(pad_size, b'\x00')
    with open('$BUILD_DIR/nuttx_mono_sig.bin', 'wb') as out:
        out.write(sig)
print(f'Signature: {sig[:4].hex()} version={int.from_bytes(sig[16:20], \"little\")}.{int.from_bytes(sig[12:16], \"little\")}.{int.from_bytes(sig[8:12], \"little\")}.{int.from_bytes(sig[4:8], \"little\")}')
print(f'Sig padding: {len(sig)} bytes (sig_vma=0x{sig_vma:X}, mono_vma=0x{mono_vma:X})')
"
        cat "$BUILD_DIR/nuttx_mono_sig.bin" "$BUILD_DIR/nuttx_mono_code.bin" > "$BUILD_DIR/nuttx_mono.bin"
        rm -f "$BUILD_DIR/nuttx_mono_code.bin" "$BUILD_DIR/nuttx_mono_sig.bin"
        echo "--- Created nuttx_mono.bin (Mono runtime for SDRAM, $(du -h "$BUILD_DIR/nuttx_mono.bin" | cut -f1))"
    else
        echo "--- WARNING: No .mono sections found in nuttx_user.elf"
    fi

    # Keep full nuttx.bin for reference
    cp "$NUTTX_DIR/nuttx.bin" "$BUILD_DIR/nuttx.bin"

else
    # Meadow.OS: simple binary copy
    cp "$NUTTX_DIR/nuttx.bin" "$BUILD_DIR/"
    echo "--- Copied nuttx.bin to $BUILD_DIR/"

    if [ -f "$NUTTX_DIR/nuttx_user.bin" ]; then
        cp "$NUTTX_DIR/nuttx_user.bin" "$BUILD_DIR/"
        echo "--- Copied nuttx_user.bin to $BUILD_DIR/"
    fi
fi

# Copy ELFs for GDB debugging
if [ -f "$NUTTX_DIR/nuttx" ]; then
    cp "$NUTTX_DIR/nuttx" "$BUILD_DIR/nuttx.elf"
    echo "--- Copied nuttx ELF to $BUILD_DIR/nuttx.elf"
fi
if [ -f "$NUTTX_DIR/nuttx.elf" ]; then
    cp "$NUTTX_DIR/nuttx.elf" "$BUILD_DIR/nuttx.elf"
    echo "--- Copied nuttx ELF to $BUILD_DIR/nuttx.elf"
fi
if [ -f "$NUTTX_DIR/nuttx_user.elf" ]; then
    cp "$NUTTX_DIR/nuttx_user.elf" "$BUILD_DIR/nuttx_user.elf"
    echo "--- Copied nuttx_user ELF to $BUILD_DIR/nuttx_user.elf"
fi

# Extract hook addresses from ELF files
if [ -f "$BUILD_DIR/nuttx.elf" ] && [ -f "$BUILD_DIR/nuttx_user.elf" ]; then
    echo "--- Extracting hook addresses for Renode ---"
    python3 "$SCRIPT_DIR/tools/extract-hook-addresses.py" \
        "$BUILD_DIR/nuttx.elf" "$BUILD_DIR/nuttx_user.elf" || \
        echo "WARNING: Hook address extraction failed (non-fatal — new symbols may differ)"
fi

echo "=== Build complete (output: $BUILD_DIR/) ==="
ls -lh "$BUILD_DIR/"
