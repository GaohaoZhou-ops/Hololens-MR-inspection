Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$script:DemoRoot = Split-Path $PSScriptRoot -Parent
$script:SharedRoot = Join-Path (Split-Path $DemoRoot -Parent) 'HoloLens1Environment'
. (Join-Path $SharedRoot 'scripts\Common.ps1')
$script:Python = Join-Path $SharedRoot 'tools\Python\3.14.10\python.exe'
Set-LocalProcessCaches
New-Item -ItemType Directory -Path (Join-Path $DemoRoot 'logs') -Force | Out-Null

function Get-DemoConfig {
    $file = Join-Path $DemoRoot 'Assets\StreamingAssets\demo-config.json'
    if (!(Test-Path $file)) { throw 'Run Configure-Lan.cmd first.' }
    Get-Content $file -Raw | ConvertFrom-Json
}
