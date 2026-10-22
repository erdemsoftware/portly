# {{GENERATED}}
bind {{BIND}}
port {{REDIS_PORT}}
dir "{{REDIS_DATA}}"
logfile "{{LOGS}}/redis.log"
maxmemory 256mb
maxmemory-policy allkeys-lru
save 900 1
save 300 10
