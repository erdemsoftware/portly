#!/usr/bin/env bash
# nginx with OpenSSL, PCRE2 and zlib linked statically. Layout (like the Windows package):
#   nginx-<v>-<os>-<arch>/nginx        the program (Portly starts it with -p <folder>/ -c <config>)
#   nginx-<v>-<os>-<arch>/conf/        mime.types, fastcgi_params ... (Portly's own nginx.conf includes them)
. "$(dirname "$0")/lib.sh"

log "nginx $NGINX_VERSION on $OS-$ARCH"
NGINX_SRC="$(fetch "https://nginx.org/download/nginx-$NGINX_VERSION.tar.gz" "$NGINX_SHA256")"
OPENSSL_SRC="$(fetch "https://github.com/openssl/openssl/releases/download/openssl-$OPENSSL_VERSION/openssl-$OPENSSL_VERSION.tar.gz" "$OPENSSL_SHA256")"
PCRE2_SRC="$(fetch "https://github.com/PCRE2Project/pcre2/releases/download/pcre2-$PCRE2_VERSION/pcre2-$PCRE2_VERSION.tar.gz" "$PCRE2_SHA256")"
ZLIB_SRC="$(fetch "https://github.com/madler/zlib/releases/download/v$ZLIB_VERSION/zlib-$ZLIB_VERSION.tar.gz" "$ZLIB_SHA256")"

static_sys_libs
SRC="$(unpack "$NGINX_SRC")"
OPENSSL_DIR="$(unpack "$OPENSSL_SRC")"
PCRE2_DIR="$(unpack "$PCRE2_SRC")"
ZLIB_DIR="$(unpack "$ZLIB_SRC")"
STAGE="$WORK/stage/nginx"
rm -rf "$STAGE"

cd "$SRC"
configure() {
  ./configure --prefix="$STAGE" \
    --with-http_ssl_module --with-http_v2_module $1 \
    --with-http_realip_module --with-http_gzip_static_module --with-http_stub_status_module \
    --with-http_sub_module --with-http_gunzip_module --with-http_auth_request_module \
    --with-threads \
    --with-pcre="$PCRE2_DIR" --with-pcre-jit --with-zlib="$ZLIB_DIR" \
    --with-openssl="$OPENSSL_DIR" --with-openssl-opt="no-shared no-tests no-docs -fPIC" \
    --with-cc-opt="-O2 -fstack-protector-strong -fPIC" --with-ld-opt="-L$DEPS/staticlib"
}
if ! configure "--with-http_v3_module"; then
  echo "HTTP/3 could not be configured here, building without it"
  make clean >/dev/null 2>&1 || true
  configure ""
fi
make -j"$JOBS"
make install

# program next to conf/ like on Windows; nothing else is needed at run time
mv "$STAGE/sbin/nginx" "$STAGE/nginx"
rm -rf "$STAGE/sbin" "$STAGE/html" "$STAGE/logs" "$STAGE/conf/"*.default
cp "$SRC/LICENSE" "$STAGE/LICENSE"
strip "$STAGE/nginx" 2>/dev/null || true
"$STAGE/nginx" -V
check_linkage "$STAGE/nginx"
package nginx "$NGINX_VERSION" "$STAGE"
