[CmdletBinding()]
param([switch]$Browser, [switch]$CaptureInteraction)
. "$PSScriptRoot\Common.ps1"
if (@(Get-NetTCPConnection -State Listen -LocalPort 8769 -ErrorAction SilentlyContinue).Count) { throw 'Port 8769 is already in use.' }
$control = Join-Path $DemoRoot 'logs\history-smoke-control.json'
'{"mode":"online"}' | Set-Content $control -Encoding ASCII
$process = Start-Process $Python -ArgumentList ('-u "{0}"' -f (Join-Path $DemoRoot 'tests\history_receiver_fixture.py')) -WorkingDirectory $DemoRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $DemoRoot 'logs\history-fixture.log') -RedirectStandardError (Join-Path $DemoRoot 'logs\history-fixture-errors.log')
$handle = $process.Handle
try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        $process.Refresh()
        if ($process.HasExited) { throw 'History fixture exited. Inspect logs\history-fixture-errors.log.' }
        try { $health = Invoke-RestMethod 'http://127.0.0.1:8769/health' -TimeoutSec 1; if ($health.historyAvailable) { $ready=$true; break } } catch { }
        Start-Sleep -Milliseconds 250
    }
    if (!$ready) { throw 'History fixture did not start.' }
    if ($CaptureInteraction) { & "$PSScriptRoot\Build-Demo.ps1" -Stage CaptureSmoke }
    else { & "$PSScriptRoot\Build-Demo.ps1" -Stage HistorySmoke }
    if ($LASTEXITCODE -ne 0) { throw 'Unity history test failed.' }
    if ($Browser) { & "$PSScriptRoot\Test-WorkBrowser.ps1" }
} finally {
    '{"mode":"stop"}' | Set-Content $control -Encoding ASCII
    if (!$process.WaitForExit(5000)) { $process.Kill(); $process.WaitForExit() }
    $process.Dispose()
}
