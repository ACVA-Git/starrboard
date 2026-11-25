[CmdletBinding()]
param(
    [switch] $KeepDependencies,
    [switch] $PurgeData,
    [switch] $Pause
)

$ErrorActionPreference = 'Continue'
$InstallRoot = Join-Path $env:ProgramData 'Starrboard'
$LegacyInstallRoot = 'C:\Util\arr-discord-bot'
$DataRoot = Join-Path $InstallRoot 'data'
if (-not (Test-Path -LiteralPath $DataRoot) -and (Test-Path -LiteralPath (Join-Path $LegacyInstallRoot 'data'))) {
    $DataRoot = Join-Path $LegacyInstallRoot 'data'
}
$ManifestPath = Join-Path $DataRoot 'dependency-manifest.json'
$BackupRoot = Join-Path $InstallRoot 'backups'
$FlarePath = Join-Path $InstallRoot 'FlareSolverr'
$LegacyFlarePath = 'C:\Util\FlareSolverr'
# Kept on uninstall: qBittorrent holds torrent/resume state, backups holds the uninstall backup and log.
$PreservedChildren = @('qBittorrent', 'backups')
$TranscriptPath = Join-Path $BackupRoot ("Uninstall-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))

New-Item -ItemType Directory -Path $BackupRoot -Force | Out-Null
try { Start-Transcript -LiteralPath $TranscriptPath -Force | Out-Null } catch { }
trap {
    Write-Host ''
    Write-Error "Starrboard uninstall failed: $($_.Exception.Message)"
    Write-Host "Uninstall log: $TranscriptPath"
    try { Stop-Transcript | Out-Null } catch { }
    if ($Pause) { Read-Host 'Press Enter to close' | Out-Null }
    exit 1
}

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Remove-WindowsService([string] $Name) {
    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if (-not $service) { return }
    Write-Host "Removing Windows service $Name"
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $Name -Force -ErrorAction SilentlyContinue
        try { $service.WaitForStatus('Stopped', '00:00:30') } catch { }
    }
    sc.exe delete $Name | Out-Host
}

function Stop-WindowsService([string] $Name) {
    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') {
        Write-Host "Stopping Windows service $Name"
        Stop-Service -Name $Name -Force -ErrorAction SilentlyContinue
        try { $service.WaitForStatus('Stopped', '00:00:30') } catch { }
    }
}

function Clear-StarrboardStaleData {
    Write-Host 'Clearing Starrboard temporary files and stale logs'
    foreach ($pattern in @('StarrboardSetup-*.ps1','StarrboardLauncher-*.ps1','StarrboardRestart-*.ps1','starrboard-win-x64-*')) {
        Get-ChildItem -LiteralPath $env:TEMP -Filter $pattern -Force -ErrorAction SilentlyContinue |
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    }
    foreach ($pattern in @('Starrboard*.log*','FlareSolverr-Starrboard*.log*','Ensure-Starrboard*.log*','Ensure-FlareSolverr*.log*')) {
        Get-ChildItem -LiteralPath 'C:\Util\logs' -Filter $pattern -Force -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath 'Registry::HKEY_CURRENT_USER\SOFTWARE\Classes\starrboard' -Recurse -Force -ErrorAction SilentlyContinue
    foreach ($path in $script:FlareTempDirectories) {
        if ($path -and $path.StartsWith($env:TEMP, [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Stop-FlareSolverrProcessTree {
    if ($null -eq $script:FlareTempDirectories) { $script:FlareTempDirectories = @() }
    for ($attempt = 1; $attempt -le 4; $attempt++) {
        $processes = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
            $_.Name -ieq 'flaresolverr.exe' -or
            $_.ExecutablePath -like "$FlarePath\*" -or
            $_.CommandLine -like "*$FlarePath\*" -or
            $_.ExecutablePath -like "$LegacyFlarePath\*" -or
            $_.CommandLine -like "*$LegacyFlarePath\*"
        }
        if (-not $processes) { break }
        foreach ($process in $processes) {
            $processId = [int]$process.ProcessId
            if ($process.CommandLine -match '--user-data-dir=(?:"([^"]+)"|([^ ]+))') {
                $profilePath = if ($matches[1]) { $matches[1] } else { $matches[2] }
                if ($profilePath -and $profilePath -notin $script:FlareTempDirectories) { $script:FlareTempDirectories += $profilePath }
            }
            cmd.exe /d /c "taskkill /PID $processId /T /F >nul 2>&1"
            Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
        }
        Start-Sleep -Milliseconds 500
    }
}

function Remove-FlareSolverrFiles([string] $Path) {
    Stop-FlareSolverrProcessTree
    for ($attempt = 1; $attempt -le 4; $attempt++) {
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue
        if (-not (Test-Path -LiteralPath $Path)) {
            Write-Host 'Removed Starrboard-managed FlareSolverr files.'
            return
        }
        Start-Sleep -Milliseconds 750
        Stop-FlareSolverrProcessTree
    }
    Write-Warning "FlareSolverr files remain locked at $Path. Restart Windows and run this uninstaller again; ownership detection will still recognize the Starrboard path."
}

function Copy-BackupFile([string] $Source, [string] $RelativeDestination, [string] $StageRoot) {
    if (-not (Test-Path -LiteralPath $Source)) { return }
    $destination = Join-Path $StageRoot $RelativeDestination
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $Source -Destination $destination -Force -ErrorAction Stop
}

function Get-UninstallEntry([string] $DisplayName) {
    $roots = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    Get-ItemProperty $roots -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -like "*$DisplayName*" } |
        Select-Object -First 1
}

function Remove-ManagedPackage([string] $Name, [string] $WingetId) {
    Write-Host "Uninstalling Starrboard-managed dependency: $Name"
    Get-Process -Name $Name -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($winget -and -not [string]::IsNullOrWhiteSpace($WingetId)) {
        & $winget.Source uninstall --id $WingetId --exact --silent --accept-source-agreements
        if ($LASTEXITCODE -eq 0) { return }
        Write-Warning "winget could not uninstall $Name; trying its registered uninstaller."
    }
    $entry = Get-UninstallEntry $Name
    if (-not $entry) {
        Write-Warning "$Name does not have a registered uninstaller."
        return
    }
    $command = [string]$entry.QuietUninstallString
    if ([string]::IsNullOrWhiteSpace($command)) {
        $command = [string]$entry.UninstallString
        # Some installers (qBittorrent's NSIS uninstaller) register no quiet command. Without
        # a silent switch they show a wizard and this uninstall would wait on it forever.
        if ($command -match 'unins\d+\.exe') {
            $command += ' /VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
        } elseif ($command -notmatch '(?i)msiexec' -and $command -notmatch '(^|\s)/S(\s|$)') {
            $command += ' /S'
        }
    }
    if ([string]::IsNullOrWhiteSpace($command)) {
        Write-Warning "$Name has no usable uninstall command."
        return
    }
    $process = Start-Process -FilePath 'cmd.exe' -ArgumentList @('/d','/c',$command) -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(900000)) {
        Write-Warning "$Name uninstaller did not finish within 15 minutes and was stopped. Uninstall $Name manually from Settings > Apps."
        cmd.exe /d /c "taskkill /PID $($process.Id) /T /F >nul 2>&1"
        return
    }
    # NSIS uninstallers copy themselves to %TEMP% and return immediately; wait for that copy too.
    $deadline = (Get-Date).AddMinutes(5)
    while ((Get-Date) -lt $deadline -and (Get-CimInstance Win32_Process -Filter "Name = 'Un.exe' OR Name = 'Au_.exe'" -ErrorAction SilentlyContinue)) {
        Start-Sleep -Seconds 2
    }
    if ($process.ExitCode -ne 0) { Write-Warning "$Name uninstaller returned exit code $($process.ExitCode)." }
}

if (-not (Test-Administrator)) {
    throw 'Run Uninstall-Starrboard.ps1 from an elevated PowerShell prompt, or use Uninstall-Starrboard.cmd.'
}

Write-Host 'Starrboard clean uninstall'
Write-Host ''
$manifest = $null
if (Test-Path -LiteralPath $ManifestPath) {
    try { $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json } catch { Write-Warning 'Dependency ownership manifest could not be read.' }
}

$flarePaths = @($FlarePath, $LegacyFlarePath) | Where-Object {
    Get-ChildItem -LiteralPath $_ -Recurse -Filter 'flaresolverr.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
}
$legacyFlareOwned = @($flarePaths).Count -gt 0

# Stop writers before archiving their configuration. This prevents SQLite/log/AppData lock errors.
Stop-WindowsService 'Starrboard'
Stop-WindowsService 'ArrDiscordBot'
Unregister-ScheduledTask -TaskName 'Starrboard - Boot Recovery' -Confirm:$false -ErrorAction SilentlyContinue
if (-not $KeepDependencies) {
    Unregister-ScheduledTask -TaskName 'Starrboard-FlareSolverr' -Confirm:$false -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName 'Starrboard - Ensure FlareSolverr' -Confirm:$false -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName 'Starrboard-qBittorrent' -Confirm:$false -ErrorAction SilentlyContinue
    Stop-FlareSolverrProcessTree
    if ($manifest -and $manifest.Dependencies) {
        foreach ($name in @('Radarr','Sonarr','Prowlarr')) {
            if ($manifest.Dependencies.$name.InstalledByStarrboard) { Stop-WindowsService $name }
        }
        if ($manifest.Dependencies.qBittorrent.InstalledByStarrboard) {
            Get-Process qbittorrent -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        }
    }
}

if (-not $PurgeData) {
    $stage = Join-Path $env:TEMP ("StarrboardBackup-{0}" -f [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    try {
        foreach ($file in @('appsettings.runtime.json','requests.db','requests.db-wal','requests.db-shm','dependency-manifest.json')) {
            Copy-BackupFile (Join-Path $DataRoot $file) (Join-Path 'Starrboard' $file) $stage
        }
        if ($manifest -and $manifest.Dependencies) {
            foreach ($definition in @(
                @{ Name='Radarr'; Root='C:\ProgramData\Radarr'; Database='radarr.db' },
                @{ Name='Sonarr'; Root='C:\ProgramData\Sonarr'; Database='sonarr.db' },
                @{ Name='Prowlarr'; Root='C:\ProgramData\Prowlarr'; Database='prowlarr.db' }
            )) {
                $dependencyRecord = $manifest.Dependencies.PSObject.Properties[$definition.Name].Value
                if ($dependencyRecord -and $dependencyRecord.InstalledByStarrboard) {
                    Copy-BackupFile (Join-Path $definition.Root 'config.xml') (Join-Path $definition.Name 'config.xml') $stage
                    foreach ($suffix in @('','-wal','-shm')) {
                        $databaseFile = $definition.Database + $suffix
                        Copy-BackupFile (Join-Path $definition.Root $databaseFile) (Join-Path $definition.Name $databaseFile) $stage
                    }
                }
            }
            if ($manifest.Dependencies.qBittorrent.InstalledByStarrboard) {
                Copy-BackupFile (Join-Path $env:APPDATA 'qBittorrent\qBittorrent.ini') 'qBittorrent\qBittorrent.ini' $stage
                $starrboardQbitProfile = Join-Path $env:ProgramData 'Starrboard\qBittorrent\qBittorrent_starrboard\config\qBittorrent.ini'
                Copy-BackupFile $starrboardQbitProfile 'qBittorrent\starrboard-profile\qBittorrent.ini' $stage
            }
        }
        if (Get-ChildItem -LiteralPath $stage -Recurse -File -ErrorAction SilentlyContinue) {
            New-Item -ItemType Directory -Path $BackupRoot -Force | Out-Null
            $backup = Join-Path $BackupRoot ("Starrboard-{0:yyyyMMdd-HHmmss}.zip" -f (Get-Date))
            Write-Host "Backing up essential Starrboard-managed configuration to $backup"
            Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $backup -Force -ErrorAction Stop
        }
    } catch {
        Write-Warning "Configuration backup could not include every essential file: $($_.Exception.Message)"
    } finally {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Remove-WindowsService 'Starrboard'
Remove-WindowsService 'ArrDiscordBot'

Write-Host 'Removing Starrboard protocol, shortcuts, and scheduled tasks'
Remove-Item -LiteralPath 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Classes\starrboard' -Recurse -Force -ErrorAction SilentlyContinue
$desktops = @(
    [Environment]::GetFolderPath('CommonDesktopDirectory'),
    [Environment]::GetFolderPath('DesktopDirectory')
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
foreach ($desktop in $desktops) {
    Remove-Item -LiteralPath (Join-Path $desktop 'Starrboard Console.url') -Force -ErrorAction SilentlyContinue
}

if (-not $KeepDependencies) {
    if (-not $manifest -or -not $manifest.Dependencies) {
        Write-Warning 'No dependency ownership manifest exists. Dependency applications were preserved to avoid removing software Starrboard may not have installed.'
    } else {
        foreach ($name in @('Radarr','Sonarr','Prowlarr','qBittorrent')) {
            $record = $manifest.Dependencies.$name
            if ($record -and $record.InstalledByStarrboard) {
                Remove-ManagedPackage $name ([string]$record.WingetPackageId)
                if ($name -in @('Radarr','Sonarr','Prowlarr')) {
                    Remove-WindowsService $name
                    Remove-Item -LiteralPath (Join-Path 'C:\ProgramData' $name) -Recurse -Force -ErrorAction SilentlyContinue
                }
                if ($name -eq 'qBittorrent') {
                    Write-Host 'Preserving qBittorrent AppData and the Starrboard profile so torrent and resume state are not destroyed.'
                }
            } else {
                Write-Host "Preserving $name because Starrboard did not record ownership."
            }
        }
        $flare = $manifest.Dependencies.FlareSolverr
        if (($flare -and $flare.InstalledByStarrboard) -or $legacyFlareOwned) {
            foreach ($path in $flarePaths) { Remove-FlareSolverrFiles $path }
        }
    }
    if ((-not $manifest -or -not $manifest.Dependencies) -and $legacyFlareOwned) {
        foreach ($path in $flarePaths) { Remove-FlareSolverrFiles $path }
    }
}

Clear-StarrboardStaleData
Set-Location $env:TEMP
if (Test-Path -LiteralPath $InstallRoot) {
    Get-ChildItem -LiteralPath $InstallRoot -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notin $PreservedChildren -and -not ($KeepDependencies -and $_.Name -eq 'FlareSolverr') } |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}
if (Test-Path -LiteralPath $LegacyInstallRoot) {
    Write-Host "A pre-0.2.0 Starrboard folder remains at $LegacyInstallRoot. Delete it manually if you no longer need its data."
}

Write-Host ''
Write-Host 'Starrboard uninstall completed.'
if (-not $PurgeData) { Write-Host "Runtime data backup location: $BackupRoot" }
if ($KeepDependencies) { Write-Host 'Dependency applications were intentionally preserved.' }
Write-Host "Uninstall log: $TranscriptPath"
try { Stop-Transcript | Out-Null } catch { }
if ($Pause) { Read-Host 'Press Enter to close' | Out-Null }
