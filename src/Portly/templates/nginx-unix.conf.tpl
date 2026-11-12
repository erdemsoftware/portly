# {{GENERATED}}
#
# macOS / Linux: nginx stays in the foreground (daemon off) so that Portly owns the process.

daemon off;
master_process on;
worker_processes  1;
error_log  "{{LOGS}}/nginx-error.log" warn;
pid        "{{TMP}}/nginx.pid";

events {
    worker_connections  1024;
}

http {
    include       "{{NGINX_DIR}}/conf/mime.types";
    default_type  application/octet-stream;
    sendfile      on;
    keepalive_timeout  65;
    client_max_body_size 1024m;
    server_names_hash_bucket_size 128;
    server_tokens off;
    access_log    off;
    charset       utf-8;
    fastcgi_read_timeout 3600;
    fastcgi_buffers 16 16k;
    fastcgi_buffer_size 32k;
    client_body_temp_path "{{TMP}}/nginx/client_body";
    proxy_temp_path       "{{TMP}}/nginx/proxy";
    fastcgi_temp_path     "{{TMP}}/nginx/fastcgi";
    uwsgi_temp_path       "{{TMP}}/nginx/uwsgi";
    scgi_temp_path        "{{TMP}}/nginx/scgi";

    # http://localhost -> Portly dashboard and tools (/adminer, /phpmyadmin)
    server {
        listen {{LISTEN_HTTP}} default_server;
        {{LISTEN_HTTPS}}
        {{LISTEN_V6}}
        server_name localhost;
        root "{{DASHBOARD}}";
        index index.php index.html;
        allow 127.0.0.1;
        allow ::1;
        deny all;
        {{SSL_CERT}}
        include "{{ETC}}/nginx/alias/*.conf";
        {{PHP_LOCATION}}
    }

    # One file per site (auto.*.conf). Your own *.conf files in this folder are kept.
    include "{{ETC}}/nginx/sites/*.conf";
}
