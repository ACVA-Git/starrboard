param(
    [string] $InstallRoot = (Join-Path $env:ProgramData 'Starrboard'),
    [switch] $NoStart
)

$ErrorActionPreference = 'Stop'

$packageRoot = $PSScriptRoot
$publishSource = Join-Path $packageRoot 'publish'
$templateSource = Join-Path $packageRoot 'appsettings.template.json'
$publishTarget = Join-Path $InstallRoot 'bin'
$dataRoot = Join-Path $InstallRoot 'data'
$runtimeConfig = Join-Path $dataRoot 'appsettings.runtime.json'
$logsRoot = Join-Path $InstallRoot 'logs'
$legacyRoot = 'C:\Util\arr-discord-bot'
$launcherExe = Join-Path $InstallRoot 'Install-Starrboard.exe'

function Invoke-Native {
    param(
        [string] $FilePath,
        [string[]] $Arguments,
        [int] $SuccessExitCode = 0
    )

    & $FilePath @Arguments | Out-Host
    if ($LASTEXITCODE -gt $SuccessExitCode) {
        throw "$FilePath failed with exit code $LASTEXITCODE"
    }
}

function Register-StarrboardLauncher {
    param([string] $LauncherPath)

    if (-not (Test-Path -LiteralPath $LauncherPath)) {
        Write-Warning "Starrboard launcher executable was not found at $LauncherPath"
        return
    }

    # Use the registry provider rather than reg.exe: Windows PowerShell drops empty-string
    # arguments to native commands, which turned "/d ''" into a hidden overwrite prompt.
    $command = '"' + $LauncherPath + '" --launch "%1"'
    $key = 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Classes\starrboard'
    New-Item -Path "$key\DefaultIcon" -Force | Out-Null
    New-Item -Path "$key\shell\open\command" -Force | Out-Null
    Set-Item -Path $key -Value 'URL:Starrboard Launcher'
    New-ItemProperty -Path $key -Name 'URL Protocol' -Value '' -PropertyType String -Force | Out-Null
    Set-Item -Path "$key\DefaultIcon" -Value "$LauncherPath,0"
    Set-Item -Path "$key\shell\open\command" -Value $command
    Write-Host "Registered starrboard:// launcher: $command"
}

function Write-StarrboardDesktopShortcut {
    param([string] $InstallRoot)

    $desktop = [Environment]::GetFolderPath('CommonDesktopDirectory')
    if ([string]::IsNullOrWhiteSpace($desktop)) {
        $desktop = [Environment]::GetFolderPath('DesktopDirectory')
    }

    if ([string]::IsNullOrWhiteSpace($desktop)) {
        Write-Warning 'Could not determine a desktop path for the Starrboard shortcut.'
        return
    }

    New-Item -ItemType Directory -Path $desktop -Force | Out-Null
    $shortcutPath = Join-Path $desktop 'Starrboard Console.url'
    $iconPath = Join-Path $InstallRoot 'bin\admin\assets\starrboard.ico'
    if (-not (Test-Path -LiteralPath $iconPath)) { $iconPath = Join-Path $InstallRoot 'Install-Starrboard.exe' }
    Set-Content -LiteralPath $shortcutPath -Encoding ASCII -Value @"
[InternetShortcut]
URL=http://127.0.0.1:8686
IconFile=$iconPath
IconIndex=0
"@
    Write-Host "Desktop shortcut: $shortcutPath"
}

function Wait-StarrboardReady {
    $serviceDeadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $serviceDeadline) {
        $service = Get-Service -Name Starrboard -ErrorAction SilentlyContinue
        if ($service -and $service.Status -eq 'Running') {
            break
        }

        Start-Sleep -Seconds 2
    }

    $service = Get-Service -Name Starrboard -ErrorAction SilentlyContinue
    if (-not $service -or $service.Status -ne 'Running') {
        throw "Starrboard service did not report Running. Current status: $($service.Status)"
    }

    $deadline = (Get-Date).AddSeconds(120)
    $lastError = $null
    while ((Get-Date) -lt $deadline) {
        try {
            $ready = Invoke-RestMethod -UseBasicParsing -Uri 'http://127.0.0.1:8686/api/ready' -TimeoutSec 3
            if (-not $ready.ready) { throw 'Starrboard readiness endpoint did not report ready.' }
            return
        } catch {
            $lastError = $_.Exception.Message
            Start-Sleep -Seconds 2
        }
    }

    Write-Warning "Starrboard service is running, but the console API did not answer yet. Last error: $lastError"
    Write-Warning "Open http://127.0.0.1:8686 manually in a minute, or check $(Join-Path $logsRoot 'Starrboard.log') if it stays unavailable."
}

$defaultInstallRoot = Join-Path $env:ProgramData 'Starrboard'
if ($InstallRoot -ne $defaultInstallRoot) {
    throw "Custom install roots are not supported by this release. Use $defaultInstallRoot."
}

if (-not (Test-Path -LiteralPath (Join-Path $publishSource 'Starrboard.exe'))) {
    throw "Package publish folder is missing Starrboard.exe at $publishSource"
}

if (-not (Test-Path -LiteralPath $templateSource)) {
    throw "Package template config was not found at $templateSource"
}

New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
New-Item -ItemType Directory -Path $publishTarget -Force | Out-Null
New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
New-Item -ItemType Directory -Path $logsRoot -Force | Out-Null
$existingInstall = Test-Path -LiteralPath $runtimeConfig

$existingService = Get-Service -Name Starrboard -ErrorAction SilentlyContinue
if ($existingService -and $existingService.Status -ne 'Stopped') {
    Stop-Service -Name Starrboard -Force
    $existingService.WaitForStatus('Stopped', '00:00:30')
}

# Releases before 0.2.0 installed to C:\Util\arr-discord-bot. Carry their settings and request history forward.
$migrated = $false
$legacyData = Join-Path $legacyRoot 'data'
if (-not $existingInstall -and (Test-Path -LiteralPath (Join-Path $legacyData 'appsettings.runtime.json'))) {
    Write-Host "Migrating settings and request history from $legacyData..."
    robocopy $legacyData $dataRoot /E /R:2 /W:2 /NP /NFL /NDL | Out-Null
    if ($LASTEXITCODE -gt 7) {
        throw "Legacy data copy failed with robocopy exit code $LASTEXITCODE"
    }

    $migratedConfig = Get-Content -LiteralPath $runtimeConfig -Raw | ConvertFrom-Json
    if ([string]$migratedConfig.Bot.DatabasePath -ieq 'C:\Util\arr-discord-bot\data\requests.db') {
        $migratedConfig.Bot.DatabasePath = Join-Path $dataRoot 'requests.db'
    }
    if ($migratedConfig.Bot.Logs -and [string]$migratedConfig.Bot.Logs.Path -ieq 'C:\Util\logs\Starrboard.log') {
        $migratedConfig.Bot.Logs.Path = Join-Path $logsRoot 'Starrboard.log'
    }
    $migratedConfig | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $runtimeConfig -Encoding UTF8
    $migrated = $true
}

robocopy $publishSource $publishTarget /MIR | Out-Null
if ($LASTEXITCODE -gt 7) {
    throw "Publish copy failed with robocopy exit code $LASTEXITCODE"
}

foreach ($file in @(
    'Install-Starrboard.exe',
    'Install-StarrboardService.ps1',
    'Ensure-StarrboardService.ps1',
    'Set-StarrboardToken.ps1',
    'Input-StarrboardToken.ps1',
    'Start-StarrboardService.ps1',
    'Open-StarrboardConsole.cmd',
    'Open-StarrboardConsole.ps1',
    'Uninstall-Starrboard.ps1',
    'Uninstall-Starrboard.cmd',
    'Test-Starrboard.ps1',
    'README.md'
)) {
    $source = Join-Path $packageRoot $file
    if (Test-Path -LiteralPath $source) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $InstallRoot $file) -Force
    }
}

Set-Content -LiteralPath (Join-Path $InstallRoot 'Run-StarrboardDependencySetup.cmd') -Encoding ASCII -Value @'
@echo off
start "" starrboard://bootstrap
'@

Set-Content -LiteralPath (Join-Path $InstallRoot 'Run-StarrboardServiceAccountSetup.cmd') -Encoding ASCII -Value @'
@echo off
start "" starrboard://service-account
'@

Copy-Item -LiteralPath $templateSource -Destination (Join-Path $InstallRoot 'appsettings.template.json') -Force
Copy-Item -LiteralPath $templateSource -Destination (Join-Path $publishTarget 'appsettings.json') -Force
Copy-Item -LiteralPath $templateSource -Destination (Join-Path $publishTarget 'appsettings.template.json') -Force

if (-not (Test-Path -LiteralPath $runtimeConfig)) {
    Copy-Item -LiteralPath $templateSource -Destination $runtimeConfig -Force
}

$serviceArguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $InstallRoot 'Install-StarrboardService.ps1'),'-InstallRoot',$InstallRoot)
if (-not $existingInstall -and -not $migrated) { $serviceArguments += '-ResetChildPermissions' }
& powershell.exe @serviceArguments
if ($LASTEXITCODE -ne 0) {
    throw "Install-StarrboardService.ps1 failed with exit code $LASTEXITCODE"
}
& sc.exe config Starrboard start= delayed-auto | Out-Null
Register-StarrboardLauncher -LauncherPath $launcherExe
Write-StarrboardDesktopShortcut -InstallRoot $InstallRoot

if (-not $NoStart) {
    Start-Service -Name Starrboard
    Wait-StarrboardReady
}

Write-Host "Starrboard package installed to $InstallRoot"
if ($migrated) {
    Write-Host "Settings and request history were copied from $legacyRoot. That folder was left in place; delete it after you confirm Starrboard works."
}
Write-Host 'Console: http://127.0.0.1:8686'
Get-Service -Name Starrboard | Select-Object Name, DisplayName, Status, StartType
