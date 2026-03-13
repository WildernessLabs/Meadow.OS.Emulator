#!/bin/bash
set -e

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

# --- Parse arguments ---

MODE="legacy"

for arg in "$@"; do
    case "$arg" in
        --legacy)  MODE="legacy" ;;
        --rebase)  MODE="rebase" ;;
        -h|--help)
            echo "Usage: ./setup.sh [--legacy|--rebase]"
            echo ""
            echo "  --legacy   Set up for legacy Meadow OS (../Meadow/) [default]"
            echo "  --rebase   Set up for rebased Meadow OS (../Meadow.OS/)"
            echo ""
            echo "Checks and installs all prerequisites for building and running"
            echo "the Meadow.OS Emulator, including firmware build tools, custom"
            echo "Renode fork, .NET SDK, and Meadow CLI."
            exit 0
            ;;
        *)
            echo "Unknown argument: $arg"
            echo "Usage: ./setup.sh [--legacy|--rebase]"
            exit 1
            ;;
    esac
done

if [ "$MODE" = "legacy" ]; then
    MEADOW_REPO="Meadow"
else
    MEADOW_REPO="Meadow.OS"
fi

echo "==========================================="
echo " Meadow.OS Emulator — Setup ($MODE mode)"
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
            export PATH="$p:$PATH"
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
        if brew install dotnet 2>/dev/null; then
            added ".NET SDK"
        else
            fail ".NET SDK — brew install failed"
        fi
    else
        fail ".NET SDK — install from https://dotnet.microsoft.com/download"
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

check_repo "$MEADOW_REPO" "$PARENT_DIR/$MEADOW_REPO"
check_repo "Meadow.CLI" "$PARENT_DIR/Meadow.CLI" "feature/TCP_Socket_Connection_for_EMU"
check_repo "Meadow.Samples" "$PARENT_DIR/Meadow.Samples" || true

# ============================================================
# 5. Meadow OS build tools (delegate to upstream repo)
# ============================================================
section "Meadow OS Build Tools"

MEADOW_DIR="$PARENT_DIR/$MEADOW_REPO"

if [ -d "$MEADOW_DIR" ]; then
    # ARM GCC
    if command -v arm-none-eabi-gcc &> /dev/null; then
        ok "arm-none-eabi-gcc ($(arm-none-eabi-gcc --version 2>&1 | head -1))"
    elif [ -d "/Applications/ARM/bin" ]; then
        export PATH="/Applications/ARM/bin:$PATH"
        ok "arm-none-eabi-gcc (/Applications/ARM/bin)"
    else
        if [ "$PLATFORM" = "macos" ] && [ -f "$MEADOW_DIR/install-packages.sh" ]; then
            echo "  Running $MEADOW_REPO/install-packages.sh..."
            if bash "$MEADOW_DIR/install-packages.sh"; then
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
            if brew install make 2>/dev/null; then
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
                if brew install "$pkg" 2>/dev/null; then
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
    warn "Skipping build tools — $MEADOW_REPO repo not found"
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
    warn "Skipping — $MEADOW_REPO repo not found"
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
        if git -C "$RENODE_DIR" submodule update --init src/Infrastructure; then
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
        export CFLAGS="${CFLAGS:--Wno-error}"
        if (cd "$RENODE_DIR" && bash build.sh $BUILD_FLAGS); then
            added "Renode build"
        else
            fail "Renode build failed"
            warn "Try: cd ../renode && CFLAGS=\"-Wno-error\" bash build.sh $BUILD_FLAGS"
        fi
    fi
else
    echo "  Cloning WildernessLabs/renode..."
    if git clone https://github.com/WildernessLabs/renode.git "$RENODE_DIR"; then
        added "renode/ repo cloned"

        echo "  Checking out meadow-emulator-fixes..."
        git -C "$RENODE_DIR" checkout meadow-emulator-fixes

        echo "  Initializing submodules..."
        git -C "$RENODE_DIR" submodule update --init src/Infrastructure

        echo "  Building Renode (this may take several minutes)..."
        BUILD_FLAGS=""
        if [ "$ARCH" = "arm64" ]; then
            BUILD_FLAGS="--host-arch aarch64"
        fi
        export CFLAGS="${CFLAGS:--Wno-error}"
        if (cd "$RENODE_DIR" && bash build.sh $BUILD_FLAGS); then
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
# 8. Firmware build output
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
