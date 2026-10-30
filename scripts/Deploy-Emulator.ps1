[CmdletBinding()]
param([string]$Address)
. "$PSScriptRoot\Common.ps1"
if (!$Address) {
    $networkLog = Join-Path $SharedRoot 'logs\emulator-network.json'
    if (Test-Path $networkLog) {
        $network = Get-Content $networkLog -Raw | ConvertFrom-Json
        foreach ($adapter in $network.adapters) {
            $mac = $adapter.MacAddress -replace '(..)(?=.)','$1-'
            $neighbor = Get-NetNeighbor -AddressFamily IPv4 | Where-Object {$_.LinkLayerAddress -eq $mac} | Select-Object -First 1
            if ($neighbor) { $Address = $neighbor.IPAddress; break }
        }
    }
}
$ip = $null
if (!$Address -or ![Net.IPAddress]::TryParse($Address,[ref]$ip)) { throw 'Pass -Address with the emulator IP shown in its Additional Tools / Network panel.' }
$portal = "http://$Address"
$info = Invoke-RestMethod "$portal/api/os/info" -TimeoutSec 8
if ($info.Platform -ne 'Virtual Machine' -or $info.OsEdition -ne 'WindowsHolographic' -or $info.OsVersion -notlike '17763.134.*') {
    throw 'This helper only deploys to the verified HoloLens 1 emulator image.'
}
& "$PSScriptRoot\Sign-Demo.ps1" -SimulatedCamera
$signed = Get-Content (Join-Path $DemoRoot 'builds\UWP-Simulated-Signed\signing.json') -Raw | ConvertFrom-Json
$dependency = @(Get-ChildItem (Join-Path $DemoRoot 'builds\UWP-Simulated\AppPackages') -Filter 'Microsoft.VCLibs.x86.14.00.appx' -Recurse -File)[0].FullName
$sdk = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots').KitsRoot10
$deployTool = Join-Path $sdk 'bin\10.0.19041.0\x86\WinAppDeployCmd.exe'
# Device Portal lists packages across users. That does not guarantee an app is
# registered for the emulator's current visitor account, so use SDK install.
& $deployTool install -file $signed.package -dependency $dependency -ip $Address -connecttimeout 20
if ($LASTEXITCODE -ne 0) { throw 'Windows SDK app deployment failed. See the diagnostic output above.' }
$packages = Invoke-RestMethod "$portal/api/app/packagemanager/packages" -TimeoutSec 8
$app = @($packages.InstalledPackages | Where-Object {$_.PackageFullName -like 'HoloLensCaptureDemo_*'} |
    Sort-Object { [version]($_.PackageFullName.Split('_')[1]) } -Descending)[0]
$appId = [Uri]::EscapeDataString([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($app.PackageRelativeId)))
$packageId = [Uri]::EscapeDataString([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($app.PackageFullName)))
Invoke-RestMethod -Method Post -Uri "$portal/api/taskmanager/app?appid=$appId&package=$packageId" -TimeoutSec 20 | Out-Null
[ordered]@{deployedAt=(Get-Date).ToString('o');portal=$portal;osVersion=$info.OsVersion;package=$app.PackageFullName;simulatedCamera=$true;launchAccepted=$true} |
    ConvertTo-Json | Set-Content (Join-Path $DemoRoot 'logs\emulator-deployment.json') -Encoding UTF8
Write-Host "Demo deployed and launched. Device Portal: $portal"
Write-Host 'In Human Input mode, right-click or press Enter to simulate Air Tap.'
