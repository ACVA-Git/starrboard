# Starrboard

A self-hosted Discord request board for [Radarr](https://radarr.video/) and [Sonarr](https://sonarr.tv/), packaged as a Windows service with a local setup console.

Members of your Discord server request movies and TV shows with a slash command. Starrboard adds them to Radarr or Sonarr, tracks who asked for what, and sends the requester a direct message when the media has been imported.

> **Legal use only.** Starrboard is request and automation glue for software you run yourself. It does not host, index, or distribute media. You are responsible for making sure that anything you acquire through Radarr, Sonarr, and their download clients is content you are legally entitled to.

## Features

- `/request movie title:<name>` and `/request tv title:<name>` slash commands.
- Private (ephemeral) request flow, so only the requester sees search results and confirmations.
- Search results come from the Radarr and Sonarr lookup APIs, with a select menu when there are multiple matches.
- Request previews show the poster, synopsis, and useful details (release or air date, runtime, rating, certification, studio or network, status, genres).
- **Per-season TV requests.** For shows with more than one season, the requester picks the seasons they want, including `Specials` (Season 0). Only those seasons are monitored and searched.
- Requests for shows that already exist in Sonarr update the existing series instead of resetting its monitoring state.
- After a TV request, Starrboard summarizes each selected season as already available, downloading, or still searching.
- Request ownership is stored in SQLite (WAL mode). Imports are checked every 5 minutes by default.
- **Completion DMs.** The requester gets a DM with the poster and details once the movie, the selected seasons, or the aired episodes of a whole-show request are fully imported. Specials and future episodes do not hold up a whole-show notification.
- Request sessions expire, final submit buttons are single-use, and DM delivery retries per request so one failure does not block others.
- Overwrites the bot's application command set on startup, so stale commands from a previous bot on the same token are removed.

## Local console

Starrboard includes an operator console at `http://127.0.0.1:8686` (localhost only). It provides:

- Health checks for Starrboard, Discord, Radarr, Sonarr, Prowlarr, FlareSolverr, qBittorrent, the SQLite database, and the log directory.
- Configuration for Discord, the Arr apps, Prowlarr, FlareSolverr, qBittorrent, Arr service accounts, and logging. Stored secrets are redacted when read back; leaving a secret field blank keeps the existing value.
- **A first-run onboarding wizard** that:
  - detects missing or misconfigured dependencies and links to official downloads,
  - can install dependencies (winget first, then official installers) through an elevated, Starrboard-owned setup dialog,
  - discovers local Arr API keys, tests each connection, and loads quality profiles,
  - creates missing Radarr/Sonarr root folders, Prowlarr application links, and qBittorrent download clients without overwriting existing entries,
  - validates read/write access to your media root folders,
  - runs a non-mutating end-to-end dry test,
  - refuses to mark setup complete until required checks pass (enforced by the server, not just the UI).
- A Setup Guide tab for beginners: finding API keys, enabling the qBittorrent Web UI, wiring Prowlarr indexers, FlareSolverr, Plex-friendly folder layouts, and running Arr services under an account that can reach network shares.

Restart the `Starrboard` service after saving config changes so it reloads them.

### Managed qBittorrent

When Starrboard installs qBittorrent itself, it:

- uses a dedicated profile at `C:\ProgramData\Starrboard\qBittorrent`, separate from any interactive user profile,
- enables the Web UI on `http://127.0.0.1:18080` (port 8080 is left alone for any existing qBittorrent),
- generates a random `admin` password through qBittorrent's own API, verifies it, saves it, and shows it to you once,
- creates the default downloads folder and grants the Radarr/Sonarr service accounts access to it.

Credentials for a qBittorrent you installed yourself are never reset.

Starrboard recommends routing torrent traffic through a VPN or other protected network path, but it does not install, configure, or check VPN routing.

## Requirements

- Windows 10/11 or Windows Server (x64). The release package is self-contained, so no .NET runtime install is needed.
- A Discord application and bot token ([Discord developer portal](https://discord.com/developers/applications)).
- Radarr and Sonarr (the onboarding wizard can install these, plus Prowlarr, qBittorrent, and FlareSolverr).

## Install

1. Download the ZIP attached to the latest [GitHub release](../../releases). GitHub's "Source code" ZIPs are for development only and do not contain the built service or installer.
2. Extract it and run `Install-Starrboard.exe`. It elevates itself, installs and starts the `Starrboard` Windows service, registers the `starrboard://` launcher, and creates a **Starrboard Console** desktop shortcut. Pass `--open` to open the console when it finishes.
3. Open `http://127.0.0.1:8686` and follow the Onboarding tab.

PowerShell fallback:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-StarrboardFromPackage.ps1
```

`Install-Starrboard.exe` is not code-signed, so Windows SmartScreen may warn you the first time you run it. You can build the package yourself from source (see [Building](#building)) if you prefer.

### Install location

Everything lives under `C:\ProgramData\Starrboard`, the same layout Radarr and Sonarr use:

| Item | Path |
| --- | --- |
| Install root (scripts, uninstaller) | `C:\ProgramData\Starrboard` |
| Service binary | `C:\ProgramData\Starrboard\bin\Starrboard.exe` |
| Runtime config | `C:\ProgramData\Starrboard\data\appsettings.runtime.json` |
| Request database | `C:\ProgramData\Starrboard\data\requests.db` |
| Logs | `C:\ProgramData\Starrboard\logs` (`Starrboard.log` rotates at 10 MB, five archives kept) |
| Managed FlareSolverr | `C:\ProgramData\Starrboard\FlareSolverr` |
| Managed qBittorrent profile | `C:\ProgramData\Starrboard\qBittorrent` |
| Backups | `C:\ProgramData\Starrboard\backups` |
| Windows service | `Starrboard`, running as `NT AUTHORITY\LocalService` |

The installer locks this folder down: only Administrators and SYSTEM can change files in it, and only they and the service can read `data` and `backups`. Custom install locations are not supported.

### Upgrading from 0.1.x

Releases before 0.2.0 installed to `C:\Util\arr-discord-bot`. Running the new installer on such a machine copies your settings, request history, and dependency manifest into `C:\ProgramData\Starrboard\data` and points the service at the new location. The old folder is left in place so you can roll back; delete it once you have confirmed the new install works. A FlareSolverr previously installed to `C:\Util\FlareSolverr` keeps running from there until you run dependency setup again.

### Discord token from the command line

The console is the easiest way to set the token. To set it from PowerShell instead (the token is prompted for securely, not echoed, and not passed on the command line):

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\ProgramData\Starrboard\Input-StarrboardToken.ps1 -StartService
```

Setting `GuildId` to your server's id registers commands for that server immediately. Leaving it at `0` registers them globally, which can take longer to appear.

### Media folders

Set the Arr **root** folders only, for example `D:\Plex\Movies` or `\\nas\media\TV Shows`. Radarr and Sonarr create the title and season folders below them. Do not point a root at a specific movie, show, or season folder.

Windows services cannot see mapped drive letters. Use UNC paths for network shares and run the Arr services under an account that can reach them (the onboarding wizard has a service-account helper for this).

### Migrating from another Discord request bot

If you are replacing an existing request bot, stop it before starting Starrboard. Do not run two bots with the same Discord token at the same time. Starrboard replaces the bot's registered slash commands on startup.

## Uninstall

Run `Uninstall-Starrboard.cmd` from `C:\ProgramData\Starrboard`. It elevates, backs up Starrboard's configuration and the Arr configuration to `C:\ProgramData\Starrboard\backups`, removes Starrboard's services, launcher, shortcuts, tasks, and files, and uninstalls only the dependencies Starrboard itself installed. Dependencies you installed yourself are left alone. The `backups` and `qBittorrent` folders are kept so your backup and torrent state survive.

- `Uninstall-Starrboard.ps1 -KeepDependencies` keeps all dependency applications.
- `Uninstall-Starrboard.ps1 -PurgeData` skips the backup.

## Building

Requires the .NET 8 SDK.

```powershell
dotnet build .\Starrboard.csproj -c Release
```

Build the full deployment ZIP (service, installer, scripts, clean config template) into `.\artifacts`:

```powershell
.\New-StarrboardDeployPackage.ps1 -PackageVersion v0.0.0-dev
```

Build from source and install on this machine in one step (from an elevated PowerShell prompt):

```powershell
.\Install-StarrboardPackage.ps1
```

The package never includes runtime config, tokens, API keys, the request database, or logs.

Pushing a `v*` tag runs the release workflow, which builds the same ZIP and attaches it to a GitHub release.

### Testing a package

Test packages on a clean Windows machine or VM, not on a machine that already runs your media stack. Existing services, scheduled tasks, and configuration can hide installer bugs.

## Security

The console is meant to be used only from the machine running Starrboard. See [SECURITY.md](SECURITY.md) for the trust model and how to report a vulnerability.

## License

[MIT](LICENSE)

Starrboard is not affiliated with Discord, Plex, or the Radarr, Sonarr, Prowlarr, qBittorrent, or FlareSolverr projects.
