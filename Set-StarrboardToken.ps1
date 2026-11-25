param(
    [Parameter(Mandatory = $true)]
    [string] $Token,

    [UInt64] $GuildId = 0
)

$ErrorActionPreference = 'Stop'

$installRoot = Join-Path $env:ProgramData 'Starrboard'
$configPaths = @(Join-Path $installRoot 'data\appsettings.runtime.json')
$templatePath = Join-Path $installRoot 'appsettings.template.json'
$publishedConfigPath = Join-Path $installRoot 'bin\appsettings.json'

foreach ($path in $configPaths) {
    if (-not (Test-Path -LiteralPath $path)) {
        $parent = Split-Path -Parent $path
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        if (Test-Path -LiteralPath $publishedConfigPath) {
            Copy-Item -LiteralPath $publishedConfigPath -Destination $path -Force
        } elseif (Test-Path -LiteralPath $templatePath) {
            Copy-Item -LiteralPath $templatePath -Destination $path -Force
        } else {
            throw "Config file not found at $path and template file not found at $templatePath"
        }
    }

    $config = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $config.Bot.DiscordToken = $Token
    if ($GuildId -ne 0) {
        $config.Bot.GuildId = $GuildId
    }

    $config | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $path -Encoding UTF8
}

Write-Host 'Discord token was written to the runtime config. Token value was not printed.'
if ($GuildId -ne 0) {
    Write-Host "Guild command registration is pinned to guild id $GuildId."
} else {
    Write-Host 'GuildId remains 0, so the bot will register /request globally.'
}
