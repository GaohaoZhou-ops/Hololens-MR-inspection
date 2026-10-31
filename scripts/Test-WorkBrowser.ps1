param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$root = Split-Path $PSScriptRoot -Parent
$shared = Join-Path (Split-Path $root -Parent) 'HoloLens1Environment'
$profile = Join-Path $shared 'cache\edge-history-test'
$baseUrl = 'http://127.0.0.1:8769'
$debugUrl = 'http://127.0.0.1:9235'
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
    if ($existing) { throw 'The browser debugging port is in use.' }
    $productionBefore = (Invoke-RestMethod 'http://127.0.0.1:8765/api/captures' -TimeoutSec 3).stats.photos
    $deviceId = 'ffffffffffffffffffffffffffffffff'
    $workId = 'eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee'
    $auth = @{Authorization='Bearer history-test-token-not-production'}
    $poll = @{deviceId=$deviceId;workId=$workId;deviceName='Browser history fixture';clientKind='editor';status='Test device';cache=@{downloadedAnchors=2;downloadBytes=2048;visibleLabels=2;pendingPhotos=1;photoBytes=1024;pendingPoses=3;poseBytes=120;pendingArchives=0}} | ConvertTo-Json -Depth 5 -Compress
    $null = Invoke-RestMethod "$baseUrl/api/device-poll" -Method Post -Headers $auth -ContentType 'application/json' -Body $poll
    $jpeg = [byte[]]@(255,216,255,217)
    for ($i=0; $i -lt 2; $i++) {
        $captureId = [guid]::NewGuid().ToString('N')
        $photoHeaders = @{Authorization=$auth.Authorization;'X-Capture-Id'=$captureId}
        $null = Invoke-RestMethod "$baseUrl/api/captures" -Method Post -Headers $photoHeaders -ContentType 'image/jpeg' -Body $jpeg
        $anchorHeaders = @{Authorization=$auth.Authorization;'X-Device-Id'=$deviceId;'X-Work-Id'=$workId;'X-Anchor-Kind'='editor'}
        $null = Invoke-RestMethod "$baseUrl/api/work-anchors/$captureId" -Method Post -Headers $anchorHeaders -ContentType 'application/octet-stream' -Body ([Text.Encoding]::UTF8.GetBytes('{"x":1,"y":0,"z":2}'))
    }
    $poseId = [guid]::NewGuid().ToString('N')
    $poseSession = @{sessionId=$poseId;startedAt=[DateTime]::UtcNow.ToString('o');deviceName='Browser trajectory fixture';clientKind='editor'}
    $poseSamples = @(0..2 | ForEach-Object { @{sequence=$_;elapsed=[double]$_;sampledAt=[DateTime]::UtcNow.ToString('o');tracked=$true;trackingState='Editor';segment=0;x=[double]$_;y=0;z=0;qx=0;qy=0;qz=0;qw=1} })
    $null = Invoke-RestMethod "$baseUrl/api/poses" -Method Post -Headers $auth -ContentType 'application/json' -Body (@{session=$poseSession;samples=$poseSamples} | ConvertTo-Json -Depth 6 -Compress)
    $before = Invoke-RestMethod "$baseUrl/api/work-history"
    $photosBefore = $before.computerCache.images.count
    $posesBefore = $before.computerCache.poses.count
    $edge = Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'
    $edgeProcess = Start-Process $edge -ArgumentList ('--headless=new --disable-gpu --no-first-run --no-default-browser-check --remote-debugging-port=9235 --user-data-dir="{0}" about:blank' -f $profile) -PassThru -RedirectStandardOutput (Join-Path $root 'logs\edge-history-test.log') -RedirectStandardError (Join-Path $root 'logs\edge-history-test-errors.log')
    $until = (Get-Date).AddSeconds(15); $target = $null
    do {
        try { $target = @((Invoke-RestMethod "$debugUrl/json/list" -TimeoutSec 1) | Where-Object type -eq 'page')[0] } catch { }
        if (!$target) { Start-Sleep -Milliseconds 250 }
    } until ($target -or (Get-Date) -gt $until)
    if (!$target) { throw 'Headless Edge did not start.' }
    $script:ws = [Net.WebSockets.ClientWebSocket]::new()
    $null = $script:ws.ConnectAsync([uri]$target.webSocketDebuggerUrl,[Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $null = Cdp 'Runtime.enable'; $null = Cdp 'Page.enable'
    $null = Cdp 'Emulation.setDeviceMetricsOverride' @{width=1440;height=1100;deviceScaleFactor=1;mobile=$false}
    $null = Cdp 'Page.navigate' @{url=$baseUrl}
    Wait-Js "document.getElementById('work-device')?.value==='$deviceId' && !document.getElementById('resume-work').disabled" 'history records and registered device'
    $null = Js "document.getElementById('manage-cache').click()"
    Wait-Js "document.getElementById('cache-dialog').open && [...document.querySelectorAll('#cache-dialog input')].every(i=>!i.checked) && document.getElementById('confirm-cache').disabled" 'cache selection defaults to nothing'
    Shot 'history-cache-desktop.png'
    $null = Js "document.getElementById('cancel-cache').click()"
    if ((Invoke-RestMethod "$baseUrl/api/work-history").computerCache.images.count -ne $photosBefore) { throw 'Cancel deleted photos.' }
    $null = Js "document.getElementById('manage-cache').click()"
    Wait-Js "document.getElementById('cache-dialog').open" 'cache dialog reopened'
    $null = Js "(()=>{let i=document.querySelector('[data-cache-scope=computer][value=images]');i.checked=true;i.dispatchEvent(new Event('change',{bubbles:true}));})()"
    Wait-Js "!document.getElementById('cache-loss-label').hidden && document.getElementById('confirm-cache').disabled" 'explicit confirmation for irreplaceable originals'
    $null = Js "document.getElementById('cache-confirm-loss').checked=true;document.getElementById('cache-confirm-loss').dispatchEvent(new Event('change',{bubbles:true}));document.getElementById('confirm-cache').click()"
    Wait-Js "!document.getElementById('cache-dialog').open && document.getElementById('download-image').hidden && document.getElementById('capture-image').hidden" 'selected originals cleared and UI shows unavailable image'
    $after = Invoke-RestMethod "$baseUrl/api/work-history"
    if ($after.computerCache.images.count -ne 0 -or $after.computerCache.poses.count -ne $posesBefore) { throw 'Computer clear changed unselected categories.' }
    if (@($after.works | Where-Object workId -eq $workId)[0].restorableCount -ne 2) { throw 'Computer clear removed spatial archives.' }
    $null = Invoke-RestMethod "$baseUrl/api/device-poll" -Method Post -Headers $auth -ContentType 'application/json' -Body $poll
    $null = Js "document.getElementById('manage-cache').click()"
    Wait-Js "document.getElementById('cache-dialog').open && !document.querySelector('[data-cache-scope=device][value=downloadedAnchors]').disabled" 'online device cache controls'
    $null = Js "for(const value of ['downloadedAnchors','visibleLabels']){let i=document.querySelector('[data-cache-scope=device][value='+value+']');i.checked=true;i.dispatchEvent(new Event('change',{bubbles:true}));}document.getElementById('confirm-cache').click()"
    Wait-Js "!document.getElementById('cache-dialog').open" 'device cache command submitted'
    $delivery = Invoke-RestMethod "$baseUrl/api/device-poll" -Method Post -Headers $auth -ContentType 'application/json' -Body $poll
    if ($delivery.command.kind -ne 'clear_cache' -or @($delivery.command.categories).Count -ne 2 -or $delivery.command.categories -contains 'pendingPhotos') { throw 'Device command included unselected caches.' }
    $ack = @{deviceId=$deviceId;commandId=$delivery.command.commandId;status='completed';detail='Browser fixture cache ACK'} | ConvertTo-Json -Compress
    $null = Invoke-RestMethod "$baseUrl/api/device-acks" -Method Post -Headers $auth -ContentType 'application/json' -Body $ack
    Wait-Js "document.getElementById('work-command-status').textContent.includes('Browser fixture cache ACK')" 'device completion is driven by acknowledgement'
    $null = Js "document.getElementById('resume-work').click()"
    Wait-Js "document.getElementById('work-command-status').textContent.includes('等待设备完成')" 'restore command queued'
    $delivery = Invoke-RestMethod "$baseUrl/api/device-poll" -Method Post -Headers $auth -ContentType 'application/json' -Body $poll
    if ($delivery.command.kind -ne 'resume' -or $delivery.command.workId -ne $workId) { throw 'Restore targeted the wrong work.' }
    Shot 'history-desktop.png'
    $null = Cdp 'Emulation.setDeviceMetricsOverride' @{width=390;height=844;deviceScaleFactor=1;mobile=$true}
    $null = Js "document.getElementById('manage-cache').click()"
    Wait-Js "document.getElementById('cache-dialog').open && document.documentElement.scrollWidth<=innerWidth+1" 'cache dialog fits mobile viewport'
    Shot 'history-cache-mobile.png'
    $null = Js "document.getElementById('cancel-cache').click();window.poseMonitor.setAutomatic(false);document.getElementById('trajectory-tab').click()"
    Wait-Js "[...document.getElementById('pose-session').options].some(o=>o.value==='$poseId')" 'trajectory fixture available'
    $null = Js "document.getElementById('pose-session').value='$poseId';document.getElementById('pose-session').dispatchEvent(new Event('change'))"
    Wait-Js "document.getElementById('pose-count').textContent==='3 / 3'" 'trajectory before cleanup'
    $null = Js "document.getElementById('manage-cache').click()"
    Wait-Js "document.getElementById('cache-dialog').open" 'trajectory cleanup selection'
    $null = Js "(()=>{let i=document.querySelector('[data-cache-scope=computer][value=poses]');i.checked=true;i.dispatchEvent(new Event('change',{bubbles:true}));document.getElementById('cache-confirm-loss').checked=true;document.getElementById('cache-confirm-loss').dispatchEvent(new Event('change',{bubbles:true}));document.getElementById('confirm-cache').click();})()"
    Wait-Js "!document.getElementById('cache-dialog').open && document.getElementById('pose-count').textContent==='0 / 0' && !document.getElementById('pose-empty').hidden" 'trajectory view clears with database'
    $newPoint = $poseSamples[0].Clone(); $newPoint.sequence=3; $newPoint.elapsed=3
    $null = Invoke-RestMethod "$baseUrl/api/poses" -Method Post -Headers $auth -ContentType 'application/json' -Body (@{session=$poseSession;samples=@($newPoint)} | ConvertTo-Json -Depth 6 -Compress)
    $null = Js "document.getElementById('refresh').click()"
    Wait-Js "document.getElementById('pose-count').textContent==='1 / 1' && document.getElementById('pose-canvas').dataset.selectedSequence==='3'" 'same session resumes after cleanup'
    # Simulate a different viewer clearing while this viewer is paused. Recreate the
    # same session before its next read so an ID comparison alone cannot detect it.
    $null = Invoke-RestMethod "$baseUrl/api/cache/clear" -Method Post -Headers @{'X-Demo-Action'='cache-clear'} -ContentType 'application/json' -Body '{"categories":["poses"]}'
    $newPoint.sequence=4; $newPoint.elapsed=4
    $null = Invoke-RestMethod "$baseUrl/api/poses" -Method Post -Headers $auth -ContentType 'application/json' -Body (@{session=$poseSession;samples=@($newPoint)} | ConvertTo-Json -Depth 6 -Compress)
    $null = Js "document.getElementById('refresh').click()"
    Wait-Js "document.getElementById('pose-count').textContent==='1 / 1' && document.getElementById('pose-canvas').dataset.selectedSequence==='4'" 'other viewer cleanup drops cached points'
    if (@((Invoke-RestMethod "$baseUrl/api/work-history").works | Where-Object workId -eq $workId)[0].restorableCount -ne 2) { throw 'Trajectory clear removed work archives.' }
    if ($script:browserErrors.Count) { throw ($script:browserErrors -join "`n") }
    $productionAfter = (Invoke-RestMethod 'http://127.0.0.1:8765/api/captures' -TimeoutSec 3).stats.photos
    if ($productionBefore -ne $productionAfter) { throw 'Production photo count changed during isolated browser check.' }
    [ordered]@{passed=$true;verifiedAt=(Get-Date).ToString('o');fixtureUrl=$baseUrl;productionPhotos=$productionAfter;browserErrors=@($script:browserErrors);checks=@('history selection','empty cache defaults','cancel','loss confirmation','selective PC cleanup','spatial archives retained','device command selection','device ACK','restore command','mobile layout','trajectory cleanup','same-session new samples','other-viewer cache invalidation')} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $root 'logs\history-browser-smoke.json') -Encoding UTF8
    Write-Output 'HISTORY_BROWSER_SMOKE_PASSED'
} finally {
    if ($script:ws -and $script:ws.State -eq [Net.WebSockets.WebSocketState]::Open) { try { $null = Cdp 'Browser.close' } catch { } }
    if ($script:ws) { $script:ws.Dispose() }
    if ($edgeProcess -and !$edgeProcess.HasExited) { $edgeProcess | Stop-Process -ErrorAction SilentlyContinue }
}
