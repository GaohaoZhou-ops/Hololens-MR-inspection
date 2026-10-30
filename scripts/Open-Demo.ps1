. "$PSScriptRoot\Common.ps1"
$null = Get-DemoConfig
if (Test-Path (Join-Path $DemoRoot 'Temp\UnityLockfile')) { Write-Host 'Demo is already open in Unity.'; exit 0 }
$arguments = '-projectPath "{0}" -buildTarget WindowsStoreApps -executeMethod HoloLensCaptureDemo.Editor.DemoBuild.Open -logFile "{1}"' -f $DemoRoot,(Join-Path $DemoRoot 'logs\unity-editor.log')
Start-Process (Get-UnityPath) -ArgumentList $arguments | Out-Null
Write-Host 'Press Play, then Space to upload a simulated photo. Hold right mouse and use WASD to move.'
