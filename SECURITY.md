# Security policy

## Supported versions

| Version | Edition | Security fixes |
|---|---|---|
| Latest 1.x release | Modern (Windows 10 / 11) | Yes. Fixes arrive through the in-app updater. |
| Latest 1.x release | Legacy (Windows 7 / 8 / 8.1) | Critical fixes only, as a new Legacy download (no in-app updates). |
| Older releases | both | No. Please update first. |

Portly downloads third-party packages (PHP, Apache, Nginx, MySQL, MariaDB, Node.js ...). Problems in those programs
are reported to their own projects. Portly warns about end-of-life branches and known vulnerable versions of the
packages you have installed (`manifest/security.json`) and offers a one-click update.

## Reporting a vulnerability

Please **do not open a public issue**.

1. Use GitHub's private vulnerability reporting: **Security > Report a vulnerability** on
   <https://github.com/erdemsoftware/portly/security/advisories/new>.
2. Describe the problem, the affected version and edition, and how to reproduce it. A proof of concept helps.

What to expect:

- Acknowledgement within **3 working days**.
- An assessment and a planned fix date within **10 working days**.
- Critical issues (for example remote code execution, a way around the update signature/checksum check, or a
  remote manifest that can change more than it should) are fixed as fast as possible, usually within **14 days**.
- You are credited in the release notes unless you prefer otherwise.

## Scope

In scope: Portly.exe and its installer, the update mechanism (`manifest/*.json`, SHA-256 checks), generated
configuration (php.ini, Apache / Nginx / MySQL configs), the local certificate authority and the dashboard page.

Out of scope: vulnerabilities in the downloaded third-party programs themselves, and attacks that need
administrator access to the computer.

## Design notes

- Every download is checked against the SHA-256 in the catalog when one is published.
- `session.upload_progress.enabled = Off`, `allow_url_include = Off` and `expose_php = Off` are enforced in every
  php.ini. Remote php.ini hotfixes are limited to a built-in whitelist of stricter values.
- Passwords Portly stores (database root password, proxy password) are encrypted with Windows DPAPI.
- Services listen on 127.0.0.1 unless LAN access is switched on.
