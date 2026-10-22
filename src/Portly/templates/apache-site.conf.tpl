# {{GENERATED}}
# To keep your own changes: rename this file so it does not start with "auto."

<VirtualHost *:{{HTTP_PORT}}>
    ServerName {{HOST}}
    {{ALIAS}}
    DocumentRoot "{{SITE_ROOT}}"
    <Directory "{{SITE_ROOT}}">
        Options Indexes FollowSymLinks
        AllowOverride All
        Require all granted
    </Directory>
    {{PHP_HANDLER}}
</VirtualHost>
