param(
    [string]$ServiceName = 'Starrboard',
    [string]$HealthUrl = 'http://127.0.0.1:8686/api/status',
    [int]$MaxAttempts = 6,
    [int]$RetrySeconds = 20,
    [string]$LogPath = (Join-Path $env:ProgramData 'Starrboard\logs\Ensure-Starrboard.log')
)

$ErrorActionPreference = 'Stop'

function Write-BootLog {
    param([string]$Message, [string]$Level = 'INFO')

    $directory = Split-Path -Parent $LogPath
    if ($directory -and -not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    Add-Content -LiteralPath $LogPath -Encoding UTF8 -Value ('{0} [{1}] {2}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Level, $Message)
}

function Test-ConsoleHealthy {
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri $HealthUrl -TimeoutSec 10
        return $response.StatusCode -eq 200
    }
    catch {
        return $false
    }
}

for ($attempt = 1; $attempt -le [Math]::Max(1, $MaxAttempts); $attempt++) {
    $service = Get-Service -Name $ServiceName -ErrorAction Stop
    if ($service.Status -ne 'Running') {
        Write-BootLog "Attempt $attempt/${MaxAttempts}: starting $ServiceName (current state: $($service.Status))." 'WARN'
        Start-Service -Name $ServiceName -ErrorAction SilentlyContinue
    }

    Start-Sleep -Seconds 5
    if (Test-ConsoleHealthy) {
        Write-BootLog "$ServiceName is running and its local console is healthy."
        exit 0
    }

    if ($attempt -ge 2) {
        $service = Get-Service -Name $ServiceName -ErrorAction Stop
        if ($service.Status -eq 'Running') {
            Write-BootLog "Attempt $attempt/${MaxAttempts}: $ServiceName is running but its console is unavailable; restarting it." 'WARN'
            Restart-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        }
    }

    if ($attempt -lt $MaxAttempts) {
        Start-Sleep -Seconds ([Math]::Max(5, $RetrySeconds))
    }
}

throw "$ServiceName did not become healthy after $MaxAttempts attempts. Check $LogPath and $(Join-Path (Split-Path -Parent $LogPath) 'Starrboard.log')."
