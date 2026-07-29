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
# Load config — sets EMU_OS, EMU_BOARD, EMU_COPROCESSOR, EMU_MONITOR_PORT, etc.
CONFIG_ARG=""
SCRIPT=""
CLI_OVERRIDES=""

# Pre-scan for --config to pass to read-config.sh
for arg in "$@"; do
    case "$arg" in
        --config=*) CONFIG_ARG="${arg#--config=}" ;;
    esac
done

if [ -n "$CONFIG_ARG" ]; then
    source "$SCRIPT_DIR/scripts/read-config.sh" "$CONFIG_ARG"
else
    source "$SCRIPT_DIR/scripts/read-config.sh"
fi

# Apply CLI overrides on top of config values
LEGACY=$( [ "$EMU_OS" = "legacy" ] && echo true || echo false )
ESP32=$( [ "$EMU_COPROCESSOR" = "dual-machine" ] && echo true || echo false )
MONITOR_PORT="$EMU_MONITOR_PORT"

POSITIONAL_ARGS=()

while [[ $# -gt 0 ]]; do
    case "$1" in
        --legacy)
            LEGACY=true
            if [[ -n "$2" && ! "$2" == -* ]]; then
                echo "ERROR: '--legacy DIR' no longer accepts a directory argument."
                echo "       Set MEADOW_EMULATOR_OS_DIR=DIR instead (see README)."
                exit 1
            fi
            ;;
        --rebase)
            LEGACY=false
            if [[ -n "$2" && ! "$2" == -* ]]; then
                echo "ERROR: '--rebase DIR' no longer accepts a directory argument."
                echo "       Set MEADOW_EMULATOR_OS_DIR=DIR instead (see README)."
                exit 1
            fi
            ;;
        --esp32)
            ESP32=true
            ;;
        --port=*)
            MONITOR_PORT="${1#--port=}"
            ;;
        --config=*)
            ;; # already handled above
        --help|-h)
            echo "Usage: ./run.sh [options] [script-name]"
            echo ""
            echo "Options:"
            echo "  --config=FILE   Use a specific config file (default: emulator.config.yaml)"
            echo "  --legacy        Override OS to legacy (same as os: legacy in config)"
            echo "  --rebase        Override OS to meadow-os (same as os: meadow-os in config)"
            echo "  --esp32         Override coprocessor to dual-machine"
            echo "  --port=N        Override Renode monitor port"
            echo ""
            echo "Override sibling-repo locations with MEADOW_EMULATOR_OS_DIR,"
            echo "MEADOW_EMULATOR_CLI_DIR, MEADOW_EMULATOR_ESP_DIR, MEADOW_EMULATOR_SAMPLES_DIR"
            echo "(see README). If unset, each is auto-detected from sibling directories."
            echo ""
            echo "Config files:"
            echo "  configs/legacy.yaml       Legacy OS, F7FeatherV2, stub ESP32"
            echo "  configs/meadow-os.yaml    New Meadow.OS rebase, F7FeatherV2"
            echo ""
            echo "Examples:"
            echo "  ./run.sh                                        # Uses emulator.config.yaml"
            echo "  ./run.sh --config configs/meadow-os.yaml        # Use specific config"
            echo "  ./run.sh --legacy --esp32                       # Override config"
            exit 0
            ;;
        *)
            POSITIONAL_ARGS+=("$1")
            ;;
    esac
    shift
done

# Restore positional arguments for SCRIPT variable
set -- "${POSITIONAL_ARGS[@]}"
SCRIPT="$1"

# Resolve sibling-repo locations (MEADOW_EMULATOR_OS_DIR, _CLI_DIR, _ESP_DIR, _SAMPLES_DIR)
source "$SCRIPT_DIR/scripts/resolve-locations.sh" "$( [ "$LEGACY" = true ] && echo legacy || echo rebase )"

# Select default script based on config + overrides
if [ -z "$SCRIPT" ]; then
    if [ "$EMU_RUNTIME" = "legacy-os_modern-dotnet" ]; then
        SCRIPT="meadow-dotnet10-headless"
    elif [ "$LEGACY" = true ] && [ "$ESP32" = true ]; then
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
        echo "       Run: ./build-meadow.os-emulated.sh --config configs/legacy.yaml"
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
elif [ "$EMU_RUNTIME" = "legacy-os_modern-dotnet" ]; then
    if [ ! -f "$SCRIPT_DIR/build/dotnet10/nuttx.bin" ]; then
        echo "ERROR: nuttx.bin not found in build/dotnet10/"
        echo "       Run ./build-meadow.os-emulated.sh with configs/legacy-os_modern-dotnet.yaml first"
        exit 1
    fi
    # Check hook addresses are generated and not stale
    ADDR_FILE="$SCRIPT_DIR/build/dotnet10/addresses.resc"
    ELF_FILE="$SCRIPT_DIR/build/dotnet10/nuttx.elf"
    if [ ! -f "$ADDR_FILE" ]; then
        echo "WARNING: Hook addresses not generated. Extracting now..."
        python3 "$SCRIPT_DIR/tools/extract-hook-addresses.py" \
            "$SCRIPT_DIR/build/dotnet10/nuttx.elf" "$SCRIPT_DIR/build/dotnet10/nuttx_user.elf"
    elif [ -f "$ELF_FILE" ] && [ "$ELF_FILE" -nt "$ADDR_FILE" ]; then
        echo "WARNING: ELF is newer than addresses.resc — regenerating hook addresses..."
        python3 "$SCRIPT_DIR/tools/extract-hook-addresses.py" \
            "$SCRIPT_DIR/build/dotnet10/nuttx.elf" "$SCRIPT_DIR/build/dotnet10/nuttx_user.elf"
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
    # ESP32 firmware — look in MEADOW_EMULATOR_ESP_DIR (resolved above), or ESP32_FW_PATH
    # for a direct path to an already-built ELF
    ESP32_FW=""
    for candidate in \
        "${ESP32_FW_PATH:-}" \
        "$MEADOW_EMULATOR_ESP_DIR/build/MeadowComms.elf" \
    ; do
        if [ -f "$candidate" ]; then
            ESP32_FW="$(cd "$(dirname "$candidate")" && pwd)/$(basename "$candidate")"
            break
        fi
    done

    if [ -z "$ESP32_FW" ]; then
        echo "ERROR: ESP32 firmware (MeadowComms.elf) not found."
        echo "       Expected at \$MEADOW_EMULATOR_ESP_DIR/build/ (currently: $MEADOW_EMULATOR_ESP_DIR)"
        echo "       or set ESP32_FW_PATH to point directly at a built ELF."
        echo "       Build it:  cd $MEADOW_EMULATOR_ESP_DIR && ./build.sh"
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
