#!/bin/sh
# Portly installer for macOS and Linux (no sudo needed).
#
#   curl -fsSL https://raw.githubusercontent.com/erdemsoftware/portly/main/tools/install.sh | sh
#
# macOS: downloads Portly-macos-<arch>.zip, checks its SHA-256 against the release's SHA256SUMS,
#        installs /Applications/Portly.app (or ~/Applications when /Applications is not writable)
#        and removes the quarantine flag so the app opens.
# Linux: downloads Portly-linux-<arch>.tar.gz, checks it, installs ~/.local/share/portly,
#        a ~/.local/bin/portly link, a menu entry and the icon.
#
# Environment (all optional):
#   PORTLY_VERSION=2.0.1      install this version instead of the latest release
#   PORTLY_INSTALL_FROM=DIR   take the files from a local folder (dist/) instead of GitHub; used by the CI test
#   PORTLY_APPS_DIR=DIR       macOS: where Portly.app goes (default /Applications)
#   PORTLY_PREFIX=DIR         Linux: install prefix (default ~/.local)
set -eu

REPO="erdemsoftware/portly"
VERSION="${PORTLY_VERSION:-}"
FROM="${PORTLY_INSTALL_FROM:-}"

say() { printf '%s\n' "$*"; }
die() { printf 'Portly installer: %s\n' "$*" >&2; exit 1; }

case "$(uname -s)" in
  Darwin) OS=macos ;;
  Linux) OS=linux ;;
  *) die "this installer is for macOS and Linux; on Windows use the setup or zip from https://github.com/$REPO/releases" ;;
esac
case "$(uname -m)" in
  arm64|aarch64) ARCH=arm64 ;;
  x86_64|amd64) ARCH=x64 ;;
  *) die "unsupported processor: $(uname -m)" ;;
esac

if [ "$OS" = macos ]; then EXT=zip; else EXT=tar.gz; fi
if [ -n "$VERSION" ]; then
  ASSET="Portly-$VERSION-$OS-$ARCH.$EXT"
  BASE="https://github.com/$REPO/releases/download/v$VERSION"
else
  ASSET="Portly-$OS-$ARCH.$EXT"
  BASE="https://github.com/$REPO/releases/latest/download"
fi

TMP="$(mktemp -d "${TMPDIR:-/tmp}/portly-install.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT INT TERM

get() {
  if [ -n "$FROM" ]; then
    [ -f "$FROM/$1" ] || die "$FROM/$1 not found"
    cp "$FROM/$1" "$2"
  elif command -v curl >/dev/null 2>&1; then
    curl -fsSL --retry 3 -o "$2" "$BASE/$1" || die "download failed: $BASE/$1"
  elif command -v wget >/dev/null 2>&1; then
    wget -q -O "$2" "$BASE/$1" || die "download failed: $BASE/$1"
  else
    die "curl or wget is needed"
  fi
}

sha256_of() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | awk '{print $1}'
  elif command -v shasum >/dev/null 2>&1; then shasum -a 256 "$1" | awk '{print $1}'
  else openssl dgst -sha256 "$1" | awk '{print $NF}'
  fi
}

say "Downloading $ASSET ..."
get SHA256SUMS "$TMP/SHA256SUMS"
get "$ASSET" "$TMP/$ASSET"

WANT="$(awk -v n="$ASSET" '($2 == n || $2 == "*" n) { print tolower($1); exit }' "$TMP/SHA256SUMS")"
[ -n "$WANT" ] || die "$ASSET is not listed in SHA256SUMS"
GOT="$(sha256_of "$TMP/$ASSET")"
[ "$WANT" = "$GOT" ] || die "checksum mismatch for $ASSET (expected $WANT, got $GOT); nothing was installed"
say "Checksum OK."

if [ "$OS" = macos ]; then
  DEST_DIR="${PORTLY_APPS_DIR:-/Applications}"
  if [ ! -d "$DEST_DIR" ] || [ ! -w "$DEST_DIR" ]; then
    [ -z "${PORTLY_APPS_DIR:-}" ] || die "$DEST_DIR is not writable"
    DEST_DIR="$HOME/Applications"
    mkdir -p "$DEST_DIR"
  fi
  mkdir "$TMP/x"
  ditto -x -k "$TMP/$ASSET" "$TMP/x"
  [ -d "$TMP/x/Portly.app" ] || die "the download does not contain Portly.app"
  codesign --verify --deep --strict "$TMP/x/Portly.app" || die "the code signature of Portly.app is not valid; nothing was installed"
  # the copy lands next to the final place first, so the swap is a rename
  rm -rf "$DEST_DIR/.Portly.app.install-new" "$DEST_DIR/.Portly.app.install-old"
  ditto "$TMP/x/Portly.app" "$DEST_DIR/.Portly.app.install-new"
  if [ -e "$DEST_DIR/Portly.app" ]; then mv "$DEST_DIR/Portly.app" "$DEST_DIR/.Portly.app.install-old"; fi
  mv "$DEST_DIR/.Portly.app.install-new" "$DEST_DIR/Portly.app"
  rm -rf "$DEST_DIR/.Portly.app.install-old"
  # the app is ad-hoc signed, not notarized: without this macOS refuses to open the downloaded app
  xattr -dr com.apple.quarantine "$DEST_DIR/Portly.app" 2>/dev/null || true
  say "Installed $DEST_DIR/Portly.app"
  say "Start it from Launchpad / Spotlight, or: open \"$DEST_DIR/Portly.app\""
else
  PREFIX="${PORTLY_PREFIX:-$HOME/.local}"
  SHARE="$PREFIX/share/portly"
  mkdir -p "$PREFIX/share" "$PREFIX/bin" "$PREFIX/share/applications"
  rm -rf "$SHARE.install-new" "$SHARE.install-old"
  mkdir "$SHARE.install-new"
  tar -xzf "$TMP/$ASSET" -C "$SHARE.install-new" --strip-components=1
  [ -x "$SHARE.install-new/Portly" ] || { rm -rf "$SHARE.install-new"; die "the download does not contain the Portly program"; }
  if [ -e "$SHARE" ]; then mv "$SHARE" "$SHARE.install-old"; fi
  mv "$SHARE.install-new" "$SHARE"
  rm -rf "$SHARE.install-old"
  ln -sf "$SHARE/Portly" "$PREFIX/bin/portly"
  ICON="$SHARE/portly.png"
  if [ -f "$ICON" ]; then
    mkdir -p "$PREFIX/share/icons/hicolor/256x256/apps"
    cp "$ICON" "$PREFIX/share/icons/hicolor/256x256/apps/portly.png"
  fi
  cat > "$PREFIX/share/applications/portly.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Portly
Comment=Local web development environment
Exec="$SHARE/Portly"
Icon=$ICON
Terminal=false
Categories=Development;WebDevelopment;
StartupWMClass=Portly
EOF
  command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$PREFIX/share/applications" >/dev/null 2>&1 || true
  say "Installed $SHARE"
  say "Start it from the application menu, or run: $PREFIX/bin/portly"
  case ":$PATH:" in
    *":$PREFIX/bin:"*) ;;
    *) say "Note: $PREFIX/bin is not in your PATH; add it to run 'portly' from a terminal." ;;
  esac
fi
