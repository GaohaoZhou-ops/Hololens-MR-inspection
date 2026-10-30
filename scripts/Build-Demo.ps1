[CmdletBinding()]
param([ValidateSet('Configure','Export','Compile','Smoke','OfflineSmoke','PoseSmoke')][string]$Stage='Compile', [switch]$SimulatedCamera)
. "$PSScriptRoot\Common.ps1"
$null = Get-DemoConfig
$unity = Get-UnityPath
if (!(Test-Path $unity)) { throw 'Unity 2019.4.41f2 is missing from the shared environment.' }
if (Test-Path (Join-Path $DemoRoot 'Temp\UnityLockfile')) { throw 'Close this demo in Unity before building.' }
$method = switch ($Stage) {
    'Configure' {'HoloLensCaptureDemo.Editor.DemoBuild.Configure'}
    'Smoke' {'HoloLensCaptureDemo.Editor.DemoBuild.SmokeTest'}
    'OfflineSmoke' {'HoloLensCaptureDemo.Editor.DemoBuild.OfflineSmokeTest'}
    'PoseSmoke' {'HoloLensCaptureDemo.Editor.DemoBuild.PoseSmokeTest'}
    default {if ($SimulatedCamera) {'HoloLensCaptureDemo.Editor.DemoBuild.ExportSimulated'} else {'HoloLensCaptureDemo.Editor.DemoBuild.Export'}}
}
$suffix = if ($SimulatedCamera) {'-simulated'} else {''}
$log = Join-Path $DemoRoot ("logs\unity-{0}{1}.log" -f $Stage.ToLowerInvariant(),$suffix)
$quit = if ($Stage -in @('Smoke','OfflineSmoke','PoseSmoke')) { '' } else { '-quit ' }
$arguments = $quit + ('-projectPath "{0}" -buildTarget WindowsStoreApps -executeMethod {1} -logFile "{2}"' -f $DemoRoot,$method,$log)
Write-Host "Unity log: $log"
$p = Start-Process $unity -ArgumentList $arguments -PassThru
$handle = $p.Handle
if (!$p.WaitForExit(1800000)) { throw "Unity is still running (PID $($p.Id)); inspect the editor and log." }
$p.Refresh()
if ($p.ExitCode -ne 0) { throw "Unity exited with $($p.ExitCode). See $log." }
$marker = switch ($Stage) { 'Configure' {'CAPTURE_DEMO_CONFIGURED'} 'Smoke' {'CAPTURE_DEMO_SMOKE True'} 'OfflineSmoke' {'CAPTURE_DEMO_OFFLINE_SMOKE True'} 'PoseSmoke' {'CAPTURE_DEMO_POSE_SMOKE True'} default {'CAPTURE_DEMO_EXPORTED'} }
if (!(Select-String -Path $log -Pattern $marker -SimpleMatch -Quiet)) { throw 'Unity did not report successful completion. Inspect the log.' }
if ($Stage -in @('Configure','Smoke','OfflineSmoke','PoseSmoke')) { Write-Host "$Stage succeeded."; exit 0 }
$folder = if ($SimulatedCamera) {'UWP-Simulated'} else {'UWP'}
$output = Join-Path $DemoRoot "builds\$folder"
$solution = Join-Path $output 'HoloLensCaptureDemo.sln'
if (!(Test-Path $solution)) { throw 'UWP solution was not generated.' }
if ($Stage -eq 'Compile') {
    $msbuild = Join-Path $SharedRoot 'VS\MSBuild\Current\Bin\MSBuild.exe'
    $buildLog = Join-Path $DemoRoot "logs\msbuild$suffix.log"
    & $msbuild $solution /restore /m /nologo /verbosity:minimal /p:Configuration=Release /p:Platform=x86 /p:AppxBundle=Never /p:UapAppxPackageBuildMode=SideloadOnly /p:AppxPackageSigningEnabled=false "/bl:$(Join-Path $DemoRoot "logs\msbuild$suffix.binlog")" /fl "/flp:logfile=$buildLog;verbosity=normal;encoding=UTF-8"
    if ($LASTEXITCODE -ne 0) { throw "UWP compilation failed; see $buildLog." }
    [ordered]@{builtAt=(Get-Date).ToString('o');unityVersion='2019.4.41f2';configuration='Release';architecture='x86';simulatedCamera=[bool]$SimulatedCamera;solution=$solution;unsignedPackage=$true} | ConvertTo-Json | Set-Content (Join-Path $DemoRoot "logs\build-status$suffix.json") -Encoding UTF8
}
Write-Host "Solution: $solution"
