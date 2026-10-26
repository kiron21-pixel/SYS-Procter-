#!/usr/bin/env bash
# Builds a .deb for Ubuntu/Debian. Usage: packaging/linux/build-deb.sh [x64|arm64]
# The package installs only under /opt/ai-laptop-guardian plus a launcher, menu entry and icon.
# It has no maintainer scripts: installing or removing it runs no code and changes no other software.
set -euo pipefail
ARCH="${1:-x64}"
case "$ARCH" in x64) DEB_ARCH=amd64 ;; arm64) DEB_ARCH=arm64 ;; *) echo "unknown arch $ARCH"; exit 1 ;; esac
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
VERSION="$(grep -oPm1 '(?<=<Version>)[^<]+' "$ROOT/Directory.Build.props")"
OUT="$ROOT/artifacts/linux"
STAGE="$OUT/deb-$ARCH"
rm -rf "$STAGE" && mkdir -p "$STAGE/DEBIAN" "$STAGE/opt/ai-laptop-guardian" "$STAGE/usr/bin" \
  "$STAGE/usr/share/applications" "$STAGE/usr/share/icons/hicolor/256x256/apps" "$STAGE/usr/share/doc/ai-laptop-guardian"

dotnet publish "$ROOT/src/Guardian.App/Guardian.App.csproj" -c Release -r "linux-$ARCH" --self-contained true \
  -p:DebugType=none -o "$STAGE/opt/ai-laptop-guardian"

ln -s /opt/ai-laptop-guardian/ai-laptop-guardian "$STAGE/usr/bin/ai-laptop-guardian"
install -m 644 "$ROOT/packaging/linux/ai-laptop-guardian.desktop" "$STAGE/usr/share/applications/"
install -m 644 "$ROOT/assets/brand/logo-256.png" "$STAGE/usr/share/icons/hicolor/256x256/apps/ai-laptop-guardian.png"
install -m 644 "$ROOT/LICENSE" "$STAGE/usr/share/doc/ai-laptop-guardian/copyright"

cat > "$STAGE/DEBIAN/control" <<CONTROL
Package: ai-laptop-guardian
Version: $VERSION
Section: admin
Priority: optional
Architecture: $DEB_ARCH
Maintainer: AI Laptop Guardian maintainers
Depends: libc6, libgcc-s1, libstdc++6, zlib1g, libicu74 | libicu72 | libicu70, libfontconfig1, libx11-6, libice6, libsm6
Description: Security and maintenance status for your computer
 AI Laptop Guardian reports security status, performance and cleanup candidates.
 It starts in Observe Only mode and changes nothing without explicit consent.
 Preview build: not yet release-verified on every distribution.
CONTROL

dpkg-deb --root-owner-group --build "$STAGE" "$OUT/ai-laptop-guardian_${VERSION}_${DEB_ARCH}.deb"
echo "Built $OUT/ai-laptop-guardian_${VERSION}_${DEB_ARCH}.deb"
