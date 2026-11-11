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
  local base="$name-$version-$OS-$ARCH" f
  local top="$WORK/pkg/$base"
  rm -rf "$top"; mkdir -p "$WORK/pkg"
  cp -R "$stage" "$top"
  if [ "$OS" = macos ]; then
    # Apple Silicon only runs signed code: a free ad-hoc signature on every Mach-O file (strip invalidates the linker's one)
    while IFS= read -r f; do
      if file "$f" | grep -q 'Mach-O'; then codesign --force --sign - "$f"; fi
    done < <(find "$top" -type f \( -perm -u+x -o -name '*.so' -o -name '*.dylib' \))
  fi
  (cd "$WORK/pkg" && tar -czf "$OUT/$base.tar.gz" "$base")
  echo "$OUT/$base.tar.gz"
}

# Linux: libcrypt / libuuid are not on every distribution (libcrypt.so.1 is missing on Fedora / RHEL without libxcrypt-compat), so they
# are linked statically: a folder with only the .a files in front of the system ones makes "-lcrypt" pick them.
static_sys_libs() {
  [ "$OS" = linux ] || return 0
  mkdir -p "$DEPS/staticlib"
  local lib f
  for lib in libcrypt.a libuuid.a; do
    f="$(gcc -print-file-name=$lib)"
    if [ -f "$f" ]; then cp "$f" "$DEPS/staticlib/"; else echo "note: $lib not found, that library stays dynamic"; fi
  done
}

# Fails when a binary needs a shared library that is not part of the operating system (relocatable check).
check_linkage() {
  local f bad
  for f in "$@"; do
    echo "--- linkage of $f"
    if [ "$OS" = linux ]; then
      ldd "$f" || true
      # glibc itself only; every path in front of =>, vdso and the loader are fine, anything else is not
      bad="$(ldd "$f" 2>/dev/null | awk '/=>/ { n=$1; sub(/\..*/, "", n); if ($3 !~ /^\/(usr\/)?lib(64)?\// || n !~ /^lib(c|m|dl|pthread|rt|resolv|util|gcc_s|stdc\+\+)$/) print $0 }')"
    else
      otool -L "$f"
      bad="$(otool -L "$f" | tail -n +2 | grep -vE '^[[:space:]]+(/usr/lib/|/System/Library/)' || true)"
    fi
    if [ -n "$bad" ]; then
      echo "unexpected shared library in $f:" >&2; echo "$bad" >&2
      [ -n "${LINKAGE_WARN:-}" ] || return 1
    fi
  done
}
