#!/usr/bin/env bash
# php-fpm + php-cli with static-php-cli (spc) and Xdebug as a loadable extension.
#   build-php.sh <php line, e.g. 8.5> [quick|full]
# Everything but the C library is linked statically (glibc on Linux, the system libraries on macOS): that is what lets
# php load xdebug.so, which a fully static (musl) build cannot do.
# Layout (the same file names as the static-php packages Portly uses today):
#   php-<v>-<os>-<arch>/php-fpm, php, ext/xdebug.so, php.ini-development, php.ini-production
. "$(dirname "$0")/lib.sh"

LINE="${1:?php line, for example 8.5}"
SET="${2:-full}"
if [ "$SET" = quick ]; then EXTS="$PHP_EXTENSIONS_QUICK"; else EXTS="$PHP_EXTENSIONS_FULL"; fi

log "PHP $LINE ($SET) with Xdebug on $OS-$ARCH"
SPC_OS="$OS"
case "$ARCH" in x64) SPC_CPU=x86_64 ;; arm64) SPC_CPU=aarch64 ;; esac
hash_var="SPC_SHA256_${OS}_${ARCH}"
SPC_ARCHIVE="$(fetch "https://github.com/crazywhalecc/static-php-cli/releases/download/$SPC_VERSION/spc-$SPC_OS-$SPC_CPU.tar.gz" "${!hash_var}")"

B="$WORK/spc-$LINE"
rm -rf "$B"; mkdir -p "$B"
tar -xzf "$SPC_ARCHIVE" -C "$B"
chmod +x "$B/spc"
cd "$B"

if [ "$OS" = linux ]; then
  # Linux: glibc (not musl), so that the php binary can load extensions. SPC_TARGET (zig toolchain, for example
  # native-native-gnu.2.17) can be set from outside to link against an older glibc.
  if [ -z "${SPC_TARGET:-}" ]; then export SPC_LIBC=glibc; fi
fi

./spc doctor --auto-fix
./spc download --with-php="$LINE" --for-extensions="$EXTS,xdebug" --prefer-pre-built
./spc build "$EXTS" --build-cli --build-fpm --build-shared=xdebug --debug

ROOT="$B/buildroot"
ls -la "$ROOT/bin" "$ROOT/modules"
VERSION="$("$ROOT/bin/php" -r 'echo PHP_VERSION;')"
echo "built PHP $VERSION"

STAGE="$WORK/stage/php-$LINE"
rm -rf "$STAGE"; mkdir -p "$STAGE/ext"
cp "$ROOT/bin/php" "$ROOT/bin/php-fpm" "$STAGE/"
cp "$ROOT/modules/xdebug.so" "$STAGE/ext/"
SRC_PHP="$(ls -d "$B"/source/php-src 2>/dev/null | head -1)"
for f in php.ini-development php.ini-production LICENSE; do [ -f "$SRC_PHP/$f" ] && cp "$SRC_PHP/$f" "$STAGE/" || true; done
chmod 755 "$STAGE/php" "$STAGE/php-fpm"
if [ "$OS" = macos ]; then strip -x "$STAGE/ext/xdebug.so" 2>/dev/null || true; else strip --strip-unneeded "$STAGE/ext/xdebug.so" 2>/dev/null || true; fi

"$STAGE/php" -v
"$STAGE/php" -d zend_extension="$STAGE/ext/xdebug.so" -v
LINKAGE_WARN=1 check_linkage "$STAGE/php" "$STAGE/php-fpm" "$STAGE/ext/xdebug.so"
if [ "$OS" = linux ]; then echo "highest glibc symbol needed:"; objdump -T "$STAGE/php" "$STAGE/ext/xdebug.so" | grep -o 'GLIBC_[0-9.]*' | sort -uV | tail -1; fi
package php "$VERSION" "$STAGE"
