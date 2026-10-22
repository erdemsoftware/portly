# {{GENERATED}}
#
# Portly does not modify Apache's own conf\httpd.conf. Apache is started with this file instead.

Define SRVROOT "{{APACHE_DIR}}"
ServerRoot "${SRVROOT}"
{{LISTEN}}

LoadModule access_compat_module modules/mod_access_compat.so
LoadModule actions_module modules/mod_actions.so
LoadModule alias_module modules/mod_alias.so
LoadModule allowmethods_module modules/mod_allowmethods.so
LoadModule auth_basic_module modules/mod_auth_basic.so
LoadModule authn_core_module modules/mod_authn_core.so
LoadModule authn_file_module modules/mod_authn_file.so
LoadModule authz_core_module modules/mod_authz_core.so
LoadModule authz_groupfile_module modules/mod_authz_groupfile.so
LoadModule authz_host_module modules/mod_authz_host.so
LoadModule authz_user_module modules/mod_authz_user.so
LoadModule autoindex_module modules/mod_autoindex.so
LoadModule deflate_module modules/mod_deflate.so
LoadModule dir_module modules/mod_dir.so
LoadModule env_module modules/mod_env.so
LoadModule expires_module modules/mod_expires.so
LoadModule filter_module modules/mod_filter.so
LoadModule headers_module modules/mod_headers.so
LoadModule include_module modules/mod_include.so
LoadModule log_config_module modules/mod_log_config.so
LoadModule mime_module modules/mod_mime.so
LoadModule negotiation_module modules/mod_negotiation.so
LoadModule proxy_module modules/mod_proxy.so
LoadModule proxy_fcgi_module modules/mod_proxy_fcgi.so
LoadModule proxy_http_module modules/mod_proxy_http.so
LoadModule proxy_wstunnel_module modules/mod_proxy_wstunnel.so
LoadModule rewrite_module modules/mod_rewrite.so
LoadModule setenvif_module modules/mod_setenvif.so
LoadModule socache_shmcb_module modules/mod_socache_shmcb.so
LoadModule ssl_module modules/mod_ssl.so
LoadModule vhost_alias_module modules/mod_vhost_alias.so

ServerName localhost
ServerAdmin admin@localhost
ServerTokens Prod
ServerSignature Off

<Directory />
    AllowOverride None
    Require all denied
</Directory>

DocumentRoot "{{DASHBOARD}}"
<Directory "{{DASHBOARD}}">
    Options -Indexes
    AllowOverride None
    Require local
</Directory>

<Directory "{{DOC_ROOT}}">
    Options Indexes FollowSymLinks
    AllowOverride All
    Require all granted
</Directory>

DirectoryIndex index.php index.html index.htm

<Files ".ht*">
    Require all denied
</Files>

ErrorLog "{{LOGS}}/apache-error.log"
LogLevel warn
LogFormat "%h %l %u %t \"%r\" %>s %b \"%{Referer}i\" \"%{User-Agent}i\"" combined
# Access log is off for speed. Remove the # below to enable it.
# CustomLog "{{LOGS}}/apache-access.log" combined

TypesConfig conf/mime.types
AddType application/x-compress .Z
AddType application/x-gzip .gz .tgz

EnableMMAP Off
EnableSendfile Off
AcceptFilter http none
AcceptFilter https none
Timeout 3600
ThreadsPerChild 150
ThreadStackSize 8388608

# PHP runs as a FastCGI process. Every PHP version has its own port,
# so each site can use a different PHP version.
ProxyTimeout 3600
ProxyFCGIBackendType GENERIC
# Windows: Apache sends "/E:/site/index.php"; PHP needs "E:/site/index.php"
ProxyFCGISetEnvIf "reqenv('SCRIPT_FILENAME') =~ m#^/([A-Za-z]:/.*)$#" SCRIPT_FILENAME "$1"
ProxyFCGISetEnvIf "reqenv('PATH_TRANSLATED') =~ m#^/([A-Za-z]:/.*)$#" PATH_TRANSLATED "$1"
{{PHP_HANDLER}}

SSLRandomSeed startup builtin
SSLRandomSeed connect builtin
SSLProtocol -all +TLSv1.2 +TLSv1.3
SSLCipherSuite HIGH:!aNULL:!MD5
SSLHonorCipherOrder On
SSLSessionCache "shmcb:logs/ssl_scache(512000)"
SSLSessionCacheTimeout 300

# http://localhost -> Portly dashboard and tools (/adminer, /phpmyadmin)
<VirtualHost _default_:{{HTTP_PORT}}>
    ServerName localhost
    DocumentRoot "{{DASHBOARD}}"
    IncludeOptional "{{ETC}}/apache/alias/*.conf"
</VirtualHost>
{{DEFAULT_SSL}}

# One file per site (auto.*.conf). Your own *.conf files in this folder are kept.
IncludeOptional "{{ETC}}/apache/sites/*.conf"
