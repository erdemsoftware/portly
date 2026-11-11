#!/usr/bin/env bash
# Shared helpers of the own-builds scripts. Source it: . "$(dirname "$0")/lib.sh"
set -euo pipefail

OB_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$OB_DIR/versions.env"

case "$(uname -s)" in
  Linux)  OS=linux ;;
  Darwin) OS=macos ;;
  *) echo "unsupported OS $(uname -s)" >&2; exit 1 ;;
esac
case "$(uname -m)" in
  x86_64|amd64)  ARCH=x64 ;;
  aarch64|arm64) ARCH=arm64 ;;
  *) echo "unsupported CPU $(uname -m)" >&2; exit 1 ;;
esac
JOBS="$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 2)"

if [ "$OS" = macos ]; then
  # oldest macOS the Portly app itself supports
  export MACOSX_DEPLOYMENT_TARGET="${MACOSX_DEPLOYMENT_TARGET:-12.0}"
fi

WORK="${OB_WORK:-$PWD/ob-work}"       # sources, build trees, static dependency prefix
OUT="${OB_OUT:-$PWD/ob-out}"          # finished tar.gz + SHA256SUMS
DEPS="$WORK/deps"
mkdir -p "$WORK/src" "$OUT" "$DEPS"

log() { printf '\n==== %s\n' "$*"; }

sha256_of() { if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }

# fetch <url> <sha256> -> downloads into $WORK/src (kept between runs) and checks the hash
fetch() {
  local url="$1" want="$2" file="$WORK/src/$(basename "$1")"
  if [ ! -f "$file" ] || [ "$(sha256_of "$file")" != "$want" ]; then
    curl -fsSL --retry 5 --retry-delay 3 -o "$file" "$url"
  fi
  local got; got="$(sha256_of "$file")"
  if [ "$got" != "$want" ]; then echo "SHA-256 mismatch for $url: $got (expected $want)" >&2; exit 1; fi
  echo "$file"
}

# unpack <archive> -> prints the extracted folder
unpack() {
  local archive="$1" dest="$WORK/build"
  mkdir -p "$dest"
  local top; top="$(tar -tf "$archive" | sed -n 1p | cut -d/ -f1)"
  rm -rf "$dest/$top"
  tar -xf "$archive" -C "$dest"
  echo "$dest/$top"
}

# package <name> <version> <staging folder whose content becomes the package>
# Result: $OUT/<name>-<version>-<os>-<arch>.tar.gz with the top folder <name>-<version>-<os>-<arch>
package() {
  local name="$1" version="$2" stage="$3"
  local base="$name-$version-$OS-$ARCH"
  local top="$WORK/pkg/$base"
  rm -rf "$top"; mkdir -p "$WORK/pkg"
  cp -R "$stage" "$top"
  (cd "$WORK/pkg" && tar -czf "$OUT/$base.tar.gz" "$base")
  echo "$OUT/$base.tar.gz"
}

# Fails when a binary needs a shared library that is not part of the operating system (relocatable check).
check_linkage() {
  local f
  for f in "$@"; do
    echo "--- linkage of $f"
    if [ "$OS" = linux ]; then
      ldd "$f" || true
      if ldd "$f" 2>/dev/null | grep -E '=>' | grep -vE '(linux-vdso|ld-linux|ld-musl|/lib(64)?/|/usr/lib|libc\.so|libm\.so|libdl\.so|libpthread\.so|librt\.so|libcrypt\.so|libresolv\.so|libutil\.so)' ; then
        echo "unexpected shared library in $f" >&2; [ -n "${LINKAGE_WARN:-}" ] || return 1
      fi
    else
      otool -L "$f"
      if otool -L "$f" | tail -n +2 | grep -vE '(/usr/lib/|/System/Library/)'; then
        echo "unexpected shared library in $f" >&2; [ -n "${LINKAGE_WARN:-}" ] || return 1
      fi
    fi
  done
}
