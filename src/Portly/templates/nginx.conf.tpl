# {{GENERATED}}

worker_processes  1;
error_log  "{{LOGS}}/nginx-error.log" warn;
pid        "{{TMP}}/nginx.pid";

events {
    worker_connections  1024;
}

http {
    include       "{{NGINX_DIR}}/conf/mime.types";
    default_type  application/octet-stream;
    sendfile      off;
    keepalive_timeout  65;
    client_max_body_size 1024m;
    server_names_hash_bucket_size 128;
    server_tokens off;
    access_log    off;
    charset       utf-8;
    fastcgi_read_timeout 3600;
    fastcgi_buffers 16 16k;
    fastcgi_buffer_size 32k;

    # http://localhost -> Portly dashboard and tools (/adminer, /phpmyadmin)
    server {
        listen {{LISTEN_HTTP}} default_server;
        {{LISTEN_HTTPS}}
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
