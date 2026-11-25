param(
    [string] $Url = 'http://127.0.0.1:8686'
)

$ErrorActionPreference = 'Stop'

$profileRoot = Join-Path $env:LOCALAPPDATA 'Starrboard\ConsoleBrowser'
$browserNames = @('msedge.exe', 'chrome.exe')

try {
    $existing = Get-CimInstance Win32_Process |
        Where-Object {
            $_.Name -in $browserNames -and
            $_.CommandLine -like "*$profileRoot*" -and
            $_.CommandLine -like "*$Url*"
        } |
        Select-Object -First 1

    if ($existing) {
        Write-Host 'Starrboard console is already open.'
        exit 0
    }
} catch {
    Write-Warning "Could not check existing browser windows: $($_.Exception.Message)"
}

$candidateBrowsers = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'),
    (Join-Path $env:ProgramFiles 'Microsoft\Edge\Application\msedge.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Google\Chrome\Application\chrome.exe'),
    (Join-Path $env:ProgramFiles 'Google\Chrome\Application\chrome.exe')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }

$browser = $candidateBrowsers | Select-Object -First 1
if ($browser) {
    New-Item -ItemType Directory -Path $profileRoot -Force | Out-Null
    $arguments = @(
        "--app=$Url",
        "--user-data-dir=""$profileRoot""",
        '--no-first-run',
        '--no-default-browser-check',
        '--disable-session-crashed-bubble'
    )
    Start-Process -FilePath $browser -ArgumentList $arguments
    exit 0
}

Start-Process $Url
