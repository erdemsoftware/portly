# {{GENERATED}}
# To keep your own changes: rename this file so it does not start with "auto."

server {
    listen {{LISTEN_HTTP}};
    {{LISTEN_HTTPS}}
    server_name {{SERVER_NAMES}};
    root "{{SITE_ROOT}}";
    index index.php index.html index.htm;
    {{SSL_CERT}}

    location / {
        try_files $uri $uri/ /index.php$is_args$args;
    }

    location ~ /\.(?!well-known) {
        deny all;
    }

    {{PHP_LOCATION}}
}
