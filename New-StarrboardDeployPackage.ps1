param(
    [string] $DotNet = 'dotnet',
    [string] $ProjectPath = (Join-Path $PSScriptRoot 'Starrboard.csproj'),
    [string] $OutputRoot = (Join-Path $PSScriptRoot 'artifacts'),
    [string] $Runtime = 'win-x64',
    [string] $PackageVersion = '',
    [switch] $FrameworkDependent
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $ProjectPath
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$versionSuffix = if ([string]::IsNullOrWhiteSpace($PackageVersion)) { $stamp } else { $PackageVersion.TrimStart('v') }
$packageName = "starrboard-$Runtime-$versionSuffix"
$stageRoot = Join-Path $env:TEMP $packageName
$packageRoot = Join-Path $stageRoot 'Starrboard'
$publishPath = Join-Path $packageRoot 'publish'
$installerPublishPath = Join-Path $stageRoot 'installer-publish'
$zipPath = Join-Path $OutputRoot "$packageName.zip"
$deployTemplate = Join-Path $repo 'appsettings.deploy.template.json'
$installerProject = Join-Path $repo 'installer\StarrboardInstaller.csproj'

$dotNetCommand = if (Test-Path -LiteralPath $DotNet) {
    (Resolve-Path -LiteralPath $DotNet).Path
} else {
    $command = Get-Command $DotNet -ErrorAction SilentlyContinue
    if ($command) {
        $command.Source
    } else {
        throw ".NET SDK was not found at $DotNet"
    }
}

if (-not (Test-Path -LiteralPath $deployTemplate)) {
    throw "Deploy template was not found at $deployTemplate"
}

if (-not (Test-Path -LiteralPath $installerProject)) {
    throw "Installer project was not found at $installerProject"
}

Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $publishPath -Force | Out-Null
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

$publishArgs = @(
    'publish',
    $ProjectPath,
    '-c', 'Release',
    '-r', $Runtime,
    '-o', $publishPath
)

if ($FrameworkDependent) {
    $publishArgs += '--self-contained'
    $publishArgs += 'false'
} else {
    $publishArgs += '--self-contained'
    $publishArgs += 'true'
}

& $dotNetCommand @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

& $dotNetCommand publish $installerProject -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $installerPublishPath
if ($LASTEXITCODE -ne 0) {
    throw "installer publish failed with exit code $LASTEXITCODE"
}

Copy-Item -LiteralPath (Join-Path $installerPublishPath 'Install-Starrboard.exe') -Destination (Join-Path $packageRoot 'Install-Starrboard.exe') -Force

Copy-Item -LiteralPath $deployTemplate -Destination (Join-Path $packageRoot 'appsettings.template.json') -Force
Copy-Item -LiteralPath $deployTemplate -Destination (Join-Path $publishPath 'appsettings.json') -Force
Copy-Item -LiteralPath $deployTemplate -Destination (Join-Path $publishPath 'appsettings.template.json') -Force

foreach ($file in @(
    'Install-StarrboardFromPackage.ps1',
    'Install-StarrboardService.ps1',
    'Ensure-StarrboardService.ps1',
    'Set-StarrboardToken.ps1',
    'Input-StarrboardToken.ps1',
    'Start-StarrboardService.ps1',
    'Open-StarrboardConsole.cmd',
    'Open-StarrboardConsole.ps1',
    'Uninstall-Starrboard.ps1',
    'Uninstall-Starrboard.cmd',
    'Test-Starrboard.ps1'
)) {
    Copy-Item -LiteralPath (Join-Path $repo $file) -Destination (Join-Path $packageRoot $file) -Force
}

Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination (Join-Path $packageRoot 'DEVELOPER-README.md') -Force

$notes = @"
Starrboard deployment package
=============================

Install on the target machine from an elevated PowerShell prompt:

    .\Install-Starrboard.exe

PowerShell fallback:

    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-StarrboardFromPackage.ps1

Then open:

    http://127.0.0.1:8686

The installer does not open a browser by default. Run .\Install-Starrboard.exe --open if you want it to open the console automatically.
Use the Onboarding tab to enter Discord, Arr, qBittorrent, FlareSolverr, and path settings.
Use the onboarding Launch Dependency Setup and Launch Service Account Setup buttons for elevated local setup flows.
Fallback command files are installed to %ProgramData%\Starrboard\Run-StarrboardDependencySetup.cmd and %ProgramData%\Starrboard\Run-StarrboardServiceAccountSetup.cmd.
Run Uninstall-Starrboard.cmd for an ownership-aware clean uninstall. It preserves pre-existing dependency applications and backs up managed configuration by default.
This package intentionally does not include your live runtime config, Discord token, API keys, SQLite database, or logs.
"@

$notes | Set-Content -LiteralPath (Join-Path $packageRoot 'PACKAGE-README.txt') -Encoding UTF8
$notes | Set-Content -LiteralPath (Join-Path $packageRoot 'README.md') -Encoding UTF8

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $zipPath -Force
Remove-Item -LiteralPath $stageRoot -Recurse -Force

Write-Host $zipPath
