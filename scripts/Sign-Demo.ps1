[CmdletBinding()]
param([switch]$SimulatedCamera)
. "$PSScriptRoot\Common.ps1"
$folder = if ($SimulatedCamera) {'UWP-Simulated'} else {'UWP'}
$packages = @(Get-ChildItem (Join-Path $DemoRoot "builds\$folder\AppPackages") -Filter '*.msix' -Recurse -File | Sort-Object LastWriteTimeUtc -Descending)
if ($packages.Count -eq 0) { throw 'Build the selected version first; no MSIX package was found.' }
$certificate = Get-ChildItem Cert:\CurrentUser\My | Where-Object {
    $_.Subject -eq 'CN=HoloLensWorkspace' -and $_.FriendlyName -eq 'HoloLensCaptureDemo local development' -and
    $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(7)
} | Select-Object -First 1
if (!$certificate) {
    $certificate = New-SelfSignedCertificate -Type Custom -Subject 'CN=HoloLensWorkspace' -FriendlyName 'HoloLensCaptureDemo local development' -CertStoreLocation Cert:\CurrentUser\My -KeyUsage DigitalSignature -KeyExportPolicy NonExportable -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}CA=false') -NotAfter (Get-Date).AddYears(1)
}
$output = Join-Path $DemoRoot "builds\$folder-Signed"
New-Item $output -ItemType Directory -Force | Out-Null
# Use the .appx extension accepted by WinAppDeployCmd. Unity's generated
# package has the same APPX container and manifest; keep its bytes intact.
$package = Join-Path $output ([IO.Path]::ChangeExtension($packages[0].Name,'.appx'))
Copy-Item $packages[0].FullName $package -Force
$sdk = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots').KitsRoot10
$signTool = Join-Path $sdk 'bin\10.0.19041.0\x64\signtool.exe'
& $signTool sign /fd SHA256 /sha1 $certificate.Thumbprint /s My $package
if ($LASTEXITCODE -ne 0) { throw 'Package signing failed.' }
Export-Certificate -Cert $certificate -FilePath (Join-Path $output 'HoloLensWorkspace.cer') | Out-Null
[ordered]@{signedAt=(Get-Date).ToString('o');package=$package;certificateThumbprint=$certificate.Thumbprint;privateKey='Non-exportable, CurrentUser/My';trustedRootModified=$false} |
    ConvertTo-Json | Set-Content (Join-Path $output 'signing.json') -Encoding UTF8
Write-Host "Signed development package: $package"
