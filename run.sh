#!/bin/bash
set -e

SCRIPT_DIR="$( cd "$(dirname "$0")" ; pwd -P )"

# Find Renode — check PATH first, then known macOS location
if command -v renode &> /dev/null; then
    RENODE="renode"
elif [ -f "/Applications/Renode.app/Contents/MacOS/bin/Renode.exe" ]; then
    # macOS: Renode runs via Mono
    RENODE="mono /Applications/Renode.app/Contents/MacOS/bin/Renode.exe"
else
    echo "ERROR: Renode not found."
    echo "       Install from https://renode.io/download"
    echo "       Or add 'renode' to your PATH"
    exit 1
fi

# Default to meadow-os script, or pass a custom .resc file
SCRIPT="${1:-meadow-os}"

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

# Check for built binary
if [ ! -f "$SCRIPT_DIR/build/nuttx.bin" ]; then
    echo "ERROR: nuttx.bin not found in build/"
    echo "       Run ./build-emulator.sh first"
    exit 1
fi

echo "=== Starting Renode with $SCRIPT ==="
cd "$SCRIPT_DIR"
"$RENODE" --disable-xwt --console --execute "include @$SCRIPT"
