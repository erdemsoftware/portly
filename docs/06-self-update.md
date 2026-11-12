# Self-update and the one-line installer (Portly 2)

## manifest/app.json

The signed `manifest/app.json` (see `03-yayinlama.md` for signing) describes the latest release. Portly 1.x reads only the
top-level fields; Portly 2 additionally reads the optional `assets` object. Nothing else changes, so one file serves both.

```json
{
  "version": "2.0.1",
  "url": "https://github.com/erdemsoftware/portly/releases/download/v2.0.1/Portly-2.0.1-windows-x64.zip",
  "sha256": "<64 hex digits>",
  "assets": {
    "windows-x64": { "url": "https://.../Portly-2.0.1-windows-x64.zip", "sha256": "...", "size": 41234567 },
    "macos-arm64": { "url": "https://.../Portly-2.0.1-macos-arm64.zip", "sha256": "..." },
    "macos-x64":   { "url": "https://.../Portly-2.0.1-macos-x64.zip",   "sha256": "..." },
    "linux-x64": {
      "tarball":  { "url": "https://.../Portly-2.0.1-linux-x64.tar.gz",  "sha256": "..." },
      "appimage": { "url": "https://.../Portly-2.0.1-linux-x64.AppImage", "sha256": "..." },
      "deb":      { "url": "https://.../Portly-2.0.1-linux-x64.deb",      "sha256": "..." }
    },
    "linux-arm64": { "tarball": { }, "appimage": { }, "deb": { } }
  },
  "notes": { "en": "...", "tr": "..." },
  "donateUrl": ""
}
```

- Keys: `<os>-<arch>` with os `windows | macos | linux` and arch `x64 | arm64`. Windows and macOS have one file per
  platform (`{url, sha256}`); Linux has one per install kind (`tarball`, `appimage`, `deb`).
- `size` is optional (bytes, informational).
- An entry without an `https` address or without a full 64-digit SHA-256 is ignored, so a half-filled manifest offers
  nothing instead of something that cannot be verified. A platform without an entry gets "no download for this system"
  and the UI opens the releases page.
- The top-level `url` / `sha256` stay the **Windows zip**: Portly 1.x installs that, and a Portly 2 Windows build without
  `assets` does too. Keep the zip layout of 1.x (a `Portly` folder or the root holds `Portly.exe`, `lang\`, `templates\`;
  extra files are copied too, `bin data www usr etc tmp logs backup` are never touched) so 1.x can update to 2.x and 2.x
  can update itself with the same zip. With the Windows 2 zip in place, `assets."windows-x64"` is the same file as the top level.
- Security rules are unchanged: the manifest is only used with a valid `.sig`; the version must be newer than the running
  one (no downgrade); only `https` addresses; the downloaded file must match the SHA-256 of the signed manifest, otherwise
  nothing is installed.

## Release file names

`tools/package-unix.sh` writes versioned names and a version-less copy of each file, plus `dist/SHA256SUMS`:

| Versioned | Version-less (for `releases/latest/download/`) |
|---|---|
| `Portly-<v>-macos-<arm64\|x64>.dmg` / `.zip` | `Portly-macos-<arch>.dmg` / `.zip` |
| `Portly-<v>-linux-<x64\|arm64>.tar.gz` / `.deb` / `.AppImage` | `Portly-linux-<arch>.tar.gz` / `.deb` / `.AppImage` |
| `Portly-<v>-windows-<x64\|arm64>.zip` | `Portly-windows-<arch>.zip` |

The release must carry one merged `SHA256SUMS` (the four package jobs each write their own: merge with
`cat */SHA256SUMS | sort -u > SHA256SUMS`, or let `tools/make-app-manifest-v2.ps1` write it, below). The installer verifies
against it.

## Filling the manifest

```powershell
pwsh tools\make-app-manifest-v2.ps1 -Version 2.0.1 -Dist dist -NotesEn "..." -NotesTr "..."
```

It reads `dist\`, writes `dist\app.json` (assets with SHA-256 and size, top level = Windows zip, notes and `donateUrl`
kept from `manifest\app.json`) and a merged `dist\SHA256SUMS`. It does not sign and does not touch `manifest\app.json`:
review the file, copy it over `manifest\app.json`, then sign with `tools\sign-manifests.ps1` as usual.

## How the app updates itself

`Updater.Plan(update)` looks at how this Portly is installed and picks the file:

| Install | Detected by | What is replaced |
|---|---|---|
| Windows | any | `Portly.exe`, `lang`, `templates` (the running exe is renamed to `.old`) |
| macOS | `Portly.app` bundle, folder writable | the zip is unpacked with `ditto` next to the app; bundle identifier, version and `codesign --verify --deep --strict` are checked; the app is swapped (the old one stays as a hidden backup until the next start) |
| Linux AppImage | `APPIMAGE` is set | the new file is downloaded next to the old one, checked, `chmod 755`, renamed over it |
| Linux tar.gz | `Portly` executable in a writable folder (e.g. `~/.local/share/portly`) | the archive is unpacked into a sibling folder and the two folders swap places |
| Linux .deb | `/opt/portly` | `pkexec`: the file is copied to a root-owned folder, its SHA-256 is checked again as root, then `apt-get install` installs it |

When nothing fits (build folder started with `dotnet`, read-only place, `.deb` without `pkexec`, data folder inside the
program folder) `CanSelfUpdate` is false and `SelfUpdateBlockedReason` says why. After a successful install call
`Updater.Restart(...)`: it starts the new program with `--after-update <pid>` (it waits for the old process, see `Program.cs`)
and `Updater.CleanupOldFiles()` (already called at start-up) removes the backups.

## One-line installer

```sh
curl -fsSL https://raw.githubusercontent.com/erdemsoftware/portly/main/tools/install.sh | sh
```

`tools/install.sh` (POSIX sh, no sudo) detects macOS / Linux and x64 / arm64, downloads
`https://github.com/erdemsoftware/portly/releases/latest/download/<version-less file>` and `SHA256SUMS`, verifies the
checksum, then installs:

- macOS: `/Applications/Portly.app` (`~/Applications` when not writable), after `codesign --verify`; removes the quarantine
  flag (`xattr -dr com.apple.quarantine`) because the app is ad-hoc signed, not notarized.
- Linux: `~/.local/share/portly` (the tar.gz), `~/.local/bin/portly` link, `~/.local/share/applications/portly.desktop`
  and the icon.

Environment: `PORTLY_VERSION=2.0.1` (a specific release), `PORTLY_INSTALL_FROM=<dir>` (local files, used by CI),
`PORTLY_APPS_DIR` (macOS target folder), `PORTLY_PREFIX` (Linux prefix). A Linux install made this way is a "tar.gz" install
for the self-update above.
