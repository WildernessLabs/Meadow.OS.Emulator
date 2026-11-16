#!/bin/bash
# resolve-locations.sh — Resolve sibling-repo directory locations for the emulator scripts.
#
# Usage:
#   source scripts/resolve-locations.sh legacy    # OS dir resolves against legacy candidates
#   source scripts/resolve-locations.sh rebase    # OS dir resolves against rebase candidates
#   source scripts/resolve-locations.sh           # defaults to legacy
#
# Any mode string other than the literal "legacy" is treated as rebase (so "rebase",
# "meadow-os", etc. all work without translation at the call site).
#
# Resolution rule: an explicit env var always wins, used verbatim with no existence
# check (same behavior as the pre-existing RENODE_PATH/ESP32_FW_PATH overrides). If
# unset, auto-detect against a sibling-directory fallback chain; if nothing matches,
# fall back to an unverified default path so callers still have something concrete to
# report in "not found at X" errors.
#
# Exports:
#   MEADOW_EMULATOR_OS_DIR       — Meadow OS source repo (legacy or rebase, per mode)
#   MEADOW_EMULATOR_CLI_DIR      — Meadow.CLI repo
#   MEADOW_EMULATOR_SAMPLES_DIR  — Meadow.Samples repo
#   MEADOW_EMULATOR_ESP_DIR      — ESP32 coprocessor firmware repo
#
# Note: RENODE_PATH and ESP32_FW_PATH are separate, pre-existing, more-specific
# overrides (a Renode checkout and a direct path to a built .elf, respectively).
# They are not part of this resolver and are unaffected by it.

_RL_SCRIPT_DIR="$( cd "$(dirname "${BASH_SOURCE[0]:-$0}")/.." ; pwd -P )"
_RL_PARENT_DIR="$(dirname "$_RL_SCRIPT_DIR")"
_RL_MODE="${1:-legacy}"

if [ -z "$MEADOW_EMULATOR_OS_DIR" ]; then
    if [ "$_RL_MODE" = "legacy" ]; then
        if [ -d "$_RL_PARENT_DIR/Meadow" ]; then
            MEADOW_EMULATOR_OS_DIR="$_RL_PARENT_DIR/Meadow"
        elif [ -d "$_RL_PARENT_DIR/Legacy" ]; then
            MEADOW_EMULATOR_OS_DIR="$_RL_PARENT_DIR/Legacy"
        else
            MEADOW_EMULATOR_OS_DIR="$_RL_PARENT_DIR/Meadow"
        fi
    else
        if [ -d "$_RL_PARENT_DIR/Meadow.OS" ]; then
            MEADOW_EMULATOR_OS_DIR="$_RL_PARENT_DIR/Meadow.OS"
        elif [ -d "$_RL_PARENT_DIR/Rebase" ]; then
            MEADOW_EMULATOR_OS_DIR="$_RL_PARENT_DIR/Rebase"
        else
            MEADOW_EMULATOR_OS_DIR="$_RL_PARENT_DIR/Meadow.OS"
        fi
    fi
fi

: "${MEADOW_EMULATOR_CLI_DIR:=$_RL_PARENT_DIR/Meadow.CLI}"
: "${MEADOW_EMULATOR_SAMPLES_DIR:=$_RL_PARENT_DIR/Meadow.Samples}"

if [ -z "$MEADOW_EMULATOR_ESP_DIR" ]; then
    if [ -d "$_RL_PARENT_DIR/Meadow-ESP32" ]; then
        MEADOW_EMULATOR_ESP_DIR="$_RL_PARENT_DIR/Meadow-ESP32"
    elif [ -d "$MEADOW_EMULATOR_OS_DIR/esp32" ]; then
        MEADOW_EMULATOR_ESP_DIR="$MEADOW_EMULATOR_OS_DIR/esp32"
    else
        MEADOW_EMULATOR_ESP_DIR="$_RL_PARENT_DIR/Meadow-ESP32"
    fi
fi

export MEADOW_EMULATOR_OS_DIR MEADOW_EMULATOR_CLI_DIR MEADOW_EMULATOR_SAMPLES_DIR MEADOW_EMULATOR_ESP_DIR
