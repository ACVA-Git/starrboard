param(
    [string] $InstallRoot = (Join-Path $env:ProgramData 'Starrboard'),
    [string] $BackupRoot = ''
)

$ErrorActionPreference = 'Stop'

# Backups contain the Discord token and API keys. The default location is the
# install's backups folder, which only SYSTEM and Administrators can read.
if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
    $BackupRoot = Join-Path $InstallRoot 'backups'
}

$dataRoot = Join-Path $InstallRoot 'data'
if (-not (Test-Path -LiteralPath $dataRoot)) {
    throw "Starrboard data folder was not found at $dataRoot"
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$destination = Join-Path $BackupRoot "starrboard-$stamp"
New-Item -ItemType Directory -Path $destination -Force | Out-Null

robocopy $dataRoot $destination /E /XD 'config-backups' /R:2 /W:2 /NP /NFL /NDL | Out-Null
if ($LASTEXITCODE -gt 7) {
    throw "robocopy failed with exit code $LASTEXITCODE"
}

Write-Host $destination
