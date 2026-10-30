[CmdletBinding()]
param([switch]$Browser)
. "$PSScriptRoot\Common.ps1"
# This fixture has its own temporary database and never opens server/data.
if (@(Get-NetTCPConnection -State Listen -LocalPort 8768 -ErrorAction SilentlyContinue).Count) {
    throw 'Port 8768 is occupied. Close that test receiver before running the pose test.'
}
$control = Join-Path $DemoRoot 'logs\pose-smoke-control.json'
$output = Join-Path $DemoRoot 'logs\pose-fixture.log'
$errors = Join-Path $DemoRoot 'logs\pose-fixture-errors.log'
'{"mode":"offline"}' | Set-Content $control -Encoding ASCII
$fixture = Join-Path $DemoRoot 'tests\pose_receiver_fixture.py'
$process = Start-Process $Python -ArgumentList ('-u "{0}"' -f $fixture) -WorkingDirectory $DemoRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput $output -RedirectStandardError $errors
$handle = $process.Handle
try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        $process.Refresh()
        if ($process.HasExited) { throw "Pose fixture exited. See $errors." }
        if ((Test-Path $output) -and (Select-String -Path $output -Pattern 'POSE_FIXTURE_READY' -SimpleMatch -Quiet)) { $ready = $true; break }
        Start-Sleep -Milliseconds 250
    }
    if (!$ready) { throw 'Pose fixture did not start.' }
    & "$PSScriptRoot\Build-Demo.ps1" -Stage PoseSmoke
    if ($LASTEXITCODE -ne 0) { throw 'Unity pose test failed. See logs\unity-posesmoke.log.' }
    if ($Browser) { & "$PSScriptRoot\Test-PoseBrowser.ps1" }
} finally {
    '{"mode":"stop"}' | Set-Content $control -Encoding ASCII
    if (!$process.WaitForExit(5000)) { $process.Kill(); $process.WaitForExit() }
    $process.Dispose()
}
