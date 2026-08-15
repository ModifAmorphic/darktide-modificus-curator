#!/bin/sh
# Modificus Curator local Linux AppImage builder. Mirrors the release
# workflow's build-linux AppImage recipe (self-contained Velopack-enabled UI
# publish, native-AOT nxm handler, Relay staged app-local, vpk 1.2.0 pack on
# channel linux-x64) so a testable AppImage can be built from any branch
# without cutting a release.
#
# Output layout under PUBLISH_DIR (default: <repo>/publish, gitignored):
#   appimage/    the vpk pack directory (wiped at the start of every run, so
#                stale files never leak into a pack)
#   releases/    the vpk output: the AppImage, full nupkg, and feed (wiped)
#   downloads/   the Relay zip when fetched via gh (wiped)
#   .vpk-tool/   the pinned vpk 1.2.0 tool cache (installed once, reused)
#
# The default pack version is <manifest>-local.<YYYYmmddHHMM>, where
# <manifest> comes from .release-please-manifest.json. The -local. prerelease
# suffix sorts below the released version by design: a locally installed test
# build never masks a real release, and the app's self-update then offers to
# move to the latest release. Local builds pack into a clean output directory
# with no prior feed, so no delta packages are generated; deltas are a
# release-workflow concern.
#
# Environment overrides (env vars, not needed for normal use):
#   VERSION=<version>   full pack version (default: <manifest>-local.<stamp>)
#   RELAY_ZIP=<file>    use a local Relay *-windows-x64.zip instead of
#                       downloading it via gh (offline or repeatable builds)
#   PUBLISH_DIR=<dir>   output root (default: <repo>/publish)
#
# Steam Deck testing: run the AppImage directly, or install it into an
# isolated tree with install.sh's testing overrides (CURATOR_APPIMAGE,
# INSTALL_ROOT, BIN_LINK). The Velopack self-update state
# (/var/tmp/velopack/ModifAmorphic.ModificusCurator) is shared by every
# install of the same pack id; scripts/uninstall.sh's VELOPACK_STATE_DIR
# override can clean a test state tree.
set -eu

msg() { printf '%s\n' "$*"; }
die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

# Resolve the repo root from the script location so any CWD works.
ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)

# Prerequisites, each with an actionable message. gh is needed only when a
# Relay zip is not supplied locally.
command -v dotnet >/dev/null 2>&1 \
    || die "dotnet not found. Install the .NET 10 SDK and re-run."
command -v unzip >/dev/null 2>&1 \
    || die "unzip not found. Install it (e.g. 'sudo apt-get install unzip') and re-run."
command -v clang >/dev/null 2>&1 \
    || die "clang not found. It is required to native-AOT-compile the nxm handler (e.g. 'sudo apt-get install clang')."
command -v mksquashfs >/dev/null 2>&1 \
    || die "mksquashfs not found. vpk requires it to build the AppImage squashfs (squashfs-tools; e.g. 'sudo apt-get install squashfs-tools' or 'sudo pacman -S squashfs-tools')."
if [ -z "${RELAY_ZIP:-}" ]; then
    command -v gh >/dev/null 2>&1 \
        || die "gh not found. It is required to download the Relay runtime. Install the GitHub CLI, or set RELAY_ZIP to a local Relay *-windows-x64.zip."
fi

# Validate a local Relay override before anything is touched, so a missing
# override fails with zero side effects.
if [ -n "${RELAY_ZIP:-}" ]; then
    [ -f "$RELAY_ZIP" ] || die "RELAY_ZIP does not exist: $RELAY_ZIP"
fi

# Pack version: the release-please manifest plus a -local prerelease stamp
# that sorts below the released version.
manifest_version=$(sed -n 's/^[[:space:]]*"\."[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' \
    "$ROOT/.release-please-manifest.json") \
    || die "Could not read $ROOT/.release-please-manifest.json."
[ -n "$manifest_version" ] \
    || die "No version found in $ROOT/.release-please-manifest.json."
VERSION="${VERSION:-$manifest_version-local.$(date +%Y%m%d%H%M)}"

PUBLISH_DIR="${PUBLISH_DIR:-$ROOT/publish}"
case "$PUBLISH_DIR" in
    ''|'/') die "Refusing to use unsafe PUBLISH_DIR: '$PUBLISH_DIR'." ;;
esac

# Refuse a local Relay override that lives inside the output root: the
# per-run wipe would delete it before use. Resolved against the existing
# output root so relative overrides are caught too.
mkdir -p "$PUBLISH_DIR" || die "Could not create the output root: $PUBLISH_DIR"
if [ -n "${RELAY_ZIP:-}" ]; then
    abs_publish=$(CDPATH= cd -- "$PUBLISH_DIR" && pwd)
    abs_relay=$(CDPATH= cd -- "$(dirname -- "$RELAY_ZIP")" && pwd)/$(basename -- "$RELAY_ZIP")
    case "$abs_relay" in
        "$abs_publish"/*) die "RELAY_ZIP lives inside PUBLISH_DIR and would be wiped by the run. Move it outside the output root and re-run." ;;
    esac
fi

pack_dir="$PUBLISH_DIR/appimage"
out_dir="$PUBLISH_DIR/releases"
download_dir="$PUBLISH_DIR/downloads"
vpk_tool_dir="$PUBLISH_DIR/.vpk-tool"

# Wipe the per-run trees; the pinned vpk tool cache survives across runs.
rm -rf "$pack_dir" "$out_dir" "$download_dir"
mkdir -p "$pack_dir" "$out_dir" "$download_dir"

msg "Building Modificus Curator $VERSION (linux-x64 AppImage)"

msg "Publishing the Curator UI (self-contained, Velopack-enabled) ..."
(cd "$ROOT/src" && dotnet publish ui --configuration Release --runtime linux-x64 --self-contained true \
    --output "$pack_dir" \
    -p:Version="$VERSION" -p:InformationalVersion="$VERSION" -p:CuratorUseVelopack=true) \
    || die "Curator UI publish failed."

msg "Publishing the nxm handler (native AOT) ..."
(cd "$ROOT/src" && dotnet publish nxm-handler --configuration Release --runtime linux-x64 \
    --output "$pack_dir") \
    || die "nxm handler publish failed."

if [ -n "${RELAY_ZIP:-}" ]; then
    relay_zip="$RELAY_ZIP"
    msg "Using local Relay archive: $relay_zip"
else
    msg "Resolving the newest non-draft stable Relay release ..."
    relay_tag=$(gh release list --repo ModifAmorphic/darktide-mod-relay \
        --limit 100 --exclude-drafts --order desc \
        --json tagName,isPrerelease,publishedAt \
        --jq '[.[] | select(.isPrerelease == false)] | .[0].tagName // empty') \
        || die "Could not list Relay releases of ModifAmorphic/darktide-mod-relay."
    [ -n "$relay_tag" ] \
        || die "No non-draft stable Relay release was found in ModifAmorphic/darktide-mod-relay."
    msg "Relay tag: $relay_tag"
    gh release download "$relay_tag" --repo ModifAmorphic/darktide-mod-relay \
        --pattern "*-windows-x64.zip" --dir "$download_dir" \
        || die "Relay download failed for tag $relay_tag."
    set -- "$download_dir"/*-windows-x64.zip
    if [ $# -ne 1 ] || [ ! -f "$1" ]; then
        die "Expected exactly one *-windows-x64.zip in $download_dir after the Relay download."
    fi
    relay_zip=$1
fi

msg "Staging Relay into the pack directory ..."
mkdir -p "$pack_dir/relay"
unzip -q "$relay_zip" -d "$pack_dir/relay" || die "Relay extraction failed."

# Pinned vpk (Velopack CLI), installed once into the output root and reused
# across runs. A cache without the tool shim is discarded and reinstalled.
if [ ! -x "$vpk_tool_dir/vpk" ]; then
    rm -rf "$vpk_tool_dir"
    mkdir -p "$vpk_tool_dir"
    msg "Installing the pinned vpk 1.2.0 tool (cached in $vpk_tool_dir) ..."
    dotnet tool install vpk --version 1.2.0 --tool-path "$vpk_tool_dir" \
        || die "vpk tool install failed."
fi

msg "Packing the AppImage (vpk 1.2.0, channel linux-x64) ..."
(cd "$ROOT/src" && "$vpk_tool_dir/vpk" pack \
    --packId ModifAmorphic.ModificusCurator \
    --packVersion "$VERSION" \
    --packDir "$pack_dir" \
    --mainExe Modificus.Curator \
    --icon ui/Assets/app-icon.png \
    --packTitle "Modificus Curator" \
    --categories "Game;Utility" \
    --channel linux-x64 \
    --runtime linux-x64 \
    --compression gzip \
    -o "$out_dir") \
    || die "vpk pack failed."

long_appimage="$out_dir/ModifAmorphic.ModificusCurator-linux-x64.AppImage"
appimage="$out_dir/ModificusCurator-linux-x64.AppImage"
mv "$long_appimage" "$appimage" \
    || die "vpk did not produce the expected AppImage: $long_appimage"
[ ! -f "$long_appimage" ] || die "Long AppImage still exists after rename."
[ -f "$appimage" ] || die "Short AppImage does not exist after rename."
[ -x "$appimage" ] || die "Short AppImage is not executable after rename."

msg ""
msg "Built Modificus Curator $VERSION"
msg "  AppImage: $appimage"
msg ""
msg "Use it:"
msg "  - Direct run: chmod +x \"$appimage\" && \"$appimage\""
msg "  - Test install (isolated tree, install.sh testing overrides):"
msg "      CURATOR_APPIMAGE=\"$appimage\" INSTALL_ROOT=<dir> BIN_LINK=<dir> \\"
msg "        sh \"$ROOT/scripts/install.sh\""
msg "  - The Velopack self-update state (/var/tmp/velopack/ModifAmorphic.ModificusCurator)"
msg "    is shared with a real install; scripts/uninstall.sh's VELOPACK_STATE_DIR"
msg "    override can clean a test state tree."
