# {{GENERATED}}
#
# Portly converts this file to caddy.json ("caddy adapt") and removes Caddy's automatic certificates:
# HTTPS uses Portly's own certificates (usr\ssl, etc\ssl\sites).
{
	admin off
	auto_https off
	persist_config off
	storage file_system "{{TMP}}/caddy"
	{{DEFAULT_BIND}}
	log {
		output file "{{LOGS}}/caddy-error.log"
		level WARN
	}
}

# http://localhost -> Portly dashboard and tools (/adminer, /phpmyadmin), this computer only
(portly_dashboard) {
	@remote not remote_ip 127.0.0.1 ::1
	respond @remote 403
{{TOOLS}}	handle {
		root * "{{DASHBOARD}}"
		{{PHP}}
		file_server
	}
}

http://:{{HTTP_PORT}} {
	import portly_dashboard
}

{{DEFAULT_SSL}}

# One block per site
{{SITES}}
