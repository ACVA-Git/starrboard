# Builds Starrboard from this source tree and installs it on this machine,
# using the same deployment package and installer as a GitHub release.
param(
    [string] $DotNet = 'dotnet',
    [switch] $NoOpen
)

$ErrorActionPreference = 'Stop'

$installRoot = Join-Path $env:ProgramData 'Starrboard'
$outputRoot = Join-Path $env:TEMP ('starrboard-source-build-' + (Get-Date -Format yyyyMMddHHmmss))
$extractRoot = Join-Path $outputRoot 'package'

if (Test-Path -LiteralPath (Join-Path $installRoot 'data')) {
    Write-Host 'Creating backup...'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Backup-Starrboard.ps1') -InstallRoot $installRoot | Write-Host
}

try {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'New-StarrboardDeployPackage.ps1') -DotNet $DotNet -OutputRoot $outputRoot -PackageVersion ('source-' + (Get-Date -Format yyyyMMddHHmmss))
    if ($LASTEXITCODE -ne 0) {
        throw "Package build failed with exit code $LASTEXITCODE"
    }

    $zip = Get-ChildItem -LiteralPath $outputRoot -Filter 'starrboard-*.zip' | Select-Object -First 1
    if (-not $zip) {
        throw "No package was produced in $outputRoot"
    }

    Expand-Archive -LiteralPath $zip.FullName -DestinationPath $extractRoot -Force
    $packageInstaller = Get-ChildItem -LiteralPath $extractRoot -Recurse -Filter 'Install-StarrboardFromPackage.ps1' | Select-Object -First 1
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $packageInstaller.FullName
    if ($LASTEXITCODE -ne 0) {
        throw "Install-StarrboardFromPackage.ps1 failed with exit code $LASTEXITCODE"
    }
}
finally {
    Remove-Item -LiteralPath $outputRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if (-not $NoOpen) {
    Start-Process 'http://127.0.0.1:8686'
}
