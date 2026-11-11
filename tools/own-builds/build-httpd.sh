#!/usr/bin/env bash
# Apache httpd with APR / APR-util built in, OpenSSL (mod_ssl), PCRE2, expat and nghttp2 (mod_http2) linked statically.
# Layout (like the Windows package):
#   httpd-<v>-<os>-<arch>/bin/httpd   (+ ab, htpasswd ...)
#   httpd-<v>-<os>-<arch>/modules/    mod_*.so, loaded with "LoadModule x modules/mod_x.so" relative to ServerRoot
#   httpd-<v>-<os>-<arch>/conf/       mime.types (Portly writes its own httpd.conf)
. "$(dirname "$0")/lib.sh"

log "httpd $HTTPD_VERSION on $OS-$ARCH"
HTTPD_SRC="$(fetch "https://downloads.apache.org/httpd/httpd-$HTTPD_VERSION.tar.bz2" "$HTTPD_SHA256" 2>/dev/null || fetch "https://archive.apache.org/dist/httpd/httpd-$HTTPD_VERSION.tar.bz2" "$HTTPD_SHA256")"
APR_SRC="$(fetch "https://downloads.apache.org/apr/apr-$APR_VERSION.tar.bz2" "$APR_SHA256" 2>/dev/null || fetch "https://archive.apache.org/dist/apr/apr-$APR_VERSION.tar.bz2" "$APR_SHA256")"
APU_SRC="$(fetch "https://downloads.apache.org/apr/apr-util-$APR_UTIL_VERSION.tar.bz2" "$APR_UTIL_SHA256" 2>/dev/null || fetch "https://archive.apache.org/dist/apr/apr-util-$APR_UTIL_VERSION.tar.bz2" "$APR_UTIL_SHA256")"
OPENSSL_SRC="$(fetch "https://github.com/openssl/openssl/releases/download/openssl-$OPENSSL_VERSION/openssl-$OPENSSL_VERSION.tar.gz" "$OPENSSL_SHA256")"
PCRE2_SRC="$(fetch "https://github.com/PCRE2Project/pcre2/releases/download/pcre2-$PCRE2_VERSION/pcre2-$PCRE2_VERSION.tar.gz" "$PCRE2_SHA256")"
EXPAT_SRC="$(fetch "https://github.com/libexpat/libexpat/releases/download/R_${EXPAT_VERSION//./_}/expat-$EXPAT_VERSION.tar.xz" "$EXPAT_SHA256")"
ZLIB_SRC="$(fetch "https://github.com/madler/zlib/releases/download/v$ZLIB_VERSION/zlib-$ZLIB_VERSION.tar.gz" "$ZLIB_SHA256")"
NGHTTP2_SRC="$(fetch "https://github.com/nghttp2/nghttp2/releases/download/v$NGHTTP2_VERSION/nghttp2-$NGHTTP2_VERSION.tar.xz" "$NGHTTP2_SHA256")"

static_sys_libs
export PKG_CONFIG_PATH="$DEPS/lib/pkgconfig"
export CFLAGS="-O2 -fPIC"
export LDFLAGS="-L$DEPS/staticlib"

log "static libraries"
D="$(unpack "$OPENSSL_SRC")"
( cd "$D" && ./config --prefix="$DEPS" --libdir=lib no-shared no-tests no-docs -fPIC && make -j"$JOBS" build_libs && make install_dev )
D="$(unpack "$ZLIB_SRC")"
( cd "$D" && ./configure --prefix="$DEPS" --static && make -j"$JOBS" && make install )
D="$(unpack "$PCRE2_SRC")"
( cd "$D" && ./configure --prefix="$DEPS" --disable-shared --enable-static --with-pic --enable-jit && make -j"$JOBS" && make install )
D="$(unpack "$EXPAT_SRC")"
( cd "$D" && ./configure --prefix="$DEPS" --disable-shared --enable-static --with-pic --without-docbook --without-examples --without-tests && make -j"$JOBS" && make install )
D="$(unpack "$NGHTTP2_SRC")"
( cd "$D" && ./configure --prefix="$DEPS" --disable-shared --enable-static --with-pic --enable-lib-only && make -j"$JOBS" && make install )
# APR / APR-util linked into the httpd program (httpd's own srclib build would link shared libraries that stay in the install folder).
# They are built with libtool shared + static support, which httpd needs to build its modules as DSOs; then the shared files are
# removed and the .la files point to the static ones only, so httpd and the modules link them statically.
D="$(unpack "$APR_SRC")"
( cd "$D" && ./configure --prefix="$DEPS" --enable-shared --enable-static --with-pic && make -j"$JOBS" && make install )
D="$(unpack "$APU_SRC")"
( cd "$D" && ./configure --prefix="$DEPS" --enable-shared --enable-static --with-pic --with-apr="$DEPS" --with-expat="$DEPS" && make -j"$JOBS" && make install )
for l in apr-1 aprutil-1; do
  rm -f "$DEPS/lib/lib$l.so"* "$DEPS/lib/lib$l."*.dylib "$DEPS/lib/lib$l.dylib"
  sed -i.bak "s/^dlname=.*/dlname=''/; s/^library_names=.*/library_names=''/" "$DEPS/lib/lib$l.la"
done

log "httpd"
SRC="$(unpack "$HTTPD_SRC")"
STAGE="$WORK/stage/httpd"
INSTALL="$WORK/stage/httpd-install"
rm -rf "$STAGE" "$INSTALL"
cd "$SRC"
./configure --prefix="$INSTALL" \
  --with-apr="$DEPS" --with-apr-util="$DEPS" --with-expat="$DEPS" --with-z="$DEPS" \
  --with-pcre="$DEPS/bin/pcre2-config" \
  --with-ssl="$DEPS" --with-nghttp2="$DEPS" \
  --with-mpm=event \
  --enable-so --enable-mods-shared=most \
  --enable-ssl=shared --enable-http2=shared --enable-proxy=shared --enable-proxy-fcgi=shared --enable-proxy-http=shared \
  --enable-proxy-wstunnel=shared --enable-rewrite=shared --enable-headers=shared --enable-deflate=shared --enable-expires=shared \
  --enable-socache-shmcb=shared --enable-vhost-alias=shared --enable-slotmem-shm=shared \
  --disable-lua --disable-luajit --disable-dav --disable-dav-fs --disable-dav-lock --disable-ldap --disable-authnz-ldap --disable-md --disable-brotli
make -j"$JOBS"
make install

mkdir -p "$STAGE/conf"
cp -R "$INSTALL/bin" "$INSTALL/modules" "$STAGE/"
cp "$INSTALL/conf/mime.types" "$STAGE/conf/"
cp "$SRC/LICENSE" "$SRC/NOTICE" "$STAGE/"
# helper scripts that point to the build folder
rm -f "$STAGE/bin/apxs" "$STAGE/bin/apr-1-config" "$STAGE/bin/apu-1-config" "$STAGE/bin/envvars" "$STAGE/bin/envvars-std" "$STAGE/bin/dbmmanage" "$STAGE/bin/apachectl"
strip "$STAGE/bin/httpd" 2>/dev/null || true
strip -x "$STAGE/modules/"*.so 2>/dev/null || true
chmod 755 "$STAGE/bin/"*
"$STAGE/bin/httpd" -V
"$STAGE/bin/httpd" -l
check_linkage "$STAGE/bin/httpd" "$STAGE/modules/mod_ssl.so" "$STAGE/modules/mod_http2.so"
package httpd "$HTTPD_VERSION" "$STAGE"
