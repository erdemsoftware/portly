<p align="center"><img src="assets/portly-256.png" width="96" alt="Portly"></p>

<h1 align="center">Portly</h1>
<p align="center">
  Schnelle, portable, quelloffene lokale Webentwicklungsumgebung für Windows<br>
  <sub>von <a href="https://erdemsoftware.com">Erdem Software</a> · MIT · keine Werbung, kein Tracking</sub>
</p>

<p align="center"><a href="README.md">English</a> · <a href="README.tr.md">Türkçe</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.es.md">Español</a> · <a href="README.pt-BR.md">Português (BR)</a> · <b>Deutsch</b> · <a href="README.fr.md">Français</a> · <a href="README.ru.md">Русский</a> · <a href="README.ja.md">日本語</a> · <a href="README.ko.md">한국어</a> · <a href="README.ar.md">العربية</a> · <a href="README.id.md">Bahasa Indonesia</a></p>
<p align="center"><sub>Die App selbst unterstützt 31 Sprachen (Arabisch, Persisch und Urdu von rechts nach links).</sub></p>

---

<p align="center"><img src="assets/screenshots/en-services.png" width="49%" alt="Dienste"> <img src="assets/screenshots/en-projects.png" width="49%" alt="Projekte"></p>
<p align="center"><img src="assets/screenshots/en-qr.png" width="49%" alt="Projekt mit QR-Code auf dem Telefon öffnen"> <img src="assets/screenshots/en-newproject.png" width="40%" alt="Neues Projekt"></p>

Portly betreibt Apache oder Nginx, PHP, MySQL oder MariaDB, PostgreSQL, MongoDB, Redis, Memcached, Node.js, Mailpit, Composer, Adminer und phpMyAdmin aus einem einzigen Ordner. Keine Installation nötig, und standardmäßig wird nichts in Windows installiert: keine Windows-Dienste (ein optionaler Dienstmodus, der aus ist, bis du ihn unter Einstellungen > Funktionen einschaltest, kann den Stack als Dienst ausführen), keine Registry-Altlasten. Es funktioniert sogar von einem USB-Stick.

## Warum Portly?

| | Laragon | **Portly** |
|---|---|---|
| Lizenz | Proprietär, für kommerzielle Nutzung kostenpflichtig | **MIT, völlig kostenlos, keine Werbung** |
| App-Updates | Neuen Installer herunterladen | **Ein Klick in der App** (SHA-256-geprüft) |
| Paketliste | Lokale `packages.conf` | **Remote aktualisierter Katalog** + aktuelle PHP-Versionen von windows.php.net |
| PHP-Version pro Site | Nein | **Ja** (jede PHP-Version auf ihrem eigenen FastCGI-Port) |
| HTTPS | Ein Zertifikat, pro Projekt neu erstellt | **Lokale Root-CA**, einmal als vertrauenswürdig eingerichtet, ein Zertifikat pro Site |
| Netzwerksicherheit | Lauscht auf allen Schnittstellen | **Standardmäßig 127.0.0.1** (keine Firewall-Abfrage) |
| PHP-Härtung | Standardwerte | `session.upload_progress` und `allow_url_include` **aus** |
| Absturzsicherheit | — | Wenn Portly beendet wird, stoppt Windows die von ihm gestarteten Server (Job-Objekt) |
| Portkonflikte | Fehlermeldung | Zeigt das Programm, das den Port belegt; **mit einem Klick auf einen freien Port wechseln** oder das Programm schließen |
| Teilen | ngrok (Konto erforderlich) | **Lokales Netzwerk** (Telefon / Tablet im selben WLAN, QR-Code, https, Passwort) und **Internet** über Cloudflare: zufällige Adresse ohne Konto oder eine **feste Adresse auf deiner eigenen Domain** |
| Migration | — | Projekte und Datenbanken aus **Laragon, XAMPP, WampServer, AMPPS, UwAmp, Uniform Server, Open Server, EasyPHP, MAMP** (Projekte aus Herd und Local) |
| Datenbanken | MySQL | **MySQL oder MariaDB, PostgreSQL, MongoDB, SQLite**; Redis und Memcached |
| Datenbankwerkzeuge | HeidiSQL | **phpMyAdmin** (öffnet angemeldet, solange root kein Passwort hat), **Adminer, HeidiSQL, DBeaver, Beekeeper Studio, DbGate, DB Browser for SQLite** |
| WordPress | Manuell | **WordPress mit einem Klick** (WP-CLI, Admin-Benutzer bereit) |
| Sprachen | 34 (txt) | **31**, einfaches JSON, offen für Beiträge |
| Windows 7 / 8 / 8.1 | Unklar | **Eigene Legacy-Edition** |

## Funktionen

- **Projekte**: Jeder Ordner in `www` wird zu `https://name.portly`; Ordner im Laravel-Stil mit `public` werden erkannt. Gruppenordner und Unterprojekte (`blog.shop.portly`), Vorlagen für Laravel, Symfony, CodeIgniter, CakePHP, Drupal, Vue, React, Svelte, Nuxt, Next.js, Angular und Astro. Jede Karte zeigt, womit das Projekt erstellt wurde, und seine Datenbank.
- **Projekt teilen**
  - *Lokales Netzwerk*: Öffne es mit einem QR-Code auf dem Telefon. Funktioniert über https; eine Anleitung auf dem Telefon installiert das Zertifikat von Portly einmalig auf iPhone und Android. Optionales Passwort, Zeitlimit, Start mit Portly, Liste der verbundenen Geräte; PHP-Fehler vom Telefon erscheinen im Protokoll von Portly.
  - *Internet*: ein kostenloser Cloudflare-Quick-Tunnel (ohne Konto) oder deine eigene Domain wie `demo.example.com` über dein Cloudflare-Konto. Gleiches Passwort, Zeitlimit und Geräteliste.
- **PHP-Version pro Site**, **PHP-Erweiterungen** mit einem Klick (17 PECL-Erweiterungen, pro PHP-Version geprüft), **Xdebug**, Terminals, die der PHP- / Node-Version des Projekts folgen (`.php-version`, `composer.json`, `.nvmrc`).
- **Dienste**: Alle starten / Alle stoppen mit einem Schloss pro Dienst (wähle, welche zur Gruppe gehören), MySQL / MariaDB in einer Karte, optional Tomcat + Java.
- **Update-Hinweise** („PHP 8.6 ist da“ → installieren und mit einem Klick umschalten), Hinweise zum Lebensende und CVE-Warnungen.
- **Datenbanksicherheit**: Prüfen & Reparieren, sichere Wechsel zwischen MySQL- / MariaDB-Versionen, Import großer SQL-Dateien, automatische Sicherungen, Zurücksetzen vergessener root-Passwörter, InnoDB-Wiederherstellungsmodus.
- **Entwickler-Workflow**: Hintergrundjobs (Queue, Scheduler, Vite), Site-Protokolle, Profiler, `portly`-CLI mit Hooks, Projektdatei `portly.yml`, Push / Pull über SSH.
- **Sites**: zusätzliche und Wildcard-Domains, eigene Zertifikate, Archiv, SSL reparieren, Zertifikatsvertrauen für Firefox / Java, **Caddy** und **FrankenPHP** (Octane).
- **Problemvermeidung**: OneDrive-Ordner, Nicht-ASCII- und lange Pfade, ein zweites PHP oder PHPRC, Antivirus, der die hosts-Datei blockiert, Quarantäneprüfung.
- Moderne Seitenleisten-Oberfläche mit hellem / dunklem Design, das Windows folgt, DPI-Unterstützung pro Monitor und Unterstützung für Bildschirmleser. Selten genutzte Funktionen werden unter Einstellungen > Funktionen eingeschaltet.

## Editionen

| Download | Windows | Updates | Pakete |
|---|---|---|---|
| `Portly-x.y.z-win10-11.zip` | 10 / 11 (64-Bit) | **Ja**: App + Paketkatalog | Aktuell: PHP 8.5, MySQL 8.4 LTS / 26.x, Apache 2.4, Node 24 LTS ... |
| `Portly-x.y.z-win7-8.zip` (Legacy) | 7 SP1 / 8 / 8.1 (64-Bit) | **Nein** (eingefrorener Katalog) | Letzte Versionen, die unter Windows 7 laufen: PHP 8.2, MySQL 5.7, Apache 2.4 VS16, Node 13 |
| macOS / Linux | — | — | **Beta**: siehe [Portly 2 Beta](#portly-2-beta) weiter unten. |

Die Legacy-Edition benötigt .NET Framework 4.8 (ist bereits Teil von Windows 10 / 11).

<a id="portly-2-beta"></a>

## Portly 2 Beta: macOS, Linux und eine neue Windows-App

Portly 2 läuft auf **macOS (Apple Silicon und Intel), Linux (x64 und arm64) und Windows 10 / 11** mit überall derselben Versionsnummer. Es ist in der Beta-Phase: Portly 1.x bleibt die stabile Version, und auf Portly 2 wird nicht automatisch aktualisiert.

- **macOS / Linux**, eine Zeile, ohne sudo (SHA-256-geprüft; macOS: `/Applications/Portly.app`, Linux: `~/.local/share/portly`):

  ```sh
  curl -fsSL https://raw.githubusercontent.com/erdemsoftware/portly/main/tools/install.sh | sh
  ```

  Oder lade die `.dmg`, `.AppImage`, `.deb` oder `.tar.gz` von [Releases](https://github.com/erdemsoftware/portly/releases) herunter.
- **Windows 10 / 11**: `Portly-2.x.y-beta.n-windows-x64.zip` von derselben Seite, in einen neuen Ordner entpackt.
- Unter macOS / Linux: PHP mit Xdebug, Caddy, Apache, Nginx, MySQL, MariaDB, PostgreSQL, MongoDB, Redis, Memcached, Mailpit und Node.js in `~/Portly`; Projekte öffnen sich unter `https://name.localhost`.

## Schnellstart

1. Lade eine ZIP-Datei von [Releases](https://github.com/erdemsoftware/portly/releases) herunter und entpacke sie in einen Ordner **ohne Leerzeichen**, z. B. `C:\Portly`.
2. Starte `Portly.exe`. Die empfohlenen Pakete sind beim ersten Start vorausgewählt: Klicke auf **Ausgewählte installieren**.
3. Klicke auf **Alle starten**.
4. Erstelle einen Ordner in `www`: `www\shop` wird zu `https://shop.portly`. Ein Ordner mit einem Domainnamen (`www\example.com`) öffnet sich als `https://example.com`, sodass die URLs beim Livegang gleich bleiben; ein Umschalter Lokal / Live auf seiner Karte holt die Live-Seite auf deinen PC, während die lokale Kopie weiter unter `https://example.com.portly` funktioniert.

`http://localhost` öffnet ein Dashboard mit allen Sites, Adminer, Mailpit und `phpinfo()`. Mehr in [docs/04-kullanim.md](docs/04-kullanim.md) (türkisch).

```
Portly\
├─ Portly.exe            the app
├─ lang\  templates\     languages and config templates (renewed by updates)
├─ bin\                  packages: bin\php\php-8.5.12-..., bin\mysql\mysql-8.4.11-winx64 ...
├─ etc\                  generated configuration (rewritten at every start)
├─ usr\                  your settings, own templates, SSL authority (never touched by updates)
├─ data\                 databases (one folder per MySQL series)
├─ www\                  your projects (another folder can be chosen in Settings)
└─ logs\  tmp\  backup\
```

Um eine Konfigurationsvorlage zu ändern, kopiere sie von `templates\` nach `usr\templates\` und bearbeite die Kopie; Updates lassen sie unberührt.

## Aus dem Quellcode bauen

Voraussetzungen: ein beliebiges .NET SDK 6 oder neuer (die App zielt auf .NET Framework 4.8, daher läuft sie unter Windows 7 SP1 – 11 ohne zusätzliche Laufzeiten). Mit installiertem Inno Setup erstellt `build.ps1` außerdem die Installationsprogramme.

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1            # both editions -> dist\
dotnet build src\Portly\Portly.csproj -c Release              # Windows 10/11 edition only
dotnet build src\Portly\Portly.csproj -c Release -p:Edition=Legacy
dotnet run --project tests\Portly.SmokeTest -c Release        # end-to-end test on test ports
```

Architektur: [docs/02-mimari.md](docs/02-mimari.md), Veröffentlichung: [docs/03-yayinlama.md](docs/03-yayinlama.md) (türkisch).

## Übersetzungen

Die Sprachdateien liegen in [`src/Portly/lang`](src/Portly/lang). Kopiere `en.json`, übersetze die Werte, behalte die `{0}`-Platzhalter bei und sende einen Pull Request. Fehlende Schlüssel fallen auf Englisch zurück. Siehe [CONTRIBUTING.md](CONTRIBUTING.md).

## Datenschutz

Portly hat keine Telemetrie, keine Analysen und keine Konten. Es verbindet sich nur mit anderen Systemen für Dinge, die du startest oder einschaltest:

- **Update-Prüfung** (standardmäßig an, in den Einstellungen abschaltbar): lädt den Paketkatalog und Versionsinformationen aus diesem GitHub-Repository herunter. Nichts über dich oder deine Projekte wird gesendet.
- **Installation eines Pakets**: lädt es von seiner offiziellen Quelle herunter (windows.php.net, dev.mysql.com, nodejs.org ...).
- **Teilen eines Projekts im Internet**: nutzt Cloudflares `cloudflared`; Besucher erreichen deine Site über Cloudflare. Teilen im lokalen Netzwerk bleibt in deinem Netzwerk.

Portly ändert Systemeinstellungen nur mit einer Abfrage: Die hosts-Datei und das Vertrauen in die lokale Zertifizierungsstelle fragen vorher nach Bestätigung (Windows UAC).

**Deinstallation**: Das portable ZIP wird entfernt, indem du seinen Ordner löschst; die Setup-Version wird über Windows-Einstellungen > Apps deinstalliert. Um auch die vertrauenswürdige Zertifizierungsstelle von Portly zu entfernen, öffne `certmgr.msc` > Vertrauenswürdige Stammzertifizierungsstellen > Zertifikate und lösche „Portly Local CA“.

## Richtlinie zur Code-Signierung

Windows-Releases werden von GitHub Actions aus diesem Repository erstellt. Die Code-Signierung über die SignPath Foundation wurde beantragt; bis dahin sind Releases unsigniert, und ihre SHA-256-Prüfsummen werden mit jedem Release veröffentlicht.

Teamrollen:
- Autoren / Committer: [Erdem Software](https://github.com/erdemsoftware)
- Prüfer: [Erdem Software](https://github.com/erdemsoftware)
- Freigebende (Release-Signierung): [Erdem Software](https://github.com/erdemsoftware)

## Spenden

Portly ist kostenlos und bleibt kostenlos: keine Werbung, keine kostenpflichtigen Stufen, kein Tracking. Wenn es dir Zeit spart, kannst du seine Entwicklung unterstützen.

- Karte: **[Buy Me a Coffee](https://buymeacoffee.com/erdemsoftware)**
- Binance Pay ID: **50018297**
- Krypto (USDT auf TRC-20, BEP-20, Polygon, Solana, Ethereum): in der App, Menü > Spenden

---

<sub>Apache, PHP, MySQL, MariaDB, PostgreSQL, MongoDB, Nginx, Node.js, Redis, Memcached, Mailpit, Composer, Adminer, phpMyAdmin, Cloudflare und die übrigen Werkzeuge werden aus ihren offiziellen Quellen heruntergeladen und sind Eigentum ihrer jeweiligen Inhaber. Portly ist nicht mit Laragon verbunden.</sub>
