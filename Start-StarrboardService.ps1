$ErrorActionPreference = 'Stop'

$serviceName = 'Starrboard'
$configPath = Join-Path $env:ProgramData 'Starrboard\data\appsettings.runtime.json'

if (-not (Test-Path -LiteralPath $configPath)) {
    throw "Runtime config file not found at $configPath"
}

$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$token = [string] $config.Bot.DiscordToken
if ([string]::IsNullOrWhiteSpace($token) -or $token -like 'PUT_*') {
    throw 'Discord token is still a placeholder. Run Set-StarrboardToken.ps1 first.'
}

& sc.exe config $serviceName start= delayed-auto | Out-Null
Restart-Service -Name $serviceName -Force
& sc.exe failure $serviceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null
& sc.exe failureflag $serviceName 1 | Out-Null

Start-Sleep -Seconds 3
Get-Service -Name $serviceName | Select-Object Name, DisplayName, Status, StartType
