[CmdletBinding()]
param()
. "$PSScriptRoot\Common.ps1"
$config = Get-DemoConfig
$serverFile = Join-Path $DemoRoot 'config\server.local.json'
if (!(Test-Path $serverFile) -or !(Test-Path $Python)) { throw 'Run Configure-Lan.cmd; verify the embedded Python runtime is installed.' }
$port = ([uri]$config.serverUrl).Port
$url = "http://127.0.0.1:$port/health"
try {
    $health = Invoke-RestMethod -Uri $url -TimeoutSec 2
    if ($health.service -eq 'HoloLensCaptureDemo') { Write-Host "Receiver already running: $($config.serverUrl)"; Write-Host "Browser monitor: http://127.0.0.1:$port/"; exit 0 }
    throw "Port $port is occupied by another service."
} catch [System.Net.WebException] { }
$arguments = '-u "{0}" --config "{1}"' -f (Join-Path $DemoRoot 'server\server.py'), $serverFile
$p = Start-Process -FilePath $Python -ArgumentList $arguments -WorkingDirectory $DemoRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $DemoRoot 'logs\server.log') -RedirectStandardError (Join-Path $DemoRoot 'logs\server-errors.log')
[ordered]@{pid=$p.Id;executable=$Python;script=(Join-Path $DemoRoot 'server\server.py');startedAt=(Get-Date).ToString('o')} | ConvertTo-Json | Set-Content (Join-Path $DemoRoot 'logs\server-process.json') -Encoding UTF8
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    Start-Sleep -Milliseconds 300
    $p.Refresh()
    if ($p.HasExited) { throw 'Receiver exited. See logs\server-errors.log.' }
    try {
        $health = Invoke-RestMethod -Uri $url -TimeoutSec 1
        if ($health.service -eq 'HoloLensCaptureDemo') {
            Write-Host "Receiver ready: $($config.serverUrl)"
            Write-Host "Browser monitor: http://127.0.0.1:$port/ (or double-click OpenMonitor.cmd)"
            Write-Host 'Photos: server\data\images; logs: logs\server.log'
            exit 0
        }
    } catch [System.Net.WebException] { }
}
throw 'Receiver did not become ready. Inspect logs\server-errors.log.'
