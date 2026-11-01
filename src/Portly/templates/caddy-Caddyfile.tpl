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
# route keeps this order: on its own Caddy sorts respond behind handle, so the refusal would never run.
# Both must hold: a local client and a local name (a foreign name on 127.0.0.1 is DNS rebinding).
(portly_dashboard) {
	route {
		@refused not {
			remote_ip 127.0.0.1 ::1
			header_regexp Host "(?i){{LOCAL_HOST}}"
		}
		respond @refused 403
		# Portly's own shares connect from 127.0.0.1 too: see WebConfig
		@shared header X-Forwarded-Host *
		respond @shared 403
{{TOOLS}}		handle {
			root * "{{DASHBOARD}}"
			{{PHP}}
			file_server
		}
	}
}

http://:{{HTTP_PORT}} {
	import portly_dashboard
}

{{DEFAULT_SSL}}

# One block per site
{{SITES}}
