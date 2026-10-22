# {{GENERATED}}

[client]
port={{MYSQL_PORT}}
default-character-set=utf8mb4

[mysqld]
basedir="{{MYSQL_DIR}}"
datadir="{{MYSQL_DATA}}"
port={{MYSQL_PORT}}
bind-address={{BIND}}
character-set-server={{CHARSET}}
collation-server={{COLLATION}}
default-storage-engine={{STORAGE_ENGINE}}
max_allowed_packet=512M
innodb_buffer_pool_size=256M
max_connections=200
table_open_cache=1024
log-error="{{LOGS}}/mysql-error.log"
secure-file-priv=""
explicit_defaults_for_timestamp=1
{{MYSQL_EXTRA}}
{{SQL_MODE}}

[mysqldump]
quick
max_allowed_packet=512M
