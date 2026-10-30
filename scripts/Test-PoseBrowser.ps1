param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$root = Split-Path $PSScriptRoot -Parent
$shared = Join-Path (Split-Path $root -Parent) 'HoloLens1Environment'
$profile = Join-Path $shared 'cache\edge-pose-test'
$baseUrl = 'http://127.0.0.1:8768'
$debugUrl = 'http://127.0.0.1:9234'
$script:sequence = 0
$script:browserErrors = [Collections.Generic.List[string]]::new()
$script:ws = $null
$edgeProcess = $null
function Cdp([string]$Method, $Parameters = @{}) {
    $script:sequence++
    $id = $script:sequence
    $bytes = [Text.Encoding]::UTF8.GetBytes((@{id=$id;method=$Method;params=$Parameters} | ConvertTo-Json -Depth 30 -Compress))
    $timeout = [Threading.CancellationTokenSource]::new(15000)
    try {
        $null = $script:ws.SendAsync([ArraySegment[byte]]::new($bytes),[Net.WebSockets.WebSocketMessageType]::Text,$true,$timeout.Token).GetAwaiter().GetResult()
        while ($true) {
            $stream = [IO.MemoryStream]::new()
            try {
                do {
                    $buffer = [byte[]]::new(65536)
                    $received = $script:ws.ReceiveAsync([ArraySegment[byte]]::new($buffer),$timeout.Token).GetAwaiter().GetResult()
                    if ($received.MessageType -eq [Net.WebSockets.WebSocketMessageType]::Close) { throw 'Browser closed the debug connection.' }
                    $stream.Write($buffer,0,$received.Count)
                } until ($received.EndOfMessage)
                $reply = [Text.Encoding]::UTF8.GetString($stream.ToArray()) | ConvertFrom-Json
            } finally { $stream.Dispose() }
            if ($reply.PSObject.Properties['method'] -and $reply.method -eq 'Runtime.exceptionThrown') {
                $script:browserErrors.Add(($reply.params | ConvertTo-Json -Depth 12 -Compress))
            }
            if ($reply.PSObject.Properties['id'] -and $reply.id -eq $id) {
                if ($reply.PSObject.Properties['error']) { throw ($reply.error | ConvertTo-Json -Compress) }
                return $reply.result
            }
        }
    } finally { $timeout.Dispose() }
}
function Js([string]$Expression) {
    $result = Cdp 'Runtime.evaluate' @{expression=$Expression;awaitPromise=$true;returnByValue=$true}
    if ($result.PSObject.Properties['exceptionDetails']) { throw ($result.exceptionDetails | ConvertTo-Json -Depth 12 -Compress) }
    if ($result.result.PSObject.Properties['value']) { return $result.result.value }
}
function Wait-Js([string]$Expression, [string]$Label) {
    $until = (Get-Date).AddSeconds(15)
    do {
        if (Js $Expression) { Write-Output "PASS $Label"; return }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $until)
    throw "Timed out: $Label"
}
function Shot([string]$Name) {
    $shot = Cdp 'Page.captureScreenshot' @{format='png';captureBeyondViewport=$false}
    [IO.File]::WriteAllBytes((Join-Path $root ("logs\" + $Name)), [Convert]::FromBase64String($shot.data))
}
try {
    try { $existing = Invoke-RestMethod "$debugUrl/json/version" -TimeoutSec 1 } catch { $existing = $null }
    if ($existing) { throw 'The test debugging port is already in use.' }
    $productionBefore = (Invoke-RestMethod 'http://127.0.0.1:8765/api/captures' -TimeoutSec 3).stats.photos
    $edge = Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'
    $edgeProcess = Start-Process $edge -ArgumentList ('--headless=new --disable-gpu --no-first-run --no-default-browser-check --remote-debugging-port=9234 --user-data-dir="{0}" about:blank' -f $profile) -PassThru -RedirectStandardOutput (Join-Path (Join-Path $root 'logs') 'edge-pose-test.log') -RedirectStandardError (Join-Path (Join-Path $root 'logs') 'edge-pose-test-errors.log')
    $until = (Get-Date).AddSeconds(15); $target = $null
    do {
        try { $target = @((Invoke-RestMethod "$debugUrl/json/list" -TimeoutSec 1) | Where-Object type -eq 'page')[0] } catch { }
        if (!$target) { Start-Sleep -Milliseconds 300 }
    } until ($target -or (Get-Date) -gt $until)
    if (!$target) { throw 'Headless Edge did not start.' }
    $script:ws = [Net.WebSockets.ClientWebSocket]::new()
    $null = $script:ws.ConnectAsync([uri]$target.webSocketDebuggerUrl,[Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $primarySocket = $script:ws
    $null = Cdp 'Runtime.enable'; $null = Cdp 'Page.enable'
    $null = Cdp 'Emulation.setDeviceMetricsOverride' @{width=1440;height=1060;deviceScaleFactor=1;mobile=$false}
    $null = Cdp 'Page.navigate' @{url="$baseUrl/#trajectory"}

    $sessionId = [guid]::NewGuid().ToString('N')
    $now = [DateTime]::UtcNow
    $samples = @(for ($i=0; $i -lt 60; $i++) {
        $angle = $i * 2 * [Math]::PI / 60
        @{sequence=$i;segment=$(if($i -lt 25){0}else{1});elapsed=[double]$i;sampledAt=$now.AddSeconds($i-59).ToString('o');tracked=($i -ne 25);trackingState=$(if($i -eq 25){'Unavailable'}else{'Editor'});x=1.3*[Math]::Sin($angle);y=.3*[Math]::Sin(2*$angle);z=1.3*(1-[Math]::Cos($angle));qx=0;qy=[Math]::Sin($angle/2);qz=0;qw=[Math]::Cos($angle/2)}
    })
    $batch = @{session=@{sessionId=$sessionId;startedAt=$now.ToString('o');deviceName='Browser fixture';clientKind='editor'};samples=$samples}
    $null = Invoke-RestMethod "$baseUrl/api/poses" -Method Post -Headers @{Authorization='Bearer pose-test-token-not-production'} -ContentType 'application/json' -Body ($batch | ConvertTo-Json -Depth 8 -Compress)
    Wait-Js "document.getElementById('pose-canvas')?.dataset.pointCount==='59' && document.getElementById('pose-session').value==='$sessionId'" 'automatic latest session, all samples and tracking gap'
    Wait-Js "document.documentElement.scrollWidth<=innerWidth+1 && !document.getElementById('pose-empty').hidden===false" 'desktop canvas fits layout'
    $null = Js "window.__refreshChanges=[];new MutationObserver(r=>window.__refreshChanges.push(...r)).observe(document.getElementById('refresh'),{attributes:true});true"
    $null = Js "document.getElementById('pose-scrub').value='10';document.getElementById('pose-scrub').dispatchEvent(new Event('input'));"
    Wait-Js "document.getElementById('pose-canvas').dataset.selectedSequence==='10' && !document.getElementById('pose-live').checked && document.getElementById('pose-position').textContent.includes('1.126')" 'historical position and orientation playback'
    $null = Js "document.getElementById('pose-scrub').value='25';document.getElementById('pose-scrub').dispatchEvent(new Event('input'));"
    Wait-Js "document.getElementById('pose-position').textContent==='\u2014' && document.getElementById('pose-canvas').dataset.selectedSequence==='25'" 'tracking loss never renders fake zero pose'
    $null = Js "document.getElementById('pose-live').click();document.getElementById('pose-top').click()"
    Wait-Js "document.getElementById('pose-top').getAttribute('aria-pressed')==='true' && document.getElementById('pose-canvas').dataset.selectedSequence==='59'" 'top view and live sample following'
    $null = Js "document.getElementById('pose-top').click()"
    Shot 'trajectory-desktop.png'
    $oldSession = Js "document.getElementById('pose-session').options[1].value"
    $null = Js "document.getElementById('pose-session').value='$oldSession';document.getElementById('pose-session').dispatchEvent(new Event('change'))"
    Wait-Js "document.getElementById('pose-canvas').dataset.pointCount==='2' && document.getElementById('pose-follow').getAttribute('aria-pressed')==='false'" 'session selection preserves separate coordinates'
    $null = Js "document.getElementById('pose-follow').click()"
    Wait-Js "document.getElementById('pose-session').value==='$sessionId' && document.getElementById('pose-canvas').dataset.pointCount==='59'" 'follow newest session resumes'
    $null = Js "document.getElementById('auto-toggle').click()"
    $extra = @{} + $samples[-1];$extra.sequence=60;$extra.elapsed=60;$extra.sampledAt=[DateTime]::UtcNow.ToString('o');$batch.samples=@($extra)
    $null = Invoke-RestMethod "$baseUrl/api/poses" -Method Post -Headers @{Authorization='Bearer pose-test-token-not-production'} -ContentType 'application/json' -Body ($batch | ConvertTo-Json -Depth 8 -Compress)
    Start-Sleep -Seconds 2
    Wait-Js "document.getElementById('pose-canvas').dataset.pointCount==='59'" 'automatic refresh pause applies to poses'
    $null = Js "document.getElementById('refresh').click()"
    Wait-Js "document.getElementById('pose-canvas').dataset.pointCount==='60' && window.__refreshChanges.length===0" 'manual pose refresh without button flicker'
    $null = Js "window.__poseExport=null;const original=URL.createObjectURL;URL.createObjectURL=b=>{b.text().then(t=>window.__poseExport=JSON.parse(t));return original(b)};document.getElementById('pose-export').click();"
    Wait-Js "window.__poseExport?.samples.length===61 && window.__poseExport.session.sessionId==='$sessionId'" 'JSON export includes full session and orientation'
    $null = Cdp 'Emulation.setDeviceMetricsOverride' @{width=390;height=844;deviceScaleFactor=1;mobile=$true}
    Wait-Js "document.documentElement.scrollWidth<=innerWidth+1" 'mobile trajectory has no horizontal overflow'
    Shot 'trajectory-mobile.png'
    $null = Js "document.getElementById('capture-tab').click()"
    Wait-Js "!document.getElementById('capture-pane').hidden && document.getElementById('trajectory-pane').hidden && document.getElementById('stat-photos').textContent==='0'" 'photo view remains available'
    if ($script:browserErrors.Count) { throw ($script:browserErrors -join "`n") }
    $productionAfter = (Invoke-RestMethod 'http://127.0.0.1:8765/api/captures' -TimeoutSec 3).stats.photos
    if ($productionAfter -ne $productionBefore) { throw 'Production history changed during isolated test.' }
    [ordered]@{passed=$true;verifiedAt=(Get-Date).ToString('o');fixtureUrl=$baseUrl;sampleCount=61;refreshAttributeChanges=(Js 'window.__refreshChanges.length');productionPhotos=$productionAfter;browserErrors=@($script:browserErrors)} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $root 'logs\pose-browser-smoke.json') -Encoding UTF8
    Write-Output 'POSE_BROWSER_SMOKE_PASSED'
} finally {
    if ($script:ws -and $script:ws.State -eq [Net.WebSockets.WebSocketState]::Open) { try { $null = Cdp 'Browser.close' } catch { } }
    if ($script:ws) { $script:ws.Dispose() }
    if ($edgeProcess -and !$edgeProcess.HasExited) { $edgeProcess | Stop-Process -ErrorAction SilentlyContinue }
}
