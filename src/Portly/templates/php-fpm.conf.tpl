; {{GENERATED}}
;
; macOS / Linux: one php-fpm per PHP version, kept in the foreground so Portly can stop it.
[global]
pid = {{PHP_FPM_PID}}
error_log = {{PHP_FPM_LOG}}
daemonize = no

[portly]
listen = 127.0.0.1:{{PHP_PORT}}
pm = static
pm.max_children = {{PHP_CHILDREN}}
pm.max_requests = {{PHP_MAX_REQUESTS}}
clear_env = no
catch_workers_output = yes
decorate_workers_output = no
