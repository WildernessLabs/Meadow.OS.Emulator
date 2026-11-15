#!/bin/bash
#
# make-lfs-image.sh — Build a LittleFS image of a Meadow app for run.sh --mount.
#
# Packs the files Meadow.CLI would deploy from an app's publish folder into an
# image of the file system partition (mounted at /meadow0), so the emulator can
# boot with the app already in place instead of deploying it over HCOM:
#
#   tools/make-lfs-image.sh [--config=FILE | --legacy | --rebase] <publish-dir> <output.bin>
#   ./run.sh [the same --config/--legacy/--rebase] --mount=<output.bin>
#
# Arguments:
#   publish-dir   The app's publish folder.  For a Meadow.CLI v3 app this is the
#                 folder `meadow app run` / `meadow app deploy` deploys from, for
#                 example <app>/bin/Release/net10.0/publish (run `meadow app run`
#                 or `meadow app build` once to create it).
#   output.bin    The image to write (61,865,984 bytes, the whole partition).
#
# Options:
#   --config=FILE The emulator config the image is for; its os: picks the format.
#                 Default: emulator.config.yaml, as for run.sh.
#   --legacy      Build for the Legacy OS (LittleFS v1), whatever the config says.
#   --rebase      Build for Meadow.OS (Rebase, LittleFS v2), whatever the config says.
#   --pdbs        Include .pdb files (Meadow.CLI's deploy leaves them out by default).
#   --xmldocs     Include XML documentation files (left out by default).
#   --list        Print the files that would go into the image, and stop.
#
# Which files go in: the same rule as Meadow.CLI's v3 deploy
# (AppManagerV3.DeployApplication in Meadow.CLI meadow-3.0):
#   - files directly in publish-dir, skipping the runtimes/ folder and .DS_Store;
#   - only .dll .pdb .json .xml .yaml .yml .config files;
#   - .pdb files only with --pdbs, and .xml files that sit beside a same-named
#     .dll (XML docs) only with --xmldocs.
# A self-contained publish also holds the linux-arm native host files
# (libcoreclr.so, createdump, ...), which Meadow does not use; this rule drops them.
# All files go to the root of the file system (/meadow0), which is where both
# OSes look for System.Private.CoreLib.dll and Meadow.dll.  The image builders
# write one flat folder, so files in other subfolders are refused.
#
# The image builder depends on the OS, because the two use different LittleFS
# on-disk versions and geometries:
#   Legacy     tools/build_lfs_v1_image.c, against the Legacy checkout's
#              LittleFS v1.7 (nuttx/fs/littlefs/) and tools/lfs_host_config.h,
#              which replaces the NuttX parts of lfs_util.h for a host build.
#   Meadow.OS  tools/build_lfs_v2_image.c, against the Meadow.OS checkout's
#              NuttX-patched LittleFS v2.5 (nuttx/fs/littlefs/littlefs/, unpacked
#              by the first NuttX build) and the host stubs in tools/lfs_v2_host/.
# Each is compiled on first use, and again whenever a source is newer than the
# binary, into build/tools/.  The OS checkout is found the same way as by the
# other scripts: MEADOW_EMULATOR_OS_DIR, or the sibling folders (../Meadow or
# ../Legacy for Legacy, ../Meadow.OS or ../Rebase for Meadow.OS).  If the app has
# no meadow.config.yaml the builder adds a default one (MonoControl: --interp);
# an app's own is kept.
#
# Requires: a C compiler (cc), python3.
#

set -euo pipefail

SCRIPT_DIR="$( cd "$(dirname "$0")/.." ; pwd -P )"

usage() {
    sed -n '3,27p' "$0" | sed 's/^# \{0,1\}//'
    exit "${1:-0}"
}

# ---------------------------------------------------------------------------
# Arguments
# ---------------------------------------------------------------------------
INCLUDE_PDBS=false
INCLUDE_XMLDOCS=false
LIST_ONLY=false
OS_OVERRIDE=""
CONFIG_ARG=""
POSITIONAL=()
for arg in "$@"; do
    case "$arg" in
        --config=*) CONFIG_ARG="${arg#--config=}" ;;
        --legacy)  OS_OVERRIDE=legacy ;;
        --rebase)  OS_OVERRIDE=meadow-os ;;
        --pdbs)    INCLUDE_PDBS=true ;;
        --xmldocs) INCLUDE_XMLDOCS=true ;;
        --list)    LIST_ONLY=true ;;
        -h|--help) usage 0 ;;
        -*)        echo "ERROR: unknown option $arg" >&2; usage 1 ;;
        *)         POSITIONAL+=("$arg") ;;
    esac
done

if $LIST_ONLY; then
    [ ${#POSITIONAL[@]} -eq 1 ] || usage 1
else
    [ ${#POSITIONAL[@]} -eq 2 ] || usage 1
fi
PUBLISH_DIR="${POSITIONAL[0]}"
OUTPUT="${POSITIONAL[1]:-}"

if [ ! -d "$PUBLISH_DIR" ]; then
    echo "ERROR: publish folder not found: $PUBLISH_DIR" >&2
    exit 1
fi
PUBLISH_DIR="$(cd "$PUBLISH_DIR" && pwd -P)"

# ---------------------------------------------------------------------------
# Select the files Meadow.CLI would deploy (see the header for the rule)
# ---------------------------------------------------------------------------
FILE_LIST="$(python3 - "$PUBLISH_DIR" "$INCLUDE_PDBS" "$INCLUDE_XMLDOCS" <<'PYEOF'
import os, sys
root, pdbs, xmldocs = sys.argv[1], sys.argv[2] == "true", sys.argv[3] == "true"
deployable = {".dll", ".pdb", ".json", ".xml", ".yaml", ".yml", ".config"}
selected, nested = [], []
for dirpath, dirnames, filenames in os.walk(root):
    rel_dir = os.path.relpath(dirpath, root)
    if rel_dir == "runtimes" or rel_dir.startswith("runtimes" + os.sep):
        continue                                  # Meadow.CLI skips runtimes/
    for name in sorted(filenames):
        if name == ".DS_Store":
            continue
        ext = os.path.splitext(name)[1].lower()
        if ext not in deployable:
            continue
        if ext == ".pdb" and not pdbs:
            continue
        if ext == ".xml" and not xmldocs and \
           os.path.exists(os.path.join(dirpath, os.path.splitext(name)[0] + ".dll")):
            continue                              # XML doc beside its assembly
        rel = os.path.normpath(os.path.join(rel_dir, name))
        (selected if rel_dir == "." else nested).append(rel)
if nested:
    sys.stderr.write("ERROR: these files are in subfolders, which the image builder cannot\n"
                     "       create yet (it writes one flat folder):\n")
    for rel in nested:
        sys.stderr.write(f"         {rel}\n")
    sys.exit(1)
if not selected:
    sys.exit(f"ERROR: no deployable files in {root}")
print("\n".join(selected))
PYEOF
)"

if $LIST_ONLY; then
    echo "$FILE_LIST"
    exit 0
fi

for required in System.Private.CoreLib.dll Meadow.dll; do
    if ! grep -qx "$required" <<< "$FILE_LIST"; then
        echo "WARNING: $required is not in the image; Mono will not start without it." >&2
    fi
done

# ---------------------------------------------------------------------------
# Pick the OS (as run.sh does) and its image builder
# ---------------------------------------------------------------------------
# read-config.sh and resolve-locations.sh test variables that may be unset,
# so relax -u around them.  A sourced script sees this script's arguments when
# given none, and read-config.sh takes any existing file among them as the
# config (the publish folder's output.bin, say), so clear them first; they
# have all been parsed above.
set +u
set --
if [ -n "$CONFIG_ARG" ]; then
    source "$SCRIPT_DIR/scripts/read-config.sh" "$CONFIG_ARG" > /dev/null
else
    source "$SCRIPT_DIR/scripts/read-config.sh" > /dev/null
fi
TARGET_OS="${OS_OVERRIDE:-$EMU_OS}"
if [ "$TARGET_OS" = "legacy" ]; then
    source "$SCRIPT_DIR/scripts/resolve-locations.sh" legacy
else
    source "$SCRIPT_DIR/scripts/resolve-locations.sh" rebase
fi
set -u
if [ -n "${EMU_RUNTIME:-}" ] && [ -z "$OS_OVERRIDE" ]; then
    echo "ERROR: run.sh --mount is not supported for build.runtime $EMU_RUNTIME." >&2
    exit 1
fi

TOOL_DIR="$SCRIPT_DIR/build/tools"
if [ "$TARGET_OS" = "legacy" ]; then
    OS_NAME="Legacy"; LFS_MAJOR=1; RUN_FLAG="--legacy"
    LFS_SRC="$MEADOW_EMULATOR_OS_DIR/nuttx/fs/littlefs"
    TOOL="$TOOL_DIR/build_lfs_v1_image"
    TOOL_SOURCES=("$SCRIPT_DIR/tools/build_lfs_v1_image.c" "$SCRIPT_DIR/tools/lfs_host_config.h"
                  "$LFS_SRC/lfs.c" "$LFS_SRC/lfs.h" "$LFS_SRC/lfs_util.c" "$LFS_SRC/lfs_util.h")
else
    OS_NAME="Meadow.OS"; LFS_MAJOR=2; RUN_FLAG="--rebase"
    LFS_SRC="$MEADOW_EMULATOR_OS_DIR/nuttx/fs/littlefs/littlefs"
    TOOL="$TOOL_DIR/build_lfs_v2_image"
    TOOL_SOURCES=("$SCRIPT_DIR/tools/build_lfs_v2_image.c"
                  "$SCRIPT_DIR/tools/lfs_v2_host/fs_heap.h" "$SCRIPT_DIR/tools/lfs_v2_host/nuttx/mm/mm.h"
                  "$LFS_SRC/lfs.c" "$LFS_SRC/lfs.h" "$LFS_SRC/lfs_util.c" "$LFS_SRC/lfs_util.h")
fi
# The run.sh options that select the same OS, for the hint at the end
if [ -n "$CONFIG_ARG" ]; then
    RUN_FLAG="--config=$CONFIG_ARG${OS_OVERRIDE:+ $RUN_FLAG}"
elif [ -z "$OS_OVERRIDE" ]; then
    RUN_FLAG=""                  # emulator.config.yaml already selects it
fi

for src in "${TOOL_SOURCES[@]}"; do
    if [ ! -f "$src" ]; then
        echo "ERROR: $src not found." >&2
        echo "       The LittleFS sources come from the $OS_NAME checkout ($MEADOW_EMULATOR_OS_DIR;" >&2
        echo "       set MEADOW_EMULATOR_OS_DIR to use another).  Meadow.OS unpacks them in its" >&2
        echo "       first NuttX build." >&2
        exit 1
    fi
done

# An image in the other on-disk version would be refused by run.sh --mount.
if ! grep -qE "#define LFS_VERSION 0x000${LFS_MAJOR}" "$LFS_SRC/lfs.h"; then
    echo "ERROR: $LFS_SRC is not LittleFS v$LFS_MAJOR ($OS_NAME uses v$LFS_MAJOR)." >&2
    exit 1
fi

rebuild=false
if [ ! -x "$TOOL" ]; then
    rebuild=true
else
    for src in "${TOOL_SOURCES[@]}"; do
        if [ "$src" -nt "$TOOL" ]; then rebuild=true; fi
    done
fi

if $rebuild; then
    echo "--- Building $TOOL (LittleFS from $LFS_SRC)"
    mkdir -p "$TOOL_DIR"
    if [ "$LFS_MAJOR" = 1 ]; then
        # Copied together so lfs_host_config.h is found beside lfs_util.h.
        # LFS_CONFIG swaps NuttX's lfs_util.h for the host one; NAME_MAX comes
        # from NuttX's limits.h on the target and is missing on macOS hosts.
        rm -rf "$TOOL_DIR/src-v1" && mkdir -p "$TOOL_DIR/src-v1"
        cp "${TOOL_SOURCES[@]}" "$TOOL_DIR/src-v1/"
        cc -O2 -DLFS_CONFIG=lfs_host_config.h -DNAME_MAX=255 -I"$TOOL_DIR/src-v1" \
            -o "$TOOL" "$TOOL_DIR/src-v1/build_lfs_v1_image.c" \
            "$TOOL_DIR/src-v1/lfs.c" "$TOOL_DIR/src-v1/lfs_util.c"
    else
        # The firmware's own lfs_util.h, unchanged; tools/lfs_v2_host/ stands in
        # for the two NuttX headers it includes.
        cc -O2 -DLFS_NO_DEBUG -DLFS_NO_WARN -DLFS_NO_ERROR \
            -I"$SCRIPT_DIR/tools/lfs_v2_host" -I"$LFS_SRC" \
            -o "$TOOL" "$SCRIPT_DIR/tools/build_lfs_v2_image.c" \
            "$LFS_SRC/lfs.c" "$LFS_SRC/lfs_util.c"
    fi
fi

# ---------------------------------------------------------------------------
# Stage the files and build the image
# ---------------------------------------------------------------------------
STAGE="$(mktemp -d "${TMPDIR:-/tmp}/make-lfs-image.XXXXXX")"
trap 'rm -rf "$STAGE"' EXIT
mkdir -p "$STAGE/app" "$STAGE/bcl"     # bcl/ (v1 builder) stays empty: no /mono/4.5 BCL on meadow-3.0

while IFS= read -r rel; do
    cp "$PUBLISH_DIR/$rel" "$STAGE/app/"
done <<< "$FILE_LIST"

echo "--- $(wc -l <<< "$FILE_LIST" | tr -d ' ') files from $PUBLISH_DIR ($(du -sh "$STAGE/app" | cut -f1))"
mkdir -p "$(dirname "$OUTPUT")"
if [ "$LFS_MAJOR" = 1 ]; then
    "$TOOL" "$STAGE/bcl" "$OUTPUT" "$STAGE/app" > "$STAGE/builder.log"
else
    "$TOOL" "$STAGE/app" "$OUTPUT" > "$STAGE/builder.log"
fi
grep -E 'meadow.config.yaml: kept|default, MonoControl|Wrote|Block|LittleFS v2' "$STAGE/builder.log"
echo "--- $OS_NAME image (LittleFS v$LFS_MAJOR): $OUTPUT"
echo "    Run:   ./run.sh ${RUN_FLAG:+$RUN_FLAG }--mount=$OUTPUT"
