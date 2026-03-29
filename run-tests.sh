#!/bin/bash
# run-tests.sh — Build, deploy, and run Mono mini test suite in Renode
#
# Usage:
#   ./run-tests.sh              # Full: build firmware + LFS + run tests
#   ./run-tests.sh --lfs-only   # Skip firmware rebuild, just rebuild LFS + run
#   ./run-tests.sh --run-only   # Skip all builds, just launch Renode
#
# Output: Test results appear in /tmp/renode-uart-dotnet10.txt
# The script waits for "Test Runner Complete" or a timeout (10 min).

set -e
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
MEADOW_DIR="$(dirname "$SCRIPT_DIR")/Meadow"
RUNTIME_DIR="$(dirname "$SCRIPT_DIR")/runtime"
BUILD_DIR="$SCRIPT_DIR/build/dotnet10"
TEST_APP="$MEADOW_DIR/conductor/tracks/mono-upgrade-09-tests/test-runner-app"
RENODE="/Users/lexas/renode/renode"
UART_LOG="/tmp/renode-uart-dotnet10.txt"

# Parse args
SKIP_FIRMWARE=false
SKIP_LFS=false
for arg in "$@"; do
  case "$arg" in
    --lfs-only) SKIP_FIRMWARE=true ;;
    --run-only) SKIP_FIRMWARE=true; SKIP_LFS=true ;;
    --help|-h)
      echo "Usage: $0 [--lfs-only|--run-only]"
      echo "  (no args)   Full build: firmware + test runner + LFS + Renode"
      echo "  --lfs-only  Skip firmware rebuild, just rebuild LFS image and run"
      echo "  --run-only  Skip all builds, just launch Renode with existing image"
      exit 0
      ;;
  esac
done

# Kill any existing Renode
pkill -f renode 2>/dev/null && sleep 1 || true

# --- Step 1: Build test runner ---
if [ "$SKIP_LFS" = false ]; then
  echo "=== Building test runner ==="
  cd "$TEST_APP"
  dotnet build TestRunnerApp.csproj -c Release -v q 2>&1 | tail -3

  echo "=== Deploying test runner as Meadow.dll ==="
  cp "$TEST_APP/bin/Release/net9.0/App.dll" "$BUILD_DIR/assemblies/Meadow.dll"
fi

# --- Step 2: Build firmware ---
if [ "$SKIP_FIRMWARE" = false ]; then
  echo "=== Building firmware ==="
  cd "$SCRIPT_DIR"
  bash build-meadow.os-emulated.sh 2>&1 | tail -5

  # Verify mono_bss address and update zero bin + resc if needed
  echo "=== Checking .mono_bss address ==="
  BSS_START=$(arm-none-eabi-nm "$BUILD_DIR/nuttx_user.elf" | grep ' _s_mono_bss' | awk '{print $1}')
  BSS_END=$(arm-none-eabi-nm "$BUILD_DIR/nuttx_user.elf" | grep ' _e_mono_bss' | awk '{print $1}')
  BSS_SIZE=$(python3 -c "print(0x$BSS_END - 0x$BSS_START)")
  echo "  _s_mono_bss = 0x$BSS_START, _e_mono_bss = 0x$BSS_END, size = $BSS_SIZE"

  # Regenerate zero bin
  python3 -c "import sys; sys.stdout.buffer.write(b'\x00' * $BSS_SIZE)" > "$BUILD_DIR/mono_bss_zero.bin"

  # Update resc file if address changed
  RESC="$SCRIPT_DIR/scripts/meadow-dotnet10-headless.resc"
  CURRENT_ADDR=$(grep 'mono_bss_zero.bin' "$RESC" | grep -o '0x[0-9A-Fa-f]*' | head -1)
  NEW_ADDR="0x${BSS_START^^}"
  if [ "$CURRENT_ADDR" != "$NEW_ADDR" ]; then
    echo "  Updating resc: $CURRENT_ADDR -> $NEW_ADDR"
    sed -i '' "s|mono_bss_zero.bin $CURRENT_ADDR|mono_bss_zero.bin $NEW_ADDR|" "$RESC"
    # Also update the comment
    sed -i '' "s|\.mono_bss at 0x[0-9A-Fa-f]*|.mono_bss at $NEW_ADDR|" "$RESC"
    HEX_SIZE=$(python3 -c "print(f'0x{$BSS_SIZE:X}')")
    sed -i '' "s|size 0x[0-9A-Fa-f]* ([0-9,]* bytes)|size $HEX_SIZE ($BSS_SIZE bytes)|" "$RESC"
  else
    echo "  Address unchanged ($CURRENT_ADDR)"
  fi
fi

# --- Step 3: Build LFS image ---
if [ "$SKIP_LFS" = false ]; then
  echo "=== Building LFS image ==="
  "$SCRIPT_DIR/tools/build_lfs_v1_image" \
    "$BUILD_DIR/assemblies" \
    "$BUILD_DIR/littlefs.bin" \
    "$BUILD_DIR/assemblies" 2>&1 | tail -3
fi

# --- Step 4: Launch Renode ---
echo "=== Launching Renode ==="
: > "$UART_LOG"

# Pick a random port to avoid conflicts
PORT=$((9900 + RANDOM % 99))
cd "$SCRIPT_DIR"
"$RENODE" --port $PORT --disable-xwt \
  -e "include @scripts/meadow-dotnet10-headless.resc" \
  > /tmp/renode-test-session.log 2>&1 &
RENODE_PID=$!
echo "  Renode PID: $RENODE_PID, monitor port: $PORT"

# --- Step 5: Wait for test completion ---
echo "=== Waiting for tests (timeout: 10 min) ==="
TIMEOUT=600
ELAPSED=0
while [ $ELAPSED -lt $TIMEOUT ]; do
  if grep -q "Test Runner Complete" "$UART_LOG" 2>/dev/null; then
    break
  fi
  if ! kill -0 $RENODE_PID 2>/dev/null; then
    echo "ERROR: Renode exited unexpectedly"
    cat /tmp/renode-test-session.log | tail -20
    exit 1
  fi
  sleep 5
  ELAPSED=$((ELAPSED + 5))
  # Show progress every 30s
  if [ $((ELAPSED % 30)) -eq 0 ]; then
    LINES=$(wc -l < "$UART_LOG" 2>/dev/null || echo 0)
    LAST=$(tail -1 "$UART_LOG" 2>/dev/null || echo "(no output)")
    echo "  ${ELAPSED}s: $LINES lines — $LAST"
  fi
done

if [ $ELAPSED -ge $TIMEOUT ]; then
  echo "ERROR: Timeout after ${TIMEOUT}s"
  echo "Last 20 lines of UART output:"
  tail -20 "$UART_LOG"
  pkill -f renode 2>/dev/null
  exit 1
fi

# --- Step 6: Report results ---
echo ""
echo "=== TEST RESULTS ==="
grep -E "===|---" "$UART_LOG" | grep -v "DIAG"
echo ""
echo "=== FAILURES ==="
grep -E "failed:|EXCEPTION:" "$UART_LOG" || echo "(none)"
echo ""

# Extract totals
TOTAL_LINE=$(grep "=== TOTAL:" "$UART_LOG")
echo "$TOTAL_LINE"

# Cleanup
pkill -f renode 2>/dev/null || true
echo ""
echo "Full log: $UART_LOG"
