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

# --- Code signing / notarization ---------------------------------------------------
# 서명하지 않은 앱을 인터넷에서 받으면 Gatekeeper가 격리(quarantine) 속성 때문에
# "손상되었기 때문에 열 수 없습니다"라고 띄웁니다. 이걸 없애는 유일한 방법은
# Developer ID 서명 + notarization 이고, 그러려면 Apple Developer 계정이 필요합니다.
# 아래 환경 변수를 채우면 vpk 가 서명/공증까지 처리합니다.
#
#   MAC_SIGN_IDENTITY     예) "Developer ID Application: Your Name (TEAMID)"
#   MAC_INSTALL_IDENTITY  예) "Developer ID Installer: Your Name (TEAMID)"   (.pkg 서명용)
#   MAC_NOTARY_PROFILE    `xcrun notarytool store-credentials` 로 만든 프로파일 이름
VPK_ARGS=()
if [ -n "${MAC_SIGN_IDENTITY:-}" ]; then
  VPK_ARGS+=(--signAppIdentity "$MAC_SIGN_IDENTITY")
fi
if [ -n "${MAC_INSTALL_IDENTITY:-}" ]; then
  VPK_ARGS+=(--signInstallIdentity "$MAC_INSTALL_IDENTITY")
fi
if [ -n "${MAC_NOTARY_PROFILE:-}" ]; then
  VPK_ARGS+=(--notaryProfile "$MAC_NOTARY_PROFILE")
fi
if [ ${#VPK_ARGS[@]} -eq 0 ]; then
  echo "경고: 서명 정보가 없어 서명되지 않은 앱을 만듭니다."
  echo "      다운로드해서 설치하면 macOS가 '손상되었습니다'라고 막습니다. 받는 쪽에서 아래를 실행해야 합니다:"
  echo "      xattr -dr com.apple.quarantine /Applications/AchDevTool.app"
fi

# bash 3.2(맥 기본)에서는 set -u 상태로 빈 배열을 펼치면 에러가 나므로 이 형태로 씁니다.
vpk pack -u "$APP_ID" -v "$VERSION" -p "$PUBLISH_DIR" -e "AchDevTool" -i "$ICON" -o "$RELEASE_DIR" \
  ${VPK_ARGS[@]+"${VPK_ARGS[@]}"}

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
if [ -n "${MAC_SIGN_IDENTITY:-}" ]; then
  # vpk 가 이미 서명했으니, 압축을 풀면서 서명이 깨지지 않았는지만 확인합니다.
  codesign --verify --deep --strict "$APP_BUNDLE"
else
  # 서명 정보가 없으면 최소한 ad-hoc 서명이라도 해 둡니다. 번들 자체는 유효해지지만
  # Gatekeeper 차단은 그대로이므로, 받는 쪽에서 격리 속성을 지워야 합니다.
  codesign --force --deep --sign - "$APP_BUNDLE"
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
