#!/bin/bash

SCRIPT_DIR="$( cd "$(dirname "$0")" ; pwd -P )"
PARENT_DIR="$(dirname "$SCRIPT_DIR")"

# Colors (only in interactive shells)
if [[ -t 1 ]]; then
    GREEN='\033[0;32m'
    YELLOW='\033[0;33m'
    RED='\033[0;31m'
    BOLD='\033[1m'
    RESET='\033[0m'
else
    GREEN=''; YELLOW=''; RED=''; BOLD=''; RESET=''
fi

# Tracking
INSTALLED=()
ALREADY_OK=()
FAILED=()
WARNINGS=()

ok()      { ALREADY_OK+=("$1"); printf "  ${GREEN}✓${RESET} %s\n" "$1"; }
added()   { INSTALLED+=("$1");  printf "  ${GREEN}+${RESET} %s ${YELLOW}(installed)${RESET}\n" "$1"; }
fail()    { FAILED+=("$1");     printf "  ${RED}✗${RESET} %s\n" "$1"; }
warn()    { WARNINGS+=("$1");   printf "  ${YELLOW}!${RESET} %s\n" "$1"; }
section() { printf "\n${BOLD}%s${RESET}\n" "$1"; }

execute_command() {
    if [ "$DRYRUN" = true ]; then
        printf "  ${YELLOW}[DRYRUN]${RESET} %s\n" "$*"
        return 0
    else
        eval "$@"
    fi
}

# --- Parse arguments ---

MODE="legacy"
DRYRUN=false

while [[ $# -gt 0 ]]; do
    case "$1" in
        --legacy)
            MODE="legacy"
            if [[ -n "$2" && ! "$2" == -* ]]; then
                echo "ERROR: '--legacy DIR' no longer accepts a directory argument."
                echo "       Set MEADOW_EMULATOR_OS_DIR=DIR instead (see README)."
                exit 1
            fi
            ;;
        --rebase)
            MODE="rebase"
            if [[ -n "$2" && ! "$2" == -* ]]; then
                echo "ERROR: '--rebase DIR' no longer accepts a directory argument."
                echo "       Set MEADOW_EMULATOR_OS_DIR=DIR instead (see README)."
                exit 1
            fi
            ;;
        --dryrun)  DRYRUN=true ;;
        -h|--help)
            echo "Usage: ./setup.sh [--legacy|--rebase|--dryrun]"
            echo ""
            echo "  --legacy        Set up for legacy Meadow OS (default dir: ../Meadow/)"
            echo "  --rebase        Set up for rebased Meadow OS (default dir: ../Meadow.OS/)"
            echo "  --dryrun        Show commands that would be run without executing them"
            echo ""
            echo "Override sibling-repo locations with MEADOW_EMULATOR_OS_DIR,"
            echo "MEADOW_EMULATOR_CLI_DIR, MEADOW_EMULATOR_ESP_DIR, MEADOW_EMULATOR_SAMPLES_DIR"
            echo "(see README). If unset, each is auto-detected from sibling directories."
            echo ""
            echo "Checks and installs all prerequisites for building and running"
            echo "the Meadow.OS Emulator, including firmware build tools, custom"
            echo "Renode fork, .NET SDK, and Meadow CLI.  For Legacy it also installs"
            echo "esp-emu and Bumble (optional, for ./run.sh --esp-emu)."
            exit 0
            ;;
        *)
            echo "Unknown argument: $1"
            echo "Usage: ./setup.sh [--legacy|--rebase|--dryrun]"
            exit 1
            ;;
    esac
    shift
done

source "$SCRIPT_DIR/scripts/resolve-locations.sh" "$MODE"
MEADOW_DIR="$MEADOW_EMULATOR_OS_DIR"

echo "==========================================="
echo " Meadow.OS Emulator — Setup ($MODE mode)"
if [ "$DRYRUN" = true ]; then
    echo " *** DRY RUN MODE — No changes will be made ***"
fi
echo "==========================================="

# ============================================================
# 1. Platform detection
# ============================================================
section "Platform"

OS="$(uname -s)"
ARCH="$(uname -m)"
printf "  %s %s\n" "$OS" "$ARCH"

if [ "$OS" = "Darwin" ]; then
    PLATFORM="macos"
elif [ "$OS" = "Linux" ]; then
    PLATFORM="linux"
else
    echo "  Unsupported platform: $OS"
    exit 1
fi

# ============================================================
# 2. Package manager
# ============================================================
section "Package Manager"

if [ "$PLATFORM" = "macos" ]; then
    if command -v brew &> /dev/null; then
        ok "Homebrew"
    else
        warn "Homebrew not found — install from https://brew.sh"
        FAILED+=("Homebrew")
    fi
elif [ "$PLATFORM" = "linux" ]; then
    if command -v apt-get &> /dev/null; then
        ok "apt-get"
    else
        warn "apt-get not found — only Debian/Ubuntu is supported"
    fi
fi

# ============================================================
# 3. .NET SDK
# ============================================================
section ".NET SDK"

# Check common install locations
if ! command -v dotnet &> /dev/null; then
    for p in /usr/local/share/dotnet /opt/homebrew/bin ~/.dotnet; do
        if [ -x "$p/dotnet" ]; then
            execute_command "export PATH=\"$p:\$PATH\""
            break
        fi
    done
fi

if command -v dotnet &> /dev/null; then
    DOTNET_VER=$(dotnet --version 2>/dev/null || echo "unknown")
    ok ".NET SDK $DOTNET_VER"
else
    if [ "$PLATFORM" = "macos" ] && command -v brew &> /dev/null; then
        echo "  Installing .NET SDK via Homebrew..."
        if execute_command "brew install dotnet 2>/dev/null"; then
            added ".NET SDK"
        else
            fail ".NET SDK — brew install failed"
        fi
    else
        fail ".NET SDK — install from https://dotnet.microsoft.com/download"
    fi
fi

# Meadow.CLI, the dotnet global tool.  The emulator's HCOM link is a TCP
# socket (socket://localhost:4242); released Meadow.CLI supports socket routes
# from 3.0.3, so no source build is needed.
MEADOW_CLI_MIN="3.0.3"
version_lt() {   # true if $1 is an older version than $2
    [ "$1" != "$2" ] && [ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | head -1)" = "$1" ]
}
if command -v dotnet &> /dev/null; then
    MEADOW_CLI_VER=$(dotnet tool list -g 2>/dev/null \
        | awk 'tolower($1) == "wildernesslabs.meadow.cli" { print $2 }')
    if [ -z "$MEADOW_CLI_VER" ]; then
        echo "  Installing Meadow.CLI (dotnet global tool)..."
        if execute_command "dotnet tool install -g WildernessLabs.Meadow.CLI"; then
            added "Meadow.CLI"
        else
            fail "Meadow.CLI — dotnet tool install -g WildernessLabs.Meadow.CLI failed"
        fi
    elif version_lt "$MEADOW_CLI_VER" "$MEADOW_CLI_MIN"; then
        echo "  Updating Meadow.CLI $MEADOW_CLI_VER (socket:// routes need $MEADOW_CLI_MIN or later)..."
        if execute_command "dotnet tool update -g WildernessLabs.Meadow.CLI"; then
            added "Meadow.CLI (updated from $MEADOW_CLI_VER)"
        else
            fail "Meadow.CLI $MEADOW_CLI_VER — $MEADOW_CLI_MIN or later needed; dotnet tool update -g WildernessLabs.Meadow.CLI failed"
        fi
    else
        ok "Meadow.CLI $MEADOW_CLI_VER"
    fi
    if ! command -v meadow &> /dev/null && [ "$DRYRUN" != true ]; then
        warn "meadow is not on PATH — add ~/.dotnet/tools to PATH"
    fi
fi

# ============================================================
# 4. Sibling repos
# ============================================================
section "Sibling Repos"

check_repo() {
    local name="$1"
    local path="$2"
    local branch="$3"

    if [ -d "$path" ]; then
        if [ -n "$branch" ]; then
            local current=$(git -C "$path" branch --show-current 2>/dev/null)
            if [ "$current" = "$branch" ]; then
                ok "$name (branch: $branch)"
            else
                ok "$name (branch: $current)"
                warn "$name — expected branch '$branch', on '$current'"
            fi
        else
            ok "$name"
        fi
        return 0
    else
        fail "$name — not found at $path"
        return 1
    fi
}

check_repo "$(basename "$MEADOW_DIR")" "$MEADOW_DIR"
# Meadow.CLI comes from the dotnet global tool (above); a source checkout in
# $MEADOW_EMULATOR_CLI_DIR is only needed to work on the CLI itself.
check_repo "Meadow.Samples" "$MEADOW_EMULATOR_SAMPLES_DIR"

# ============================================================
# 5. Meadow OS build tools (delegate to upstream repo)
# ============================================================
section "Meadow OS Build Tools"

if [ -d "$MEADOW_DIR" ]; then
    # ARM GCC
    if command -v arm-none-eabi-gcc &> /dev/null; then
        ok "arm-none-eabi-gcc ($(arm-none-eabi-gcc --version 2>&1 | head -1))"
    elif [ -d "/Applications/ARM/bin" ]; then
        execute_command "export PATH=\"/Applications/ARM/bin:\$PATH\""
        ok "arm-none-eabi-gcc (/Applications/ARM/bin)"
    else
        if [ "$PLATFORM" = "macos" ] && [ -f "$MEADOW_DIR/install-packages.sh" ]; then
            echo "  Running $(basename "$MEADOW_DIR")/install-packages.sh..."
            if execute_command "bash \"$MEADOW_DIR/install-packages.sh\""; then
                added "Meadow build packages (via install-packages.sh)"
            else
                fail "install-packages.sh failed"
            fi
        elif [ "$PLATFORM" = "macos" ]; then
            fail "arm-none-eabi-gcc — install ARM GCC 10.3 from https://developer.arm.com/downloads/-/gnu-rm"
        else
            fail "arm-none-eabi-gcc — run: sudo apt-get install gcc-arm-none-eabi"
        fi
    fi

    # GNU Make 4.x+ (macOS ships 3.81)
    if [ "$PLATFORM" = "macos" ]; then
        if command -v gmake &> /dev/null; then
            ok "GNU Make ($(gmake --version | head -1))"
        else
            echo "  Installing GNU Make via Homebrew..."
            if execute_command "brew install make 2>/dev/null"; then
                added "GNU Make (gmake)"
            else
                fail "GNU Make — brew install make"
            fi
        fi
    else
        ok "Make ($(make --version | head -1))"
    fi

    # kconfig (needed for NuttX configure)
    # The emulator repo ships kconfig-tweak and kconfig-conf shims in tools/
    if [ -x "$SCRIPT_DIR/tools/kconfig-tweak" ]; then
        ok "kconfig-tweak (emulator tools/ shim)"
    elif command -v kconfig-tweak &> /dev/null; then
        ok "kconfig-tweak (system)"
    else
        warn "kconfig-tweak — not found. The build script adds tools/ to PATH, but system install may help."
        if [ -f "$MEADOW_DIR/build-tools.sh" ]; then
            warn "  Run: cd $MEADOW_DIR && ./build-tools.sh"
        fi
    fi

    # Additional Homebrew packages (if install-packages.sh wasn't run)
    if [ "$PLATFORM" = "macos" ] && command -v brew &> /dev/null; then
        for pkg in ccache autoconf libtool cmake automake; do
            if brew ls --versions "$pkg" &> /dev/null; then
                ok "$pkg"
            else
                echo "  Installing $pkg..."
                if execute_command "brew install \"$pkg\" 2>/dev/null"; then
                    added "$pkg"
                else
                    fail "$pkg"
                fi
            fi
        done
    fi

    # ld.lld (LLVM linker — in Meadow/toolchain/)
    TOOLCHAIN_DIR="$MEADOW_DIR/toolchain"
    if [ "$PLATFORM" = "macos" ] && [ -d "$TOOLCHAIN_DIR/macos" ]; then
        ok "ld.lld (toolchain/macos)"
    elif [ "$PLATFORM" = "linux" ] && [ -d "$TOOLCHAIN_DIR/linux" ]; then
        ok "ld.lld (toolchain/linux)"
    elif command -v ld.lld &> /dev/null; then
        ok "ld.lld (system)"
    else
        warn "ld.lld — LLVM linker not found. May be needed for firmware link step."
    fi
else
    warn "Skipping build tools — $(basename "$MEADOW_DIR") repo not found"
fi

# ============================================================
# 6. Mono runtime (pre-built check)
# ============================================================
section "Mono Runtime"

if [ -d "$MEADOW_DIR" ]; then
    MONO_LIB="$MEADOW_DIR/mono/mono/sgen/.libs/libmonosgen.a"
    if [ -f "$MONO_LIB" ]; then
        ok "libmonosgen.a (pre-built)"
    else
        warn "Mono runtime not built — run: cd $MEADOW_DIR && ./build-mono.sh"
    fi
else
    warn "Skipping — $(basename "$MEADOW_DIR") repo not found"
fi

# ============================================================
# 7. Custom Renode fork
# ============================================================
section "Renode (WildernessLabs fork)"

RENODE_DIR="$PARENT_DIR/renode"
RENODE_DLL="$RENODE_DIR/output/bin/Release/Renode.dll"

if [ -d "$RENODE_DIR" ]; then
    RENODE_BRANCH=$(git -C "$RENODE_DIR" branch --show-current 2>/dev/null)
    if [ "$RENODE_BRANCH" = "meadow-emulator-fixes" ]; then
        ok "renode/ repo (branch: meadow-emulator-fixes)"
    else
        ok "renode/ repo (branch: $RENODE_BRANCH)"
        warn "Expected branch 'meadow-emulator-fixes'"
    fi

    # Check submodule
    if [ -f "$RENODE_DIR/src/Infrastructure/src/Emulator/Cores/Arm-M/NVIC.cs" ]; then
        ok "Infrastructure submodule initialized"
    else
        echo "  Initializing Infrastructure submodule..."
        if execute_command "git -C \"$RENODE_DIR\" submodule update --init src/Infrastructure"; then
            added "Infrastructure submodule"
        else
            fail "Submodule init failed"
        fi
    fi

    # Check built
    if [ -f "$RENODE_DLL" ]; then
        ok "Renode built ($(ls -lh "$RENODE_DLL" | awk '{print $5}'))"
    else
        echo "  Building Renode (this may take several minutes)..."
        BUILD_FLAGS=""
        if [ "$ARCH" = "arm64" ]; then
            BUILD_FLAGS="--host-arch aarch64"
        fi
        # Newer upstream may need relaxed warnings for AppleClang
        execute_command "export CFLAGS=\"\${CFLAGS:--Wno-error}\""
        if execute_command "(cd \"$RENODE_DIR\" && bash build.sh $BUILD_FLAGS)"; then
            added "Renode build"
        else
            fail "Renode build failed"
            warn "Try: cd ../renode && CFLAGS=\"-Wno-error\" bash build.sh $BUILD_FLAGS"
        fi
    fi
else
    echo "  Cloning WildernessLabs/renode..."
    if execute_command "git clone https://github.com/WildernessLabs/renode.git \"$RENODE_DIR\""; then
        added "renode/ repo cloned"

        echo "  Checking out meadow-emulator-fixes..."
        execute_command "git -C \"$RENODE_DIR\" checkout meadow-emulator-fixes"

        echo "  Initializing submodules..."
        execute_command "git -C \"$RENODE_DIR\" submodule update --init src/Infrastructure"

        echo "  Building Renode (this may take several minutes)..."
        BUILD_FLAGS=""
        if [ "$ARCH" = "arm64" ]; then
            BUILD_FLAGS="--host-arch aarch64"
        fi
        execute_command "export CFLAGS=\"\${CFLAGS:--Wno-error}\""
        if execute_command "(cd \"$RENODE_DIR\" && bash build.sh $BUILD_FLAGS)"; then
            added "Renode built"
        else
            fail "Renode build failed"
        fi
    else
        fail "Failed to clone WildernessLabs/renode"
    fi
fi

# Check for stock Renode (warn about missing fixes)
if [ ! -f "$RENODE_DLL" ]; then
    if command -v renode &> /dev/null || [ -f "/Applications/Renode.app/Contents/MacOS/bin/Renode.exe" ]; then
        warn "Stock Renode found but missing SVC/PRIMASK fix — emulation will be ~12,000x slower"
        warn "Build the WildernessLabs fork for usable performance"
    fi
fi

# ============================================================
# 8. ESP32 ROM binary (required for dual-machine mode)
# ============================================================
section "ESP32 ROM Binary"

ESP32_ROM="$SCRIPT_DIR/roms/esp32-v3-rom.bin"
if [ -f "$ESP32_ROM" ]; then
    ok "esp32-v3-rom.bin ($(ls -lh "$ESP32_ROM" | awk '{print $5}'))"
else
    echo "  Downloading ESP32 ROM from Espressif QEMU repo..."
    execute_command "mkdir -p \"$SCRIPT_DIR/roms\""
    if execute_command "curl -fSL -o \"$ESP32_ROM\" https://github.com/espressif/qemu/raw/esp-develop/pc-bios/esp32-v3-rom.bin 2>/dev/null"; then
        added "esp32-v3-rom.bin"
    else
        fail "ESP32 ROM download — see roms/README.md for manual instructions"
    fi
fi

# ============================================================
# 9. esp-emu coprocessor (Legacy, ./run.sh --esp-emu)
#
# Optional: everything else works without it, so a problem here is a
# warning and does not fail setup.
# ============================================================
if [ "$MODE" = "legacy" ]; then
section "esp-emu Coprocessor (optional)"

ESP_EMU_MIN="1.2.0"             # the first with the chip as an SPI slave
ESP_EMU_INSTALL="1.2.0"         # the version tested with this repo
ESP_EMU_BIN_DIR="${ESP_EMU_BIN_DIR:-$HOME/.local/bin}"
BLE_VENV="$SCRIPT_DIR/build/ble-venv"
BUMBLE_SPEC="bumble>=0.0.220,<0.1"

esp_emu_path() {
    if [ -n "${ESP_EMU:-}" ]; then
        echo "$ESP_EMU"
    elif command -v esp-emu &> /dev/null; then
        command -v esp-emu
    elif [ -x "$ESP_EMU_BIN_DIR/esp-emu" ]; then
        echo "$ESP_EMU_BIN_DIR/esp-emu"
    fi
}
esp_emu_version() {
    "$1" --version 2>/dev/null | grep -o -E '[0-9]+\.[0-9]+\.[0-9]+' | tail -1
}

ESP_EMU_PATH="$(esp_emu_path)"
ESP_EMU_VER=""
if [ -n "$ESP_EMU_PATH" ] && [ -x "$ESP_EMU_PATH" ]; then
    ESP_EMU_VER="$(esp_emu_version "$ESP_EMU_PATH")"
fi

if [ -n "$ESP_EMU_VER" ] && ! version_lt "$ESP_EMU_VER" "$ESP_EMU_MIN"; then
    ok "esp-emu $ESP_EMU_VER ($ESP_EMU_PATH)"
elif [ -n "${ESP_EMU:-}" ]; then
    # The user chose this binary, so do not install another beside it.
    warn "esp-emu at ESP_EMU=$ESP_EMU is ${ESP_EMU_VER:-not usable}; $ESP_EMU_MIN or later is needed"
elif [ "$PLATFORM" = "macos" ] && [ "$ARCH" != "arm64" ]; then
    warn "esp-emu — no release for Intel Macs; --esp-emu is not available here"
else
    echo "  Installing esp-emu $ESP_EMU_INSTALL to $ESP_EMU_BIN_DIR..."
    # The installer from the same tag as the release, not from main.
    ESP_EMU_URL="https://raw.githubusercontent.com/espressif/esp-emulator/v$ESP_EMU_INSTALL/install.sh"
    if execute_command "curl -fsSL \"$ESP_EMU_URL\" | sh -s -- --version \"$ESP_EMU_INSTALL\" --bin-dir \"$ESP_EMU_BIN_DIR\" --quiet"; then
        added "esp-emu $ESP_EMU_INSTALL"
    else
        warn "esp-emu install failed — see https://github.com/espressif/esp-emulator/releases"
    fi
fi
if [ "$DRYRUN" != true ] && [ -x "$ESP_EMU_BIN_DIR/esp-emu" ] && [ -z "${ESP_EMU:-}" ] && ! command -v esp-emu &> /dev/null; then
    warn "esp-emu is not on PATH — add $ESP_EMU_BIN_DIR to PATH, or set ESP_EMU=$ESP_EMU_BIN_DIR/esp-emu"
fi

# Bumble, for tools/ble-peer.py, in its own venv (build/ is not tracked).
if [ -x "$BLE_VENV/bin/python" ] && "$BLE_VENV/bin/python" -c "import bumble" &> /dev/null; then
    ok "Bumble (build/ble-venv)"
elif ! command -v python3 &> /dev/null; then
    warn "Bumble — python3 not found; tools/ble-peer.py needs it"
else
    echo "  Installing Bumble into build/ble-venv..."
    if execute_command "mkdir -p \"$SCRIPT_DIR/build\" && python3 -m venv \"$BLE_VENV\" && \"$BLE_VENV/bin/python\" -m pip install -q --disable-pip-version-check \"$BUMBLE_SPEC\""; then
        added "Bumble (build/ble-venv)"
    else
        warn "Bumble install failed — tools/ble-peer.py will not run"
    fi
fi

# The firmware is not built here: it needs ESP-IDF 6.1 and takes some minutes.
ESP_EMU_FW="${ESPEMU_FW_PATH:-$MEADOW_EMULATOR_ESP_DIR/build-esp32s3/merged_flash.bin}"
if [ -f "$ESP_EMU_FW" ]; then
    ok "ESP32-S3 firmware ($ESP_EMU_FW)"
elif [ -f "$MEADOW_EMULATOR_ESP_DIR/build.sh" ] && grep -q -- "--emulator" "$MEADOW_EMULATOR_ESP_DIR/build.sh"; then
    warn "ESP32-S3 firmware not built (needs ESP-IDF 6.1) — run: cd $MEADOW_EMULATOR_ESP_DIR && ./build.sh --emulator --build"
else
    warn "ESP32-S3 firmware — $MEADOW_EMULATOR_ESP_DIR has no build.sh --emulator; update it to Meadow-ESP32 develop (or set MEADOW_EMULATOR_ESP_DIR)"
fi
fi

# ============================================================
# 10. Firmware build output
# ============================================================
section "Firmware Build"

if [ "$MODE" = "legacy" ]; then
    BUILD_OUT="$SCRIPT_DIR/build/legacy/nuttx.bin"
else
    BUILD_OUT="$SCRIPT_DIR/build/nuttx.bin"
fi

if [ -f "$BUILD_OUT" ]; then
    ok "Firmware built ($(ls -lh "$BUILD_OUT" | awk '{print $5}'))"
else
    if [ "$MODE" = "legacy" ]; then
        warn "Firmware not built — run: ./build-meadow.os_legacy-emulated.sh"
    else
        warn "Firmware not built — run: ./build-meadow.os-emulated.sh"
    fi
fi

# ============================================================
# Summary
# ============================================================
echo ""
echo "==========================================="
printf " ${BOLD}Setup Summary${RESET}\n"
echo "==========================================="

if [ ${#ALREADY_OK[@]} -gt 0 ]; then
    printf " ${GREEN}Already installed:${RESET} %d items\n" ${#ALREADY_OK[@]}
fi

if [ ${#INSTALLED[@]} -gt 0 ]; then
    printf " ${GREEN}Newly installed:${RESET}\n"
    for item in "${INSTALLED[@]}"; do
        printf "   + %s\n" "$item"
    done
fi

if [ ${#WARNINGS[@]} -gt 0 ]; then
    printf " ${YELLOW}Warnings:${RESET}\n"
    for item in "${WARNINGS[@]}"; do
        printf "   ! %s\n" "$item"
    done
fi

if [ ${#FAILED[@]} -gt 0 ]; then
    printf " ${RED}Failed:${RESET}\n"
    for item in "${FAILED[@]}"; do
        printf "   ✗ %s\n" "$item"
    done
    echo ""
    echo " Some prerequisites are missing. Fix the above and re-run ./setup.sh"
    exit 1
fi

echo ""
if [ ! -f "$BUILD_OUT" ]; then
    echo " Next steps:"
    if [ "$MODE" = "legacy" ]; then
        echo "   1. ./build-meadow.os_legacy-emulated.sh   # Build firmware"
        echo "   2. ./run.sh --legacy                      # Start emulator"
    else
        echo "   1. ./build-meadow.os-emulated.sh          # Build firmware"
        echo "   2. ./run.sh                               # Start emulator"
    fi
else
    echo " Ready! Run the emulator:"
    if [ "$MODE" = "legacy" ]; then
        echo "   ./run.sh --legacy"
    else
        echo "   ./run.sh"
    fi
fi
echo ""
