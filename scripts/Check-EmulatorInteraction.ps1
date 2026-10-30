. "$PSScriptRoot\Common.ps1"
$admin = (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (!$admin) {
    $p=Start-Process powershell.exe -Verb RunAs -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -File "{0}"' -f $PSCommandPath) -PassThru
    $handle=$p.Handle; $p.WaitForExit(); exit $p.ExitCode
}
try {
    $captureScript = Join-Path $SharedRoot 'scripts\Capture-EmulatorWindow.ps1'
    & $captureScript
    $window = (Get-Process XDE | Select-Object -First 1).MainWindowHandle
    $initial = @(Get-ChildItem (Join-Path $DemoRoot 'server\data\images') -Filter '*.jpg').Count
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class EmulatorTestKeys {
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr info);
}
'@
    if ([EmulatorScreenCapture]::GetForegroundWindow() -ne $window) { throw 'The emulator must be in the foreground.' }
    # XDE Human Input maps Enter to an Air Tap. These keys go to XDE, not the guest keyboard API.
    try { [EmulatorTestKeys]::keybd_event(13,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 100 }
    finally { [EmulatorTestKeys]::keybd_event(13,0,2,[UIntPtr]::Zero) }
    Start-Sleep -Seconds 3
    $afterTap = @(Get-ChildItem (Join-Path $DemoRoot 'server\data\images') -Filter '*.jpg').Count
    & $captureScript
    Copy-Item (Join-Path $SharedRoot 'logs\emulator-window.png') (Join-Path $DemoRoot 'logs\emulator-before-move.png') -Force
    if ([EmulatorScreenCapture]::GetForegroundWindow() -ne $window) { throw 'The emulator lost focus before movement.' }
    try { [EmulatorTestKeys]::keybd_event(68,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 550 }
    finally { [EmulatorTestKeys]::keybd_event(68,0,2,[UIntPtr]::Zero) }
    Start-Sleep -Seconds 1
    & $captureScript
    Copy-Item (Join-Path $SharedRoot 'logs\emulator-window.png') (Join-Path $DemoRoot 'logs\emulator-after-move.png') -Force
    [ordered]@{checkedAt=(Get-Date).ToString('o');airTapReceived=($afterTap -gt $initial);initialImageCount=$initial;afterTapImageCount=$afterTap;simulatedMovement='D held for 550 ms';beforeImage='logs/emulator-before-move.png';afterImage='logs/emulator-after-move.png'} |
        ConvertTo-Json | Set-Content (Join-Path $DemoRoot 'logs\emulator-interaction.json') -Encoding UTF8
} catch { $_ | Out-String | Set-Content (Join-Path $DemoRoot 'logs\emulator-interaction-error.log') -Encoding UTF8; throw }
