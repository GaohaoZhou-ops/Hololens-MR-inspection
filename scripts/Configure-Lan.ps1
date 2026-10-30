[CmdletBinding()]
param([string]$Address, [int]$Port = 8765, [switch]$AllowLan)
. "$PSScriptRoot\Common.ps1"
if ($Port -lt 1024 -or $Port -gt 65535) { throw 'Use a port between 1024 and 65535.' }
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($AllowLan -and !$admin) {
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -Port {1} -AllowLan' -f $PSCommandPath, $Port
    if ($Address) {
        $parsedAddress = $null
        if (![Net.IPAddress]::TryParse($Address, [ref]$parsedAddress)) { throw 'Address must be an IPv4 address.' }
        $arguments += ' -Address ' + $Address
    }
    $p = Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments -PassThru
    $handle = $p.Handle
    $p.WaitForExit(); $p.Refresh()
    if ($p.ExitCode -ne 0) { throw "LAN setup failed (exit $($p.ExitCode))." }
    exit 0
}
if (!$Address) {
    $adapter = Get-NetIPConfiguration | Where-Object { $_.IPv4DefaultGateway -and $_.NetAdapter.Status -eq 'Up' -and $_.InterfaceAlias -notlike 'vEthernet*' } | Select-Object -First 1
    if (!$adapter) { throw 'No active LAN adapter found. Run this script with -Address YOUR_PC_IP.' }
    $Address = $adapter.IPv4Address.IPAddress
}
$parsed = $null
if (![Net.IPAddress]::TryParse($Address, [ref]$parsed) -or $parsed.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
    throw 'Address must be an IPv4 address.'
}
$serverFile = Join-Path $DemoRoot 'config\server.local.json'
$token = $null
if (Test-Path $serverFile) { $token = (Get-Content $serverFile -Raw | ConvertFrom-Json).apiToken }
if (!$token) {
    $bytes = New-Object byte[] 32
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    $generator.GetBytes($bytes); $generator.Dispose()
    $token = [Convert]::ToBase64String($bytes)
}
$client = [ordered]@{serverUrl="http://${Address}:$Port";apiToken=$token;distanceMeters=2.0;requestTimeoutSeconds=15;maxLabels=12;maxQueuedCaptures=20;maxQueueMegabytes=64;simulateCamera=$false}
$server = [ordered]@{host='0.0.0.0';port=$Port;apiToken=$token;delaySeconds=1.0;dataDirectory='../server/data'}
$utf8 = New-Object Text.UTF8Encoding $false
[IO.File]::WriteAllText((Join-Path $DemoRoot 'Assets\StreamingAssets\demo-config.json'), ($client | ConvertTo-Json), $utf8)
[IO.File]::WriteAllText($serverFile, ($server | ConvertTo-Json), $utf8)
if ($AllowLan) {
    if (!(Test-Path $Python)) { throw 'The shared embedded Python runtime is missing.' }
    $ruleName = 'HoloLensCaptureDemo-Receiver'
    $rule = Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue
    if ($rule) { Remove-NetFirewallRule -Name $ruleName }
    New-NetFirewallRule -Name $ruleName -DisplayName 'HoloLens Capture Demo receiver' -Group 'HoloLensCaptureDemo' -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -Program $Python -RemoteAddress LocalSubnet -Profile Any | Out-Null
}
[ordered]@{configuredAt=(Get-Date).ToString('o');serverUrl=$client.serverUrl;firewallConfigured=[bool]$AllowLan;firewallScope='LocalSubnet TCP';port=$Port} | ConvertTo-Json | Set-Content (Join-Path $DemoRoot 'logs\lan-setup.json') -Encoding UTF8
Write-Host "Configured receiver: $($client.serverUrl)"
Write-Host 'Rebuild the UWP app after changing the LAN address. Local API token was stored without printing it.'
