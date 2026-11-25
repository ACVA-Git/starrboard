param(
    [UInt64] $GuildId = 0,
    [switch] $StartService
)

$ErrorActionPreference = 'Stop'

$tokenSecure = Read-Host 'Paste the new Discord bot token' -AsSecureString
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($tokenSecure)

try {
    $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
    if ([string]::IsNullOrWhiteSpace($token)) {
        throw 'Token was empty.'
    }

    if ($GuildId -ne 0) {
        & "$PSScriptRoot\Set-StarrboardToken.ps1" -Token $token -GuildId $GuildId
    } else {
        & "$PSScriptRoot\Set-StarrboardToken.ps1" -Token $token
    }
} finally {
    if ($bstr -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
    }

    Remove-Variable token -ErrorAction SilentlyContinue
}

if ($StartService) {
    & "$PSScriptRoot\Start-StarrboardService.ps1"
} else {
    Write-Host 'Token stored. Run Start-StarrboardService.ps1 when ready to enable and start the service.'
}
