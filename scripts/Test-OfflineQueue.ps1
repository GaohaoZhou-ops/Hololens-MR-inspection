[CmdletBinding()]
param()
. "$PSScriptRoot\Common.ps1"
# This fixture has its own temporary database and never opens server/data.
if (@(Get-NetTCPConnection -State Listen -LocalPort 8767 -ErrorAction SilentlyContinue).Count) {
    throw 'Port 8767 is occupied. Close that test receiver before running the offline test.'
}
$control = Join-Path $DemoRoot 'logs\offline-smoke-control.json'
$output = Join-Path $DemoRoot 'logs\offline-fixture.log'
$errors = Join-Path $DemoRoot 'logs\offline-fixture-errors.log'
'{"mode":"offline"}' | Set-Content $control -Encoding ASCII
$fixture = Join-Path $DemoRoot 'tests\offline_receiver_fixture.py'
$process = Start-Process $Python -ArgumentList ('-u "{0}"' -f $fixture) -WorkingDirectory $DemoRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput $output -RedirectStandardError $errors
$handle = $process.Handle
try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        $process.Refresh()
        if ($process.HasExited) { throw "Offline fixture exited. See $errors." }
        if ((Test-Path $output) -and (Select-String -Path $output -Pattern 'OFFLINE_FIXTURE_READY' -SimpleMatch -Quiet)) { $ready = $true; break }
        Start-Sleep -Milliseconds 250
    }
    if (!$ready) { throw 'Offline fixture did not start.' }
    & "$PSScriptRoot\Build-Demo.ps1" -Stage OfflineSmoke
    if ($LASTEXITCODE -ne 0) { throw 'Unity offline test failed. See logs\unity-offlinesmoke.log.' }
} finally {
    '{"mode":"stop"}' | Set-Content $control -Encoding ASCII
    if (!$process.WaitForExit(5000)) { $process.Kill(); $process.WaitForExit() }
    $process.Dispose()
}
