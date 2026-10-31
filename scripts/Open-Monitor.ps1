param([ValidateSet('monitor','system-live')][string]$View='monitor', [string]$DeviceIp)
. "$PSScriptRoot\Common.ps1"
$config = Get-DemoConfig
$url = 'http://127.0.0.1:{0}/' -f ([uri]$config.serverUrl).Port
try { $health = Invoke-RestMethod ($url + 'health') -TimeoutSec 2 }
catch [System.Net.WebException] {
    $p = Start-Process powershell.exe -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -File "{0}"' -f (Join-Path $PSScriptRoot 'Start-Server.ps1')) -PassThru
    $handle = $p.Handle
    if (!$p.WaitForExit(15000)) { throw 'The receiver is still starting. Check logs\server-errors.log.' }
    $health = Invoke-RestMethod ($url + 'health') -TimeoutSec 2
}
if (!$health.PSObject.Properties['dashboardAvailable'] -or !$health.dashboardAvailable) {
    throw 'An older receiver is running. Run StopServer.cmd, then OpenMonitor.cmd again.'
}
if ($View -eq 'system-live') {
    $url += 'system-live'
    if ($DeviceIp) {
        $ip = $null
        if (![Net.IPAddress]::TryParse($DeviceIp, [ref]$ip) -or $ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) { throw 'Supply the HoloLens IPv4 address with -DeviceIp.' }
        $url += '?device=' + [Uri]::EscapeDataString($ip.ToString())
    }
    try { $null = Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 2 }
    catch { throw 'The system-live page is unavailable. Restart the receiver with StopServer.cmd, then OpenSystemLive.cmd.' }
}
Start-Process $url
Write-Host "Capture monitor: $url"
