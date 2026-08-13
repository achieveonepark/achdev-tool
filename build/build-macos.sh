#!/usr/bin/env bash
# Builds a self-contained macOS publish of AchDev Tool, packages it with Velopack,
# then turns the resulting .app into a drag-to-Applications .dmg (Velopack's own
# macOS output is a portable .zip; see https://docs.velopack.io — DMG needs this
# extra unzip + hdiutil step).
#
# Usage:  ./build/build-macos.sh [arm64|x64]   (defaults to arm64 / Apple Silicon)
# Requires: macOS, .NET SDK, Xcode Command Line Tools, `vpk` (installed below if missing).

set -euo pipefail

ARCH="${1:-arm64}"
case "$ARCH" in
  arm64) RID="osx-arm64" ;;
  x64)   RID="osx-x64" ;;
  *) echo "Unknown arch '$ARCH' (expected arm64 or x64)"; exit 1 ;;
esac

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$REPO_ROOT/src/AchDevTool/AchDevTool.csproj"
APP_ID="AchDevTool"
PUBLISH_DIR="$REPO_ROOT/publish/$RID"
RELEASE_DIR="$REPO_ROOT/releases/$RID"
ICON="$REPO_ROOT/src/AchDevTool/Assets/app.icns"

VERSION=$(sed -nE 's/.*<Version>(.*)<\/Version>.*/\1/p' "$PROJECT" | head -1)
echo "Building AchDev Tool v$VERSION for $RID"

if ! command -v vpk >/dev/null 2>&1; then
  echo "Installing vpk (Velopack CLI)..."
  dotnet tool install -g vpk
  export PATH="$PATH:$HOME/.dotnet/tools"
fi

rm -rf "$PUBLISH_DIR"
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true -o "$PUBLISH_DIR"

vpk pack -u "$APP_ID" -v "$VERSION" -p "$PUBLISH_DIR" -e "AchDevTool" -i "$ICON" -o "$RELEASE_DIR"

# --- Portable .zip -> .app -> .dmg -------------------------------------------------
PORTABLE_ZIP=$(find "$RELEASE_DIR" -maxdepth 1 -name "*-Portable.zip" | head -1)
if [ -z "$PORTABLE_ZIP" ]; then
  echo "Could not find the Velopack portable .zip in $RELEASE_DIR" >&2
  exit 1
fi

STAGE_DIR=$(mktemp -d)
unzip -q "$PORTABLE_ZIP" -d "$STAGE_DIR"
APP_BUNDLE=$(find "$STAGE_DIR" -maxdepth 1 -name "*.app" | head -1)
if [ -z "$APP_BUNDLE" ]; then
  echo "Could not find the .app bundle inside $PORTABLE_ZIP" >&2
  exit 1
fi
ln -s /Applications "$STAGE_DIR/Applications"

DMG_PATH="$RELEASE_DIR/${APP_ID}-${VERSION}-${RID}.dmg"
rm -f "$DMG_PATH"
hdiutil create -volname "AchDev Tool" -srcfolder "$STAGE_DIR" -ov -format UDZO "$DMG_PATH"
rm -rf "$STAGE_DIR"

echo ""
echo "Done. Installer artifacts created in: $RELEASE_DIR"
echo " - $(basename "$DMG_PATH")  (drag-to-Applications installer)"
echo " - $(basename "$PORTABLE_ZIP")  (portable .app, also used for auto-updates)"
