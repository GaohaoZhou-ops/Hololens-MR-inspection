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
Start-Process $url
Write-Host "Capture monitor: $url"
