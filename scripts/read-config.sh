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
#   EMU_RUNTIME       — (empty) | legacy-os_modern-dotnet
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

# Parse arguments.  Only these name the config: --config=FILE, --config FILE,
# or an argument ending in .yaml/.yml.  Anything else is ignored: a script
# sourced with no arguments sees its caller's arguments, which can include
# unrelated existing files (run.sh's .resc script, make-lfs-image.sh's output
# image), and those must not be taken for the config.
_rc_next_is_config=false
for arg in "$@"; do
    if [ "$_rc_next_is_config" = true ]; then
        EMU_CONFIG_FILE="$arg"
        _rc_next_is_config=false
        continue
    fi
    case "$arg" in
        --config=*)   EMU_CONFIG_FILE="${arg#--config=}" ;;
        --config)     _rc_next_is_config=true ;;
        *.yaml|*.yml) EMU_CONFIG_FILE="$arg" ;;
    esac
done
unset _rc_next_is_config

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
print(f'export EMU_RUNTIME=\"{val(\"build.runtime\", \"\")}\"')
print(f'export EMU_HCOM_PORT=\"{val(\"ports.hcom\", \"4242\")}\"')
print(f'export EMU_MONITOR_PORT=\"{val(\"ports.monitor\", \"1234\")}\"')
print(f'export EMU_ESP32CTRL_PORT=\"{val(\"ports.esp32ctrl\", \"4243\")}\"')
print(f'export EMU_GDB_PORT=\"{val(\"ports.gdb\", \"0\")}\"')
print(f'export EMU_CONSOLE_LOG=\"{val(\"debug.console_log\", \"/tmp/renode-uart.txt\")}\"')
print(f'export EMU_HEADLESS=\"{val(\"debug.headless\", \"true\")}\"')
print(f'export EMU_CONFIG_FILE=\"$EMU_CONFIG_FILE\"')

# Map board name to GPIO version number for the firmware hook
board = val('board', 'F7FeatherV2')
board_ver = dict(F7FeatherV1=1, F7FeatherV2=2, F7CoreComputeV2=3).get(board, 2)
print(f'export EMU_BOARD_VERSION=\"' + str(board_ver) + '\"')
" 2>&1)"

if [ $? -ne 0 ]; then
    echo "ERROR: Failed to parse config file: $EMU_CONFIG_FILE"
    return 1 2>/dev/null || exit 1
fi

# Validate config values
_RC_VALID=true
case "$EMU_OS" in
    legacy|meadow-os) ;;
    *) echo "ERROR: Invalid os '$EMU_OS' in $EMU_CONFIG_FILE (valid: legacy, meadow-os)"; _RC_VALID=false ;;
esac
case "$EMU_BOARD" in
    F7FeatherV1|F7FeatherV2|F7CoreComputeV2) ;;
    *) echo "ERROR: Invalid board '$EMU_BOARD' in $EMU_CONFIG_FILE (valid: F7FeatherV1, F7FeatherV2, F7CoreComputeV2)"; _RC_VALID=false ;;
esac
case "$EMU_COPROCESSOR" in
    stub|dual-machine) ;;
    *) echo "ERROR: Invalid coprocessor '$EMU_COPROCESSOR' in $EMU_CONFIG_FILE (valid: stub, dual-machine)"; _RC_VALID=false ;;
esac
case "$EMU_RUNTIME" in
    ""|legacy-os_modern-dotnet) ;;
    *) echo "ERROR: Invalid build.runtime '$EMU_RUNTIME' in $EMU_CONFIG_FILE (valid: (empty), legacy-os_modern-dotnet)"; _RC_VALID=false ;;
esac
if ! [[ "$EMU_HCOM_PORT" =~ ^[0-9]+$ ]] || [ "$EMU_HCOM_PORT" -lt 1 ] || [ "$EMU_HCOM_PORT" -gt 65535 ]; then
    echo "ERROR: Invalid ports.hcom '$EMU_HCOM_PORT' (must be 1-65535)"; _RC_VALID=false
fi
if [ "$_RC_VALID" = false ]; then
    return 1 2>/dev/null || exit 1
fi
