[CmdletBinding()]
param()
. "$PSScriptRoot\Common.ps1"
$record = Join-Path $DemoRoot 'logs\server-process.json'
if (!(Test-Path $record)) { Write-Host 'No recorded receiver process.'; exit 0 }
$saved = Get-Content $record -Raw | ConvertFrom-Json
$p = Get-CimInstance Win32_Process -Filter "ProcessId=$($saved.pid)" -ErrorAction SilentlyContinue
if ($p) {
    if ($p.ExecutablePath -ne $Python -or !$p.CommandLine.Contains((Join-Path $DemoRoot 'server\server.py'))) { throw 'PID belongs to another process; refusing to stop it.' }
    Stop-Process -Id $saved.pid
}
Remove-Item $record
Write-Host 'Demo receiver stopped.'
