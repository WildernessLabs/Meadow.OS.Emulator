#!/bin/bash
set -e

SCRIPT_DIR="$( cd "$(dirname "$0")" ; pwd -P )"

# Ensure dotnet is on PATH (macOS Homebrew installs to /usr/local/share/dotnet)
if ! command -v dotnet &> /dev/null; then
    export PATH="/usr/local/share/dotnet:$PATH"
fi

# Find Renode — custom fork (sibling repo or RENODE_PATH), PATH, or macOS app bundle
# Check RENODE_PATH env var first, then sibling ../renode, then git root's sibling
RENODE_FORK=""
for candidate in \
    "${RENODE_PATH:-}" \
    "$SCRIPT_DIR/../renode" \
    "$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel 2>/dev/null)/../renode" \
; do
    if [ -n "$candidate" ] && [ -f "$candidate/output/bin/Release/Renode.dll" ]; then
        RENODE_FORK="$candidate"
        break
    fi
done

if [ -n "$RENODE_FORK" ]; then
    # Custom WildernessLabs/renode fork (built from source)
    RENODE_CMD="dotnet $RENODE_FORK/output/bin/Release/Renode.dll"
elif command -v renode &> /dev/null; then
    echo "WARNING: Using stock Renode (missing SVC/PRIMASK fix — emulation will be ~12,000x slower)."
    echo "         Run ./setup.sh to install the WildernessLabs/renode fork."
    echo ""
    RENODE_CMD="renode"
elif [ -f "/Applications/Renode.app/Contents/MacOS/bin/Renode.exe" ]; then
    echo "WARNING: Using stock Renode.app (missing SVC/PRIMASK fix — emulation will be ~12,000x slower)."
    echo "         Run ./setup.sh to install the WildernessLabs/renode fork."
    echo ""
    RENODE_CMD="mono /Applications/Renode.app/Contents/MacOS/bin/Renode.exe"
else
    echo "ERROR: Renode not found."
    echo ""
    echo "  Option 1 (recommended): Build the WildernessLabs/renode fork as a sibling repo:"
    echo "    cd $(dirname "$SCRIPT_DIR")"
    echo "    git clone https://github.com/WildernessLabs/renode.git"
    echo "    cd renode"
    echo "    git checkout meadow-emulator-fixes"
    echo "    git submodule update --init src/Infrastructure"
    echo "    bash build.sh --host-arch aarch64 --skip-fetch   # Apple Silicon"
    echo "    bash build.sh --skip-fetch                       # Intel / Linux"
    echo ""
    echo "  Option 2: Install stock Renode from https://renode.io/download"
    echo "    (missing SVC/PRIMASK and RTC fixes — emulation will be very slow)"
    exit 1
fi

# Parse arguments
LEGACY=false
ESP32=false
SCRIPT=""
MONITOR_PORT=1234

for arg in "$@"; do
    case "$arg" in
        --legacy)
            LEGACY=true
            ;;
        --esp32)
            ESP32=true
            ;;
        --port=*)
            MONITOR_PORT="${arg#--port=}"
            ;;
        --help|-h)
            echo "Usage: ./run.sh [options] [script-name]"
            echo ""
            echo "Options:"
            echo "  --legacy        Run NuttX Legacy OS (default: Meadow.OS)"
            echo "  --esp32         Run with real ESP32 coprocessor (dual-machine emulation)"
            echo "                  Without --esp32, the ESP32 is stubbed via EspcpStub"
            echo "  --port=N        Renode monitor port (default: 1234)"
            echo ""
            echo "Examples:"
            echo "  ./run.sh --legacy              # NuttX + EspcpStub (no ESP32)"
            echo "  ./run.sh --legacy --esp32      # NuttX + real ESP32 firmware"
            echo "  ./run.sh                       # Meadow.OS (default)"
            exit 0
            ;;
        *)
            SCRIPT="$arg"
            ;;
    esac
done

# Select default script based on OS variant and ESP32 flag
if [ -z "$SCRIPT" ]; then
    if [ "$LEGACY" = true ] && [ "$ESP32" = true ]; then
        SCRIPT="meadow-dual"
    elif [ "$LEGACY" = true ]; then
        SCRIPT="meadow-legacy-headless"
    else
        SCRIPT="meadow-os"
    fi
fi

# If just a name was given, look in scripts/
if [[ ! "$SCRIPT" == *.resc ]]; then
    SCRIPT="scripts/${SCRIPT}.resc"
fi

if [ ! -f "$SCRIPT_DIR/$SCRIPT" ]; then
    echo "ERROR: Script not found: $SCRIPT_DIR/$SCRIPT"
    echo "Available scripts:"
    ls "$SCRIPT_DIR/scripts/"*.resc 2>/dev/null
    exit 1
fi

# Check for built binary and generated hook addresses
if [ "$LEGACY" = true ]; then
    if [ ! -f "$SCRIPT_DIR/build/legacy/nuttx.bin" ]; then
        echo "ERROR: nuttx.bin not found in build/legacy/"
        echo "       Run ./build-meadow.os_legacy-emulated.sh first"
        exit 1
    fi
    # Check hook addresses are generated and not stale
    ADDR_FILE="$SCRIPT_DIR/build/legacy/addresses.resc"
    ELF_FILE="$SCRIPT_DIR/build/legacy/nuttx.elf"
    if [ ! -f "$ADDR_FILE" ]; then
        echo "WARNING: Hook addresses not generated. Extracting now..."
        python3 "$SCRIPT_DIR/tools/extract-hook-addresses.py" \
            "$SCRIPT_DIR/build/legacy/nuttx.elf" "$SCRIPT_DIR/build/legacy/nuttx_user.elf"
    elif [ -f "$ELF_FILE" ] && [ "$ELF_FILE" -nt "$ADDR_FILE" ]; then
        echo "WARNING: ELF is newer than addresses.resc — regenerating hook addresses..."
        python3 "$SCRIPT_DIR/tools/extract-hook-addresses.py" \
            "$SCRIPT_DIR/build/legacy/nuttx.elf" "$SCRIPT_DIR/build/legacy/nuttx_user.elf"
    fi
else
    if [ ! -f "$SCRIPT_DIR/build/nuttx.bin" ]; then
        echo "ERROR: nuttx.bin not found in build/"
        echo "       Run ./build-meadow.os-emulated.sh first"
        exit 1
    fi
fi

# ESP32 dual-machine prerequisites
if [ "$ESP32" = true ]; then
    # ESP32 firmware — look in sibling repos (Meadow-ESP32 for legacy, Meadow.OS for new)
    ESP32_FW=""
    for candidate in \
        "${ESP32_FW_PATH:-}" \
        "$SCRIPT_DIR/../Meadow-ESP32/build/MeadowComms.elf" \
        "$SCRIPT_DIR/../Meadow.OS/esp32/build/MeadowComms.elf" \
    ; do
        if [ -f "$candidate" ]; then
            ESP32_FW="$(cd "$(dirname "$candidate")" && pwd)/$(basename "$candidate")"
            break
        fi
    done

    if [ -z "$ESP32_FW" ]; then
        echo "ERROR: ESP32 firmware (MeadowComms.elf) not found."
        echo "       Expected in sibling Meadow-ESP32/build/ or set ESP32_FW_PATH"
        echo "       Build it:  cd ../Meadow-ESP32 && ./build.sh"
        exit 1
    fi
    echo "ESP32 firmware: $ESP32_FW"
    export ESP32_FW

    # ESP32 ROM
    if [ ! -f "$SCRIPT_DIR/roms/esp32-v3-rom.bin" ]; then
        echo "ERROR: ESP32 ROM not found at roms/esp32-v3-rom.bin"
        echo "       See roms/README.md for download instructions"
        exit 1
    fi
fi

echo "=== Starting Renode with $SCRIPT ==="
echo "Monitor: telnet localhost $MONITOR_PORT"
echo "HCOM:    socket://localhost:4242"
if [ "$ESP32" = true ]; then
    echo "ESP32:   dual-machine mode (real coprocessor)"
    echo "         meadow config route socket://localhost:4242"
else
    echo "ESP32:   stubbed (EspcpStub)"
fi
echo "Press Ctrl+C to stop."
echo ""
cd "$SCRIPT_DIR"

# For dual-machine mode, pass the ESP32 firmware path as a Renode variable
if [ "$ESP32" = true ]; then
    $RENODE_CMD --disable-xwt --port "$MONITOR_PORT" \
        --execute "set esp32_fw @$ESP32_FW; include @$SCRIPT"
else
    $RENODE_CMD --disable-xwt --port "$MONITOR_PORT" --execute "include @$SCRIPT"
fi
