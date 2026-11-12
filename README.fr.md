<p align="center"><img src="assets/portly-256.png" width="96" alt="Portly"></p>

<h1 align="center">Portly</h1>
<p align="center">
  Environnement de développement web local rapide, portable et open source pour Windows<br>
  <sub>par <a href="https://erdemsoftware.com">Erdem Software</a> · MIT · sans publicité ni traçage</sub>
</p>

<p align="center"><a href="README.md">English</a> · <a href="README.tr.md">Türkçe</a> · <a href="README.zh-CN.md">简体中文</a> · <a href="README.es.md">Español</a> · <a href="README.pt-BR.md">Português (BR)</a> · <a href="README.de.md">Deutsch</a> · <b>Français</b> · <a href="README.ru.md">Русский</a> · <a href="README.ja.md">日本語</a> · <a href="README.ko.md">한국어</a> · <a href="README.ar.md">العربية</a> · <a href="README.id.md">Bahasa Indonesia</a></p>
<p align="center"><sub>L'application elle-même est disponible en 31 langues (arabe, persan et ourdou, écrits de droite à gauche).</sub></p>

---

<p align="center"><img src="assets/screenshots/en-services.png" width="49%" alt="Services"> <img src="assets/screenshots/en-projects.png" width="49%" alt="Projets"></p>
<p align="center"><img src="assets/screenshots/en-qr.png" width="49%" alt="Ouvrir un projet sur votre téléphone avec un code QR"> <img src="assets/screenshots/en-newproject.png" width="40%" alt="Nouveau projet"></p>

Portly exécute Apache ou Nginx, PHP, MySQL ou MariaDB, PostgreSQL, MongoDB, Redis, Memcached, Node.js, Mailpit, Composer, Adminer et phpMyAdmin à partir d'un seul dossier. Aucun programme d'installation n'est nécessaire et rien n'est installé dans Windows par défaut : pas de services Windows (un mode service facultatif, désactivé sauf si vous l'activez dans Paramètres > Fonctionnalités, peut exécuter la pile en tant que service), pas d'encombrement du registre. Il fonctionne même depuis une clé USB.

## Pourquoi Portly ?

| | Laragon | **Portly** |
|---|---|---|
| Licence | Propriétaire, payant pour un usage commercial | **MIT, totalement gratuit, sans publicité** |
| Mises à jour de l'application | Télécharger un nouvel installateur | **Un clic dans l'application** (vérifié par SHA-256) |
| Liste des paquets | `packages.conf` local | **Catalogue mis à jour à distance** + versions PHP en direct depuis windows.php.net |
| Version de PHP par site | Non | **Oui** (chaque version de PHP sur son propre port FastCGI) |
| HTTPS | Un certificat, recréé pour chaque projet | **Autorité racine locale** approuvée une seule fois, un certificat par site |
| Sécurité réseau | Écoute sur toutes les interfaces | **127.0.0.1 par défaut** (pas de demande du pare-feu) |
| Durcissement de PHP | Valeurs par défaut | `session.upload_progress` et `allow_url_include` **désactivés** |
| Sécurité en cas de plantage | — | Si Portly s'arrête, Windows arrête les serveurs qu'il a démarrés (objet de travail) |
| Conflits de ports | Message d'erreur | Affiche le programme qui occupe le port ; **passez à un port libre en un clic** ou fermez ce programme |
| Partage | ngrok (compte nécessaire) | **Réseau local** (téléphone / tablette sur le même Wi-Fi, code QR, https, mot de passe) et **Internet** via Cloudflare : adresse aléatoire sans compte, ou une **adresse fixe sur votre propre domaine** |
| Migration | — | Projets et bases de données depuis **Laragon, XAMPP, WampServer, AMPPS, UwAmp, Uniform Server, Open Server, EasyPHP, MAMP** (projets depuis Herd et Local) |
| Bases de données | MySQL | **MySQL ou MariaDB, PostgreSQL, MongoDB, SQLite** ; Redis et Memcached |
| Outils de base de données | HeidiSQL | **phpMyAdmin** (s'ouvre connecté tant que root n'a pas de mot de passe), **Adminer, HeidiSQL, DBeaver, Beekeeper Studio, DbGate, DB Browser for SQLite** |
| WordPress | Manuel | **WordPress en un clic** (WP-CLI, utilisateur administrateur prêt) |
| Langues | 34 (txt) | **31**, JSON simple, ouvert aux contributions |
| Windows 7 / 8 / 8.1 | Incertain | **Édition Legacy séparée** |

## Fonctionnalités

- **Projets** : chaque dossier dans `www` devient `https://name.portly` ; les dossiers `public` de type Laravel sont détectés. Regroupement de dossiers et sous-projets (`blog.shop.portly`), modèles pour Laravel, Symfony, CodeIgniter, CakePHP, Drupal, Vue, React, Svelte, Nuxt, Next.js, Angular et Astro. Chaque carte indique avec quoi le projet est construit et sa base de données.
- **Partager un projet**
  - *Réseau local* : ouvrez-le sur votre téléphone avec un code QR. Fonctionne en https ; un guide sur le téléphone installe le certificat de Portly sur iPhone et Android une seule fois. Mot de passe facultatif, limite de temps, démarrage avec Portly, liste des appareils connectés ; les erreurs PHP provenant du téléphone apparaissent dans le journal de Portly.
  - *Internet* : un tunnel rapide Cloudflare gratuit (sans compte), ou votre propre domaine, par exemple `demo.example.com`, via votre compte Cloudflare. Même mot de passe, limite de temps et liste des appareils.
- **Version de PHP par site**, **extensions PHP** en un clic (17 extensions PECL, vérifiées pour chaque version de PHP), **Xdebug**, terminaux qui suivent la version de PHP / Node du projet (`.php-version`, `composer.json`, `.nvmrc`).
- **Services** : Tout démarrer / Tout arrêter avec un cadenas par service (choisissez les services qui font partie du groupe), MySQL / MariaDB dans une seule carte, Tomcat + Java en option.
- **Avis de mise à jour** (« PHP 8.6 est disponible » → installer et basculer en un clic), avertissements de fin de vie et de CVE.
- **Sécurité des bases de données** : vérification et réparation, déplacements sûrs entre les versions de MySQL / MariaDB, import SQL volumineux, sauvegardes automatiques, réinitialisation du mot de passe root oublié, mode de récupération InnoDB.
- **Flux de travail du développeur** : tâches en arrière-plan (file d'attente, planificateur, Vite), journaux du site, profileur, CLI `portly` avec hooks, fichier projet `portly.yml`, envoi / récupération via SSH.
- **Sites** : domaines supplémentaires et génériques (wildcard), vos propres certificats, archivage, Corriger SSL, approbation des certificats dans Firefox / Java, **Caddy** et **FrankenPHP** (Octane).
- **Prévention des problèmes** : dossiers OneDrive, chemins non-ASCII et longs, un second PHP ou PHPRC, antivirus bloquant le fichier hosts, vérification de la quarantaine.
- Interface latérale moderne avec thème clair / sombre qui suit Windows, DPI par écran et prise en charge des lecteurs d'écran. Les fonctionnalités rarement utilisées s'activent dans Paramètres > Fonctionnalités.

## Éditions

| Téléchargement | Windows | Mises à jour | Paquets |
|---|---|---|---|
| `Portly-x.y.z-win10-11.zip` | 10 / 11 (64 bits) | **Oui** : application + catalogue de paquets | Dernières : PHP 8.5, MySQL 8.4 LTS / 26.x, Apache 2.4, Node 24 LTS ... |
| `Portly-x.y.z-win7-8.zip` (Legacy) | 7 SP1 / 8 / 8.1 (64 bits) | **Non** (catalogue figé) | Dernières versions fonctionnant sous Windows 7 : PHP 8.2, MySQL 5.7, Apache 2.4 VS16, Node 13 |
| macOS / Linux | — | — | **Bêta** : voir [Portly 2 bêta](#portly-2-beta) plus bas. |

L'édition Legacy nécessite .NET Framework 4.8 (déjà inclus dans Windows 10 / 11).

<a id="portly-2-beta"></a>

## Portly 2 bêta : macOS, Linux et une nouvelle application Windows

Portly 2 fonctionne sur **macOS (Apple Silicon et Intel), Linux (x64 et arm64) et Windows 10 / 11**, avec le même numéro de version partout. Il est en bêta : Portly 1.x reste la version stable et ne passe pas automatiquement à celle-ci.

- **macOS / Linux**, une seule ligne, sans sudo (SHA-256 vérifié ; macOS : `/Applications/Portly.app`, Linux : `~/.local/share/portly`) :

  ```sh
  curl -fsSL https://raw.githubusercontent.com/erdemsoftware/portly/main/tools/install.sh | sh
  ```

  Ou téléchargez le `.dmg`, `.AppImage`, `.deb` ou `.tar.gz` depuis [Releases](https://github.com/erdemsoftware/portly/releases).
- **Windows 10 / 11** : `Portly-2.x.y-beta.n-windows-x64.zip` depuis la même page, décompressé dans un nouveau dossier.
- Sur macOS / Linux : PHP avec Xdebug, Caddy, Apache, Nginx, MySQL, MariaDB, PostgreSQL, MongoDB, Redis, Memcached, Mailpit et Node.js dans `~/Portly` ; les projets s'ouvrent sur `https://name.localhost`.

## Démarrage rapide

1. Téléchargez un zip depuis [Releases](https://github.com/erdemsoftware/portly/releases) et décompressez-le dans un dossier **sans espaces**, par exemple `C:\Portly`.
2. Lancez `Portly.exe`. Les paquets recommandés sont présélectionnés au premier démarrage : cliquez sur **Installer la sélection**.
3. Cliquez sur **Tout démarrer**.
4. Créez un dossier dans `www` : `www\shop` devient `https://shop.portly`. Un dossier nommé comme un domaine (`www\example.com`) s'ouvre sous `https://example.com`, ainsi les URL restent identiques lors de la mise en ligne ; un commutateur Local / Live sur sa carte rétablit le site en ligne sur votre PC, tandis que la copie locale continue de fonctionner sur `https://example.com.portly`.

`http://localhost` ouvre un tableau de bord avec tous les sites, Adminer, Mailpit et `phpinfo()`. Plus de détails dans [docs/04-kullanim.md](docs/04-kullanim.md) (en turc).

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

Pour modifier un modèle de configuration, copiez-le depuis `templates\` vers `usr\templates\` et modifiez la copie ; les mises à jour ne le toucheront pas.

## Compiler depuis les sources

Prérequis : n'importe quel .NET SDK 6 ou plus récent (l'application cible .NET Framework 4.8, elle fonctionne donc sous Windows 7 SP1 – 11 sans runtimes supplémentaires). Si Inno Setup est installé, `build.ps1` produit aussi les programmes d'installation.

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1            # both editions -> dist\
dotnet build src\Portly\Portly.csproj -c Release              # Windows 10/11 edition only
dotnet build src\Portly\Portly.csproj -c Release -p:Edition=Legacy
dotnet run --project tests\Portly.SmokeTest -c Release        # end-to-end test on test ports
```

Architecture : [docs/02-mimari.md](docs/02-mimari.md), publication : [docs/03-yayinlama.md](docs/03-yayinlama.md) (en turc).

## Traductions

Les fichiers de langue se trouvent dans [`src/Portly/lang`](src/Portly/lang). Copiez `en.json`, traduisez les valeurs, conservez les marqueurs `{0}` et envoyez une pull request. Les clés manquantes reprennent l'anglais. Voir [CONTRIBUTING.md](CONTRIBUTING.md).

## Confidentialité

Portly n'a ni télémétrie, ni analyses, ni comptes. Il ne se connecte à d'autres systèmes que pour les actions que vous lancez ou activez :

- **Vérification des mises à jour** (activée par défaut, désactivable dans les Paramètres) : télécharge le catalogue de paquets et les informations de version depuis ce dépôt GitHub. Aucune information sur vous ou vos projets n'est envoyée.
- **Installation d'un paquet** : téléchargement depuis sa source officielle (windows.php.net, dev.mysql.com, nodejs.org ...).
- **Partage d'un projet sur Internet** : exécute `cloudflared` de Cloudflare ; les visiteurs accèdent à votre site via Cloudflare. Le partage sur le réseau local reste dans votre réseau.

Portly ne modifie les paramètres système qu'après confirmation : le fichier hosts et l'approbation de son autorité de certification locale demandent une confirmation (UAC de Windows).

**Désinstallation** : le zip portable se supprime en effaçant son dossier ; la version installateur se désinstalle depuis Paramètres Windows > Applications. Pour retirer aussi l'autorité de certification Portly approuvée, ouvrez `certmgr.msc` > Autorités de certification racines de confiance > Certificats et supprimez « Portly Local CA ».

## Politique de signature de code

Les versions Windows sont compilées à partir de ce dépôt par GitHub Actions. La signature de code via SignPath Foundation est en cours de demande ; d'ici là, les versions ne sont pas signées et leurs sommes SHA-256 sont publiées avec chaque version.

Rôles de l'équipe :
- Auteurs / committers : [Erdem Software](https://github.com/erdemsoftware)
- Relecteurs : [Erdem Software](https://github.com/erdemsoftware)
- Approbateurs (signature des versions) : [Erdem Software](https://github.com/erdemsoftware)

## Faire un don

Portly est gratuit et le restera : pas de publicité, pas d'offres payantes, pas de traçage. Si l'application vous fait gagner du temps, vous pouvez soutenir son développement.

- Carte : **[Buy Me a Coffee](https://buymeacoffee.com/erdemsoftware)**
- Identifiant Binance Pay : **50018297**
- Crypto (USDT sur TRC-20, BEP-20, Polygon, Solana, Ethereum) : dans l'application, Menu > Don

---

<sub>Apache, PHP, MySQL, MariaDB, PostgreSQL, MongoDB, Nginx, Node.js, Redis, Memcached, Mailpit, Composer, Adminer, phpMyAdmin, Cloudflare et les autres outils sont téléchargés depuis leurs sources officielles et appartiennent à leurs propriétaires respectifs. Portly n'est pas affilié à Laragon.</sub>
