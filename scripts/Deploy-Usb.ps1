[CmdletBinding()]
param([Parameter(Mandatory=$true)][guid]$DeviceId, [string]$Pin)
. "$PSScriptRoot\Common.ps1"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$sdk = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots').KitsRoot10
$tool = Join-Path $sdk 'bin\10.0.19041.0\x86\WinAppDeployCmd.exe'
$signed = Get-Content (Join-Path $DemoRoot 'builds\UWP-Signed\signing.json') -Raw | ConvertFrom-Json
$package = $signed.package
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $reader = [IO.StreamReader]::new($zip.GetEntry('Data/StreamingAssets/demo-config.json').Open())
    try { $clientConfig = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    $reader = [IO.StreamReader]::new($zip.GetEntry('AppxManifest.xml').Open())
    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
} finally { $zip.Dispose() }
if ($clientConfig.simulateCamera -or $manifest.Package.Identity.ProcessorArchitecture -ne 'x86' -or $manifest.Package.Identity.Name -ne 'HoloLensCaptureDemo') {
    throw 'Expected the signed x86 real-camera HoloLensCaptureDemo package.'
}
$serverConfig = Get-Content (Join-Path $DemoRoot 'config\server.local.json') -Raw | ConvertFrom-Json
if ($serverConfig.apiToken -ne $clientConfig.apiToken) { throw 'Package and receiver pairing tokens differ. Rebuild before deploying.' }
if ($clientConfig.serverUrl -ne (Get-DemoConfig).serverUrl) { throw 'Package uses an older receiver address. Rebuild before deploying.' }
$dependency = @(Get-ChildItem (Join-Path $DemoRoot 'builds\UWP\AppPackages') -Filter 'Microsoft.VCLibs.x86.14.00.appx' -Recurse -File | Sort-Object LastWriteTimeUtc -Descending)[0].FullName
$connection = @('-guid',$DeviceId.ToString(),'-connecttimeout','20')
if ($Pin) { $connection += @('-pin',$Pin) }
$listing = & $tool list @connection 2>&1
if ($LASTEXITCODE -ne 0) {
    $listing | ForEach-Object { if ($Pin) { ([string]$_).Replace($Pin,'<pairing PIN>') } else { $_ } }
    throw 'Device pairing failed. Keep its developer Pair window open and supply the displayed PIN.'
}
$existing = [bool]($listing -match 'HoloLensCaptureDemo_')
$operation = if ($existing) { 'update' } else { 'install' }
Write-Host "USB $operation of HoloLensCaptureDemo $($manifest.Package.Identity.Version), real camera, receiver $($clientConfig.serverUrl)"
$output = & $tool $operation -file $package -dependency $dependency @connection 2>&1
$code = $LASTEXITCODE
$safeOutput = @($output | ForEach-Object { if ($Pin) { ([string]$_).Replace($Pin,'<pairing PIN>') } else { [string]$_ } })
$safeOutput | Set-Content (Join-Path $DemoRoot 'logs\usb-install.log') -Encoding UTF8
$safeOutput | Write-Output
if ($code -ne 0) { throw "USB deployment failed with exit code $code; existing app data was not explicitly removed." }
$verify = & $tool list @connection 2>&1
if ($LASTEXITCODE -ne 0 -or !($verify -match ('HoloLensCaptureDemo_' + [regex]::Escape($manifest.Package.Identity.Version)))) {
    throw 'Deployment returned success but the expected installed package was not verified.'
}
[ordered]@{deployedAt=(Get-Date).ToString('o');transport='USB';deviceId=$DeviceId.ToString();operation=$operation;package=$package;
    version=$manifest.Package.Identity.Version;architecture='x86';simulatedCamera=$false;receiver=$clientConfig.serverUrl;installedVerified=$true;launchVerified=$false} |
    ConvertTo-Json | Set-Content (Join-Path $DemoRoot 'logs\usb-deployment.json') -Encoding UTF8
Write-Host 'USB installation verified. Open HoloLensCaptureDemo on the headset and allow its camera permission.'
