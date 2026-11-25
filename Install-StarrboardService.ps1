param(
    [string] $InstallRoot = (Join-Path $env:ProgramData 'Starrboard'),
    [switch] $ResetChildPermissions
)

$ErrorActionPreference = 'Stop'

$serviceName = 'Starrboard'
$displayName = 'Starrboard'
$legacyServiceName = 'ArrDiscordBot'
$serviceAccount = 'NT AUTHORITY\LocalService'
$binRoot = Join-Path $InstallRoot 'bin'
$dataRoot = Join-Path $InstallRoot 'data'
$logsRoot = Join-Path $InstallRoot 'logs'
$backupsRoot = Join-Path $InstallRoot 'backups'
$exePath = Join-Path $binRoot 'Starrboard.exe'
$imagePath = '"' + $exePath + '"'

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

if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Published bot executable not found at $exePath"
}

foreach ($directory in @($dataRoot, $logsRoot, $backupsRoot)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

# Every SYSTEM scheduled task Starrboard creates runs files from under the install root,
# so only SYSTEM and Administrators may write there. Secrets in data and backups are
# readable only by SYSTEM, Administrators, and the service account.
$systemSid = '*S-1-5-18'
$administratorsSid = '*S-1-5-32-544'
$usersSid = '*S-1-5-32-545'
$localServiceSid = '*S-1-5-19'
& icacls.exe $InstallRoot /setowner $administratorsSid /T /C /Q | Out-Null
Invoke-Native icacls.exe @($InstallRoot,'/inheritance:r','/grant:r',"${systemSid}:(OI)(CI)F","${administratorsSid}:(OI)(CI)F","${usersSid}:(OI)(CI)RX",'/Q')
if ($ResetChildPermissions) {
    & icacls.exe (Join-Path $InstallRoot '*') /reset /T /C /Q | Out-Null
}
Invoke-Native icacls.exe @($binRoot,'/grant',"${localServiceSid}:(OI)(CI)RX",'/Q')
foreach ($directory in @($dataRoot, $backupsRoot)) {
    Invoke-Native icacls.exe @($directory,'/inheritance:r','/grant:r',"${systemSid}:(OI)(CI)F","${administratorsSid}:(OI)(CI)F",'/Q')
}
Invoke-Native icacls.exe @($dataRoot,'/grant',"${localServiceSid}:(OI)(CI)M",'/Q')
Invoke-Native icacls.exe @($logsRoot,'/grant',"${localServiceSid}:(OI)(CI)M",'/Q')

$legacy = Get-Service -Name $legacyServiceName -ErrorAction SilentlyContinue
if ($legacy) {
    if ($legacy.Status -ne 'Stopped') {
        Stop-Service -Name $legacyServiceName -Force -ErrorAction Stop
    }

    Invoke-Native sc.exe @('delete', $legacyServiceName)
    Start-Sleep -Seconds 2
}

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force -ErrorAction Stop
    }

    Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" -Name ImagePath -Value $imagePath
    Set-Service -Name $serviceName -StartupType Manual
} else {
    New-Service -Name $serviceName -DisplayName $displayName -BinaryPathName $imagePath -StartupType Manual | Out-Null
}

Invoke-Native sc.exe @('config', $serviceName, 'obj=', $serviceAccount)
Invoke-Native sc.exe @('config', $serviceName, 'start=', 'delayed-auto')
Invoke-Native sc.exe @('description', $serviceName, 'Private Discord request board for Radarr and Sonarr.')
Invoke-Native sc.exe @('failure', $serviceName, 'reset=', '86400', 'actions=', 'restart/60000/restart/60000/restart/300000')
Invoke-Native sc.exe @('failureflag', $serviceName, '1')

$bootRecoveryScript = Join-Path $InstallRoot 'Ensure-StarrboardService.ps1'
if (Test-Path -LiteralPath $bootRecoveryScript) {
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$bootRecoveryScript`""
    $trigger = New-ScheduledTaskTrigger -AtStartup
    $trigger.Delay = 'PT3M'
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 15) -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -StartWhenAvailable
    Register-ScheduledTask -TaskName 'Starrboard - Boot Recovery' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Verifies Starrboard after boot and retries its local console when automatic service startup fails.' -Force | Out-Null
}

Write-Host "$displayName service is installed/updated."
Write-Host 'Package installs start the local console immediately. The Discord worker waits until a bot token is configured.'
