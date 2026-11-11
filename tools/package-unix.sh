#!/usr/bin/env bash
# Builds the Portly 2 packages for macOS and Linux into dist/.
#   tools/package-unix.sh <rid> [version]
#   rid: osx-arm64 | osx-x64 (run on macOS)  |  linux-x64 | linux-arm64 (run on Linux)
# macOS: Portly.app (ad-hoc signed) in Portly-<v>-macos-<arch>.dmg and .zip
# Linux: Portly-<v>-linux-<arch>.tar.gz, .deb and .AppImage
# Also: a version-less copy of every file (Portly-macos-arm64.zip ...) and dist/SHA256SUMS (both names listed)
set -euo pipefail

RID="${1:?rid}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="${2:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/src/Portly.Desktop/Portly.Desktop.csproj" | head -1)}"
ARCH="${RID#*-}"
PUB="$ROOT/build/publish/$RID"
DIST="$ROOT/dist"
rm -rf "$PUB"
mkdir -p "$DIST"

dotnet publish "$ROOT/src/Portly.Desktop/Portly.Desktop.csproj" -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -p:Version="$VERSION" -o "$PUB" --nologo
find "$PUB" -name '*.pdb' -delete
chmod 755 "$PUB/Portly"

case "$RID" in
osx-*)
  APP="$ROOT/build/macos/Portly.app"
  rm -rf "$ROOT/build/macos"
  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
  cp -R "$PUB/." "$APP/Contents/MacOS/"
  ICONSET="$ROOT/build/macos/portly.iconset"
  mkdir -p "$ICONSET"
  for s in 16 32 128 256; do
    sips -z $s $s "$ROOT/assets/portly-256.png" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
    d=$((s * 2)); [ $d -le 256 ] && sips -z $d $d "$ROOT/assets/portly-256.png" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
  done
  iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/portly.icns"
  cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Portly</string>
  <key>CFBundleDisplayName</key><string>Portly</string>
  <key>CFBundleIdentifier</key><string>net.erdemsoftware.portly</string>
  <key>CFBundleExecutable</key><string>Portly</string>
  <key>CFBundleIconFile</key><string>portly</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>Copyright (c) 2026 Erdem Software. MIT License.</string>
</dict>
</plist>
EOF
  # free ad-hoc signature: required for Apple Silicon, not a Developer ID
  codesign --force --deep --sign - "$APP"
  codesign --verify --deep --strict "$APP"
  NAME="Portly-$VERSION-macos-$ARCH"
  STAGE="$ROOT/build/macos/dmg"
  mkdir -p "$STAGE"
  cp -R "$APP" "$STAGE/"
  ln -s /Applications "$STAGE/Applications"
  rm -f "$DIST/$NAME.dmg" "$DIST/$NAME.zip"
  hdiutil create -volname "Portly" -srcfolder "$STAGE" -ov -format UDZO "$DIST/$NAME.dmg" >/dev/null
  (cd "$ROOT/build/macos" && ditto -c -k --keepParent Portly.app "$DIST/$NAME.zip")
  ;;
linux-*)
  NAME="Portly-$VERSION-linux-$ARCH"
  STAGE="$ROOT/build/linux/$NAME"
  rm -rf "$ROOT/build/linux"
  mkdir -p "$STAGE"
  cp -R "$PUB/." "$STAGE/"
  cp "$ROOT/assets/portly-256.png" "$STAGE/portly.png"
  tar -C "$ROOT/build/linux" -czf "$DIST/$NAME.tar.gz" "$NAME"

  DESKTOP="[Desktop Entry]
Type=Application
Name=Portly
Comment=Local web development environment
Exec=portly
Icon=portly
Terminal=false
Categories=Development;WebDevelopment;
StartupWMClass=Portly"

  # .deb: /opt/portly + /usr/bin/portly
  DEBARCH=$([ "$ARCH" = arm64 ] && echo arm64 || echo amd64)
  DEB="$ROOT/build/linux/deb"
  mkdir -p "$DEB/DEBIAN" "$DEB/opt/portly" "$DEB/usr/bin" "$DEB/usr/share/applications" "$DEB/usr/share/icons/hicolor/256x256/apps"
  cp -R "$PUB/." "$DEB/opt/portly/"
  ln -s /opt/portly/Portly "$DEB/usr/bin/portly"
  echo "$DESKTOP" > "$DEB/usr/share/applications/portly.desktop"
  cp "$ROOT/assets/portly-256.png" "$DEB/usr/share/icons/hicolor/256x256/apps/portly.png"
  cat > "$DEB/DEBIAN/control" <<EOF
Package: portly
Version: $VERSION
Section: devel
Priority: optional
Architecture: $DEBARCH
Maintainer: Erdem Software <47487615+erdemsoftware@users.noreply.github.com>
Homepage: https://github.com/erdemsoftware/portly
Depends: libc6, libfontconfig1, libx11-6, libice6, libsm6
Description: Portly - local web development environment
 PHP, MySQL, Caddy, Node.js and more, in your home folder (~/Portly).
EOF
  dpkg-deb --build --root-owner-group "$DEB" "$DIST/$NAME.deb" >/dev/null

  # AppImage
  APPDIR="$ROOT/build/linux/Portly.AppDir"
  mkdir -p "$APPDIR/usr/bin"
  cp -R "$PUB/." "$APPDIR/usr/bin/"
  echo "$DESKTOP" > "$APPDIR/portly.desktop"
  cp "$ROOT/assets/portly-256.png" "$APPDIR/portly.png"
  cat > "$APPDIR/AppRun" <<'EOF'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/bin/Portly" "$@"
EOF
  chmod 755 "$APPDIR/AppRun"
  TOOL="$ROOT/build/linux/appimagetool"
  TOOLARCH=$([ "$ARCH" = arm64 ] && echo aarch64 || echo x86_64)
  curl -fsSL -o "$TOOL" "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-$TOOLARCH.AppImage"
  chmod 755 "$TOOL"
  ARCH=$TOOLARCH APPIMAGE_EXTRACT_AND_RUN=1 "$TOOL" --no-appstream "$APPDIR" "$DIST/$NAME.AppImage" >/dev/null
  ;;
*)
  echo "unknown rid $RID" >&2; exit 1 ;;
esac

# stable names (Portly-macos-arm64.zip ...) for https://github.com/erdemsoftware/portly/releases/latest/download/<name>
# and tools/install.sh, plus the checksums install.sh verifies
for f in "$DIST/Portly-$VERSION-"*; do
  [ -f "$f" ] || continue
  n="$(basename "$f")"
  cp -f "$f" "$DIST/Portly-${n#Portly-$VERSION-}"
done
(
  cd "$DIST"
  if command -v sha256sum >/dev/null 2>&1; then sha256sum Portly-*; else shasum -a 256 Portly-*; fi > SHA256SUMS
)

ls -la "$DIST"
