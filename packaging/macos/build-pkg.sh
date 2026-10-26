#!/usr/bin/env bash
# Builds AI Laptop Guardian.app and a signed, notarized .pkg on macOS. NOT YET RUN: see docs/PLATFORM_STATUS.md.
# Usage: packaging/macos/build-pkg.sh [arm64|x64]
# Signing/notarization (all optional; without them you get an unsigned build that Gatekeeper will block):
#   DEVELOPER_ID_APP="Developer ID Application: Name (TEAMID)"
#   DEVELOPER_ID_INSTALLER="Developer ID Installer: Name (TEAMID)"
#   NOTARY_PROFILE=<keychain profile created with: xcrun notarytool store-credentials>
set -euo pipefail
ARCH="${1:-arm64}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -1)"
OUT="$ROOT/artifacts/macos"
APP="$OUT/AI Laptop Guardian.app"
rm -rf "$APP" && mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

dotnet publish "$ROOT/src/Guardian.App/Guardian.App.csproj" -c Release -r "osx-$ARCH" --self-contained true \
  -p:DebugType=none -o "$APP/Contents/MacOS"
sed "s/__VERSION__/$VERSION/g" "$ROOT/packaging/macos/Info.plist" > "$APP/Contents/Info.plist"
cp "$ROOT/assets/brand/guardian.icns" "$APP/Contents/Resources/"

if [[ -n "${DEVELOPER_ID_APP:-}" ]]; then
  # Sign nested native libraries first, then the bundle, with the hardened runtime.
  find "$APP/Contents/MacOS" -type f \( -name "*.dylib" -o -perm -u+x \) -print0 | while IFS= read -r -d '' f; do
    codesign --force --options runtime --timestamp --entitlements "$ROOT/packaging/macos/entitlements.plist" --sign "$DEVELOPER_ID_APP" "$f"
  done
  codesign --force --options runtime --timestamp --entitlements "$ROOT/packaging/macos/entitlements.plist" --sign "$DEVELOPER_ID_APP" "$APP"
  codesign --verify --deep --strict "$APP"
fi

PKG="$OUT/AI-Laptop-Guardian-$VERSION-$ARCH.pkg"
COMPONENT="$OUT/component.pkg"
pkgbuild --component "$APP" --install-location /Applications --identifier app.ailaptopguardian.pkg --version "$VERSION" "$COMPONENT"
if [[ -n "${DEVELOPER_ID_INSTALLER:-}" ]]; then
  productbuild --package "$COMPONENT" --sign "$DEVELOPER_ID_INSTALLER" "$PKG"
else
  productbuild --package "$COMPONENT" "$PKG"
fi
rm -f "$COMPONENT"

if [[ -n "${NOTARY_PROFILE:-}" ]]; then
  xcrun notarytool submit "$PKG" --keychain-profile "$NOTARY_PROFILE" --wait
  xcrun stapler staple "$PKG"
fi
echo "Built $PKG"
