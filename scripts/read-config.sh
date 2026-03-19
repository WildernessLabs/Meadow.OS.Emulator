#!/bin/bash
# read-config.sh — Parse emulator.config.yaml and export shell variables.
#
# Usage:
#   source scripts/read-config.sh                          # reads emulator.config.yaml
#   source scripts/read-config.sh configs/meadow-os.yaml   # reads specific config
#   source scripts/read-config.sh --config path.yaml       # same, with --config flag
#
# Exports:
#   EMU_OS            — legacy | meadow-os
#   EMU_BOARD         — F7FeatherV2 | F7CoreComputeV2
#   EMU_COPROCESSOR   — stub | dual-machine
#   EMU_DEFCONFIG     — NuttX defconfig name (e.g., mono, meadow)
#   EMU_HCOM_PORT     — HCOM TCP socket port
#   EMU_MONITOR_PORT  — Renode monitor port
#   EMU_ESP32CTRL_PORT — ESP32 control channel port
#   EMU_GDB_PORT      — GDB server port (0 = disabled)
#   EMU_CONSOLE_LOG   — UART console log file path
#   EMU_HEADLESS      — true | false

_RC_SCRIPT_DIR="$( cd "$(dirname "${BASH_SOURCE[0]:-$0}")" ; pwd -P )"
_RC_REPO_DIR="$(dirname "$_RC_SCRIPT_DIR")"

# Determine config file path — check repo root first, then current dir
if [ -f "${_RC_REPO_DIR}/emulator.config.yaml" ]; then
    EMU_CONFIG_FILE="${_RC_REPO_DIR}/emulator.config.yaml"
elif [ -f "emulator.config.yaml" ]; then
    EMU_CONFIG_FILE="$(pwd)/emulator.config.yaml"
else
    EMU_CONFIG_FILE="${_RC_REPO_DIR}/emulator.config.yaml"
fi

# Parse arguments
for arg in "$@"; do
    case "$arg" in
        --config=*)
            EMU_CONFIG_FILE="${arg#--config=}"
            ;;
        --config)
            # Next arg is the path — handled by caller
            ;;
        *)
            # Positional arg = config path
            if [ -f "$arg" ]; then
                EMU_CONFIG_FILE="$arg"
            fi
            ;;
    esac
done

if [ ! -f "$EMU_CONFIG_FILE" ]; then
    echo "ERROR: Config file not found: $EMU_CONFIG_FILE"
    echo "       Create one by copying a template: cp configs/legacy.yaml emulator.config.yaml"
    return 1 2>/dev/null || exit 1
fi

# Parse YAML with python3 (PyYAML required — same dependency as extract-hook-addresses.py)
eval "$(python3 -c "
import yaml, sys

with open('$EMU_CONFIG_FILE') as f:
    c = yaml.safe_load(f)

def val(keys, default=''):
    v = c
    for k in keys.split('.'):
        if isinstance(v, dict) and k in v:
            v = v[k]
        else:
            return default
    return str(v) if v is not None else default

print(f'export EMU_OS=\"{val(\"os\", \"legacy\")}\"')
print(f'export EMU_BOARD=\"{val(\"board\", \"F7FeatherV2\")}\"')
print(f'export EMU_COPROCESSOR=\"{val(\"coprocessor\", \"stub\")}\"')
print(f'export EMU_DEFCONFIG=\"{val(\"build.defconfig\", \"mono\")}\"')
print(f'export EMU_HCOM_PORT=\"{val(\"ports.hcom\", \"4242\")}\"')
print(f'export EMU_MONITOR_PORT=\"{val(\"ports.monitor\", \"1234\")}\"')
print(f'export EMU_ESP32CTRL_PORT=\"{val(\"ports.esp32ctrl\", \"4243\")}\"')
print(f'export EMU_GDB_PORT=\"{val(\"ports.gdb\", \"0\")}\"')
print(f'export EMU_CONSOLE_LOG=\"{val(\"debug.console_log\", \"/tmp/renode-uart.txt\")}\"')
print(f'export EMU_HEADLESS=\"{val(\"debug.headless\", \"true\")}\"')
print(f'export EMU_CONFIG_FILE=\"$EMU_CONFIG_FILE\"')
" 2>&1)"

if [ $? -ne 0 ]; then
    echo "ERROR: Failed to parse config file: $EMU_CONFIG_FILE"
    return 1 2>/dev/null || exit 1
fi
