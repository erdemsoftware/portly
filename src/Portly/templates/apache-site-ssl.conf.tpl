<VirtualHost *:{{HTTPS_PORT}}>
    ServerName {{HOST}}
    ServerAlias *.{{HOST}}
    DocumentRoot "{{SITE_ROOT}}"
    <Directory "{{SITE_ROOT}}">
        Options Indexes FollowSymLinks
        AllowOverride All
        Require all granted
    </Directory>
    {{PHP_HANDLER}}
    SSLEngine on
    SSLCertificateFile "{{CRT}}"
    SSLCertificateKeyFile "{{KEY}}"
</VirtualHost>
