# Security

## Reporting a vulnerability

Please report vulnerabilities privately through this repository's **Security** tab ("Report a vulnerability"). Do not open a public issue for a security problem.

Include the Starrboard version, what you found, and steps to reproduce. Never include real Discord tokens, API keys, or passwords in a report.

## Supported versions

Only the latest release receives fixes.

## Trust model

Starrboard is designed to be run and administered on a single Windows machine that you control.

**Local console**

- The console listens on `http://127.0.0.1:8686` by default and rejects any connection that does not come from a loopback address, even if the listen URL is changed.
- Write requests (POST, PUT, PATCH, DELETE) are rejected when the browser marks them cross-site or when their `Origin`/`Referer` is not a localhost address. This stops ordinary web pages from changing your configuration.
- **The console has no login.** Any program or user that can make HTTP requests from the same machine can read the (redacted) configuration and change settings, including the Discord token and Arr API keys. Do not run Starrboard on a shared machine where you do not trust other local users or software.

**Secrets**

- The Discord token, Arr API keys, and qBittorrent password are stored in plain text in `C:\ProgramData\Starrboard\data\appsettings.runtime.json`. The installer restricts `data` and `backups` to SYSTEM, Administrators, and the service account. Keep these files out of backups you share.
- The console API returns secrets masked as `********`. Saving a masked or blank secret keeps the stored value.
- Service-account passwords entered in the service-account helper are prompted for by PowerShell and are not stored by Starrboard.
- Deployment packages and this repository never contain runtime config, tokens, API keys, the request database, or logs.

**`starrboard://` launcher**

- The installer registers a `starrboard://` URL handler so the console can open the dependency setup, service-account setup, and restart helpers. Only those fixed actions are accepted.
- The setup helpers download their PowerShell from the local console (`127.0.0.1:8686`) and run it elevated after a Windows UAC prompt. Only approve a UAC prompt you started yourself from the Starrboard console.

**Install folder permissions**

- Starrboard's scheduled tasks (boot recovery, FlareSolverr, managed qBittorrent) run as SYSTEM from files under `C:\ProgramData\Starrboard`. The installer therefore removes inherited permissions on that folder so that only SYSTEM and Administrators can create or change files there. Ordinary users get read access, except to `data` and `backups`.

**Service account**

- The `Starrboard` service runs as `NT AUTHORITY\LocalService`, with read/execute access to its binaries and modify access only to its `data` and log folders.

## Out of scope

- Content you download with Radarr, Sonarr, Prowlarr, or qBittorrent, and the security of those applications.
- VPN or network routing for torrent clients. Starrboard does not manage them.
