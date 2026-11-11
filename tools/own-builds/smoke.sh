#!/usr/bin/env bash
# Smoke test of a finished package: it is unpacked into a fresh folder and run from there (relocatability).
#   smoke.sh <nginx|httpd|memcached|php> <package.tar.gz>
set -euo pipefail

KIND="${1:?kind}"; PKG="${2:?package}"
T="$(mktemp -d)"
trap 'kill $(cat "$T"/*.pid 2>/dev/null) 2>/dev/null || true; rm -rf "$T"' EXIT
tar -xzf "$PKG" -C "$T"
P="$(echo "$T"/*/)"; P="${P%/}"
PORT="${SMOKE_PORT:-18199}"
SPORT="$((PORT + 1))"
echo "unpacked into $P"

fail() { echo "SMOKE FAIL: $*" >&2; exit 1; }
wait_http() { for _ in $(seq 1 40); do curl -fsk -o /dev/null "$1" && return 0; sleep 0.25; done; return 1; }

# self-signed certificate for the HTTPS check
make_cert() {
  openssl req -x509 -newkey rsa:2048 -nodes -keyout "$T/key.pem" -out "$T/crt.pem" -days 2 -subj "/CN=localhost" >/dev/null 2>&1 \
    || fail "openssl could not make a test certificate"
}

case "$KIND" in
nginx)
  "$P/nginx" -v
  "$P/nginx" -V 2>&1 | tee "$T/V.txt"
  for m in http_ssl_module http_v2_module http_realip_module http_gzip_static_module http_stub_status_module; do grep -q "with-$m" "$T/V.txt" || fail "nginx lacks $m"; done
  grep -q "http_v3_module" "$T/V.txt" && echo "HTTP/3: yes" || echo "HTTP/3: no"
  grep -qE "built with OpenSSL 3\." "$T/V.txt" || fail "OpenSSL is not built in"
  make_cert
  mkdir -p "$P/logs" "$P/temp" "$T/www"; echo "portly-nginx-ok" > "$T/www/index.html"
  cat > "$T/nginx.conf" <<CONF
worker_processes 1;
pid "$T/nginx.pid";
error_log "$T/error.log" warn;
events { worker_connections 64; }
http {
  include "$P/conf/mime.types";
  access_log off;
  server {
    listen 127.0.0.1:$PORT;
    listen 127.0.0.1:$SPORT ssl;
    http2 on;
    ssl_certificate "$T/crt.pem";
    ssl_certificate_key "$T/key.pem";
    root "$T/www";
    location = /status { stub_status; }
  }
}
CONF
  "$P/nginx" -t -p "$P/" -c "$T/nginx.conf"
  "$P/nginx" -p "$P/" -c "$T/nginx.conf"
  wait_http "http://127.0.0.1:$PORT/" || { cat "$T/error.log"; fail "nginx does not answer on HTTP"; }
  curl -fs "http://127.0.0.1:$PORT/" | grep -q portly-nginx-ok || fail "wrong page"
  curl -fsk --http2 -o /dev/null -w "https %{http_code} %{http_version}\n" "https://127.0.0.1:$SPORT/" || fail "nginx does not answer on HTTPS"
  "$P/nginx" -p "$P/" -c "$T/nginx.conf" -s quit
  for _ in $(seq 1 40); do [ -e "$T/nginx.pid" ] || break; sleep 0.25; done
  [ ! -e "$T/nginx.pid" ] || fail "nginx did not stop"
  ;;
httpd)
  H="$P/bin/httpd"
  "$H" -v
  "$H" -V
  "$H" -M -d "$P" -C "ServerName localhost" -C "Listen 127.0.0.1:$PORT" -f /dev/null 2>/dev/null || true
  ls "$P/modules"
  for m in ssl proxy proxy_fcgi rewrite headers http2; do [ -f "$P/modules/mod_$m.so" ] || fail "mod_$m.so is missing"; done
  make_cert
  mkdir -p "$P/logs" "$T/www"; echo "portly-httpd-ok" > "$T/www/index.html"
  cat > "$T/httpd.conf" <<CONF
ServerRoot "$P"
Listen 127.0.0.1:$PORT
Listen 127.0.0.1:$SPORT
LoadModule unixd_module modules/mod_unixd.so
LoadModule authz_core_module modules/mod_authz_core.so
LoadModule mime_module modules/mod_mime.so
LoadModule dir_module modules/mod_dir.so
LoadModule alias_module modules/mod_alias.so
LoadModule log_config_module modules/mod_log_config.so
LoadModule headers_module modules/mod_headers.so
LoadModule rewrite_module modules/mod_rewrite.so
LoadModule socache_shmcb_module modules/mod_socache_shmcb.so
LoadModule proxy_module modules/mod_proxy.so
LoadModule proxy_fcgi_module modules/mod_proxy_fcgi.so
LoadModule ssl_module modules/mod_ssl.so
LoadModule http2_module modules/mod_http2.so
ServerName localhost
PidFile "$T/httpd.pid"
ErrorLog "$T/error.log"
TypesConfig conf/mime.types
DocumentRoot "$T/www"
<Directory "$T/www">
  Require all granted
</Directory>
Mutex file:$T default
Protocols h2 http/1.1
SSLSessionCache "shmcb:$T/ssl_scache(512000)"
<VirtualHost 127.0.0.1:$SPORT>
  SSLEngine on
  SSLCertificateFile "$T/crt.pem"
  SSLCertificateKeyFile "$T/key.pem"
</VirtualHost>
CONF
  "$H" -t -f "$T/httpd.conf"
  "$H" -f "$T/httpd.conf" -k start
  wait_http "http://127.0.0.1:$PORT/" || { cat "$T/error.log"; fail "httpd does not answer on HTTP"; }
  curl -fs "http://127.0.0.1:$PORT/" | grep -q portly-httpd-ok || fail "wrong page"
  curl -fsk --http2 -o /dev/null -w "https %{http_code} %{http_version}\n" "https://127.0.0.1:$SPORT/" || { cat "$T/error.log"; fail "httpd does not answer on HTTPS"; }
  "$H" -f "$T/httpd.conf" -k graceful-stop
  for _ in $(seq 1 40); do [ -e "$T/httpd.pid" ] || break; sleep 0.25; done
  [ ! -e "$T/httpd.pid" ] || fail "httpd did not stop"
  ;;
memcached)
  "$P/memcached" -V
  "$P/memcached" -h | head -3
  "$P/memcached" -p "$PORT" -U 0 -l 127.0.0.1 -m 16 &
  echo $! > "$T/mc.pid"
  for _ in $(seq 1 40); do (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null && break; sleep 0.25; done
  exec 3<>"/dev/tcp/127.0.0.1/$PORT"
  printf 'set k 0 0 2\r\nok\r\nget k\r\nversion\r\nquit\r\n' >&3
  ANS="$(cat <&3)"; echo "$ANS"
  echo "$ANS" | grep -q "^VERSION " || fail "memcached did not answer 'version'"
  echo "$ANS" | grep -q "^ok" || fail "memcached did not return the stored value"
  kill "$(cat "$T/mc.pid")"
  ;;
php)
  "$P/php" -v
  "$P/php-fpm" -v
  "$P/php" -m | tr '\n' ' '; echo
  for e in curl mbstring openssl pdo_mysql mysqli intl gd zip sodium xsl; do "$P/php" -m | grep -qix "$e" || echo "NOTE: extension $e is not in this build"; done
  OUT="$("$P/php" -d "zend_extension=$P/ext/xdebug.so" -v)"; echo "$OUT"
  echo "$OUT" | grep -q "with Xdebug v" || fail "Xdebug does not load"
  "$P/php" -d "zend_extension=$P/ext/xdebug.so" -d xdebug.mode=debug -r 'var_dump(extension_loaded("xdebug"), function_exists("xdebug_info"));' | tee "$T/x.txt"
  grep -q "bool(true)" "$T/x.txt" || fail "xdebug functions missing"
  # php-fpm answers a request: a one-pool config on a test port
  cat > "$T/fpm.conf" <<CONF
[global]
daemonize = no
error_log = $T/fpm.log
pid = $T/fpm.pid
[www]
listen = 127.0.0.1:$PORT
pm = static
pm.max_children = 1
clear_env = no
CONF
  "$P/php-fpm" -y "$T/fpm.conf" -n -F -R 2>/dev/null &
  for _ in $(seq 1 40); do (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null && break; sleep 0.25; done
  (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null || { cat "$T/fpm.log" || true; fail "php-fpm does not listen"; }
  kill -QUIT "$(cat "$T/fpm.pid")"
  ;;
*) fail "unknown kind $KIND" ;;
esac
echo "SMOKE OK: $KIND"
