$ErrorActionPreference = 'Stop'

$serviceName = 'Starrboard'
$configPath = Join-Path $env:ProgramData 'Starrboard\data\appsettings.runtime.json'
$logPath = Join-Path $env:ProgramData 'Starrboard\logs\Starrboard.log'

if (-not (Test-Path -LiteralPath $configPath)) {
    throw "Config file not found at $configPath"
}

$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$token = [string] $config.Bot.DiscordToken

$service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
if (-not $service) {
    throw "$serviceName service is not installed."
}

$result = [ordered]@{
    ServiceState = $service.State
    ServiceStartMode = $service.StartMode
    ServiceStartName = $service.StartName
    ProcessId = $service.ProcessId
    DiscordTokenConfigured = (-not [string]::IsNullOrWhiteSpace($token) -and $token -notlike 'PUT_*')
    DiscordIdentity = $null
    RadarrLookupCount = $null
    RadarrFirstResult = $null
    SonarrLookupCount = $null
    SonarrFirstResult = $null
    RecentReadyLog = $false
    RecentCommandOverwriteLog = $false
    RecentGlobalCommandClearLog = $false
    DatabaseQuickCheck = $null
}

if ($result.DiscordTokenConfigured) {
    try {
        $headers = @{ Authorization = 'Bot ' + $token }
        $me = Invoke-RestMethod -Uri 'https://discord.com/api/v10/users/@me' -Headers $headers -TimeoutSec 20
        $result.DiscordIdentity = "$($me.username) ($($me.id))"
    } catch {
        $result.DiscordIdentity = "Discord identity check failed: $($_.Exception.Message)"
    }
}

$movie = Invoke-RestMethod -Uri ($config.Bot.Radarr.Url + '/api/v3/movie/lookup?term=matrix') -Headers @{ 'X-Api-Key' = $config.Bot.Radarr.ApiKey } -TimeoutSec 20
$series = Invoke-RestMethod -Uri ($config.Bot.Sonarr.Url + '/api/v3/series/lookup?term=breaking%20bad') -Headers @{ 'X-Api-Key' = $config.Bot.Sonarr.ApiKey } -TimeoutSec 20

$result.RadarrLookupCount = $movie.Count
$result.RadarrFirstResult = $movie[0].title
$result.SonarrLookupCount = $series.Count
$result.SonarrFirstResult = $series[0].title

if (Test-Path -LiteralPath $logPath) {
    $recentLog = Get-Content -LiteralPath $logPath -Tail 100
    $result.RecentReadyLog = [bool]($recentLog | Select-String -SimpleMatch '[Discord] Ready')
    $result.RecentCommandOverwriteLog = [bool]($recentLog | Select-String -SimpleMatch 'Overwrote application commands for guild')
    $result.RecentGlobalCommandClearLog = [bool]($recentLog | Select-String -SimpleMatch 'Cleared global application command set')
}

try {
    $python = Get-Command python -ErrorAction SilentlyContinue
    if (-not $python) {
        throw 'python was not found in PATH'
    }

    $dbPath = [string] $config.Bot.DatabasePath
    $escapedDbPath = $dbPath.Replace('\', '\\').Replace("'", "\'")
    $script = "import sqlite3; con=sqlite3.connect('$escapedDbPath'); print(con.execute('PRAGMA quick_check;').fetchone()[0]); con.close()"
    $result.DatabaseQuickCheck = (& $python.Source -c $script)
} catch {
    $result.DatabaseQuickCheck = "check failed: $($_.Exception.Message)"
}

[pscustomobject]$result
