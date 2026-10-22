<VirtualHost _default_:{{HTTPS_PORT}}>
    ServerName localhost
    DocumentRoot "{{DASHBOARD}}"
    SSLEngine on
    SSLCertificateFile "{{CRT}}"
    SSLCertificateKeyFile "{{KEY}}"
    IncludeOptional "{{ETC}}/apache/alias/*.conf"
</VirtualHost>
