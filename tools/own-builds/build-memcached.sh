#!/usr/bin/env bash
# memcached with libevent linked statically. Layout: memcached-<v>-<os>-<arch>/memcached
. "$(dirname "$0")/lib.sh"

log "memcached $MEMCACHED_VERSION on $OS-$ARCH"
MC_SRC="$(fetch "https://memcached.org/files/memcached-$MEMCACHED_VERSION.tar.gz" "$MEMCACHED_SHA256")"
LE_SRC="$(fetch "https://github.com/libevent/libevent/releases/download/release-$LIBEVENT_VERSION-stable/libevent-$LIBEVENT_VERSION-stable.tar.gz" "$LIBEVENT_SHA256")"

LE_DIR="$(unpack "$LE_SRC")"
( cd "$LE_DIR" && ./configure --prefix="$DEPS" --disable-shared --enable-static --with-pic --disable-openssl --disable-samples --disable-libevent-regress \
    && make -j"$JOBS" && make install )

SRC="$(unpack "$MC_SRC")"
STAGE="$WORK/stage/memcached"
rm -rf "$STAGE"; mkdir -p "$STAGE"
cd "$SRC"
# only the static libevent is in $DEPS, so the linker cannot pick a system copy
PKG_CONFIG_PATH="$DEPS/lib/pkgconfig" ./configure --prefix="$WORK/stage/mc-install" --with-libevent="$DEPS" --disable-docs --disable-dependency-tracking
make -j"$JOBS"
cp memcached "$STAGE/memcached"
cp COPYING "$STAGE/LICENSE" 2>/dev/null || cp LICENSE "$STAGE/LICENSE" 2>/dev/null || true
strip "$STAGE/memcached" 2>/dev/null || true
"$STAGE/memcached" -V
check_linkage "$STAGE/memcached"
package memcached "$MEMCACHED_VERSION" "$STAGE"
