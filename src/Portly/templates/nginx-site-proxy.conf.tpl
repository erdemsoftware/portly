# {{GENERATED}}
# To keep your own changes: rename this file so it does not start with "auto."
# This site runs on FrankenPHP (127.0.0.1:{{PROXY_PORT}}); Nginx only forwards the requests.

server {
    listen {{LISTEN_HTTP}};
    {{LISTEN_HTTPS}}
    {{LISTEN_V6}}
    server_name {{SERVER_NAMES}};
    {{SSL_CERT}}

    location / {
        proxy_pass http://127.0.0.1:{{PROXY_PORT}};
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-Host $host;
        proxy_set_header X-Forwarded-Port $server_port;
        proxy_read_timeout 3600;
        proxy_buffering off;
    }
}
