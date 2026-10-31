param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$root = Split-Path $PSScriptRoot -Parent
$shared = Join-Path (Split-Path $root -Parent) 'HoloLens1Environment'
$baseUrl = 'http://127.0.0.1:8770'
$debugUrl = 'http://127.0.0.1:9236'
$script:sequence = 0
$script:browserErrors = [Collections.Generic.List[string]]::new()
$script:ws = $null
$script:clip = $null
$script:holdMedia = $false
$script:failMedia = $false
$script:heldMedia = [Collections.Generic.List[object]]::new()
$script:mediaRequests = [Collections.Generic.List[string]]::new()
function Serve-Media($Request) {
    $status=200; $bytes=[Convert]::FromBase64String($script:clip)
    $headers=@(@{name='Content-Type';value='video/webm'},@{name='Cache-Control';value='no-store'},@{name='Accept-Ranges';value='bytes'})
    if ($script:failMedia) { $status=401; $bytes=[Text.Encoding]::UTF8.GetBytes('Fixture unauthorized'); $headers=@(@{name='Content-Type';value='text/plain'}) }
    else {
        $range=$Request.request.headers.PSObject.Properties | Where-Object Name -ieq 'Range' | Select-Object -First 1
        if ($range -and $range.Value -match '^bytes=(\d+)-(\d*)$') {
            $start=[int]$Matches[1]; $end=$bytes.Length-1
            if ($Matches[2]) { $end=[Math]::Min($end,[int]$Matches[2]) }
            if ($start -le $end) { $headers+=@{name='Content-Range';value=("bytes {0}-{1}/{2}" -f $start,$end,$bytes.Length)}; $bytes=[byte[]]$bytes[$start..$end]; $status=206 }
        }
    }
    $headers+=@{name='Content-Length';value=[string]$bytes.Length}
    $script:sequence++
    $message=@{id=$script:sequence;method='Fetch.fulfillRequest';params=@{requestId=$Request.requestId;responseCode=$status;responseHeaders=$headers;body=[Convert]::ToBase64String($bytes)}} | ConvertTo-Json -Depth 12 -Compress
    $data=[Text.Encoding]::UTF8.GetBytes($message)
    $null=$script:ws.SendAsync([ArraySegment[byte]]::new($data),[Net.WebSockets.WebSocketMessageType]::Text,$true,[Threading.CancellationToken]::None).GetAwaiter().GetResult()
}

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
            if ($reply.PSObject.Properties['method'] -and $reply.method -eq 'Fetch.requestPaused') {
                $script:mediaRequests.Add($reply.params.request.url)
                if ($script:holdMedia) { $script:heldMedia.Add($reply.params) } else { Serve-Media $reply.params }
            }
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
function Js([string]$Expression, [switch]$UserGesture) {
    $result = Cdp 'Runtime.evaluate' @{expression=$Expression;awaitPromise=$true;returnByValue=$true;userGesture=[bool]$UserGesture}
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

function Camera([string]$Action, [string]$Id) {
    Invoke-RestMethod "$baseUrl/api/camera-lease" -Method Post -ContentType 'application/json' -Headers @{Authorization='Bearer camera-handoff-test-token-not-production'} -Body (@{action=$Action;leaseId=$Id}|ConvertTo-Json -Compress) -TimeoutSec 5
}
function Wait-Camera([string]$Id) {
    $until=(Get-Date).AddSeconds(8)
    do {
        $state=Camera 'acquire' $Id
        if ($state.ready -and $state.previewCount -gt 0) { Write-Output 'PASS all players acknowledged pause'; return }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $until)
    throw 'Player did not acknowledge its pause.'
}

$receiver = $null
$fixture = Join-Path $shared ('cache\camera-handoff-test-' + [guid]::NewGuid().ToString('N'))
$profile = Join-Path $fixture 'edge-profile'
$python = Join-Path $shared 'tools\Python\3.14.10\python.exe'
try {
    if (@(Get-NetTCPConnection -State Listen -LocalPort 8770,9236 -ErrorAction SilentlyContinue).Count) { throw 'A test port is in use.' }
    New-Item $fixture -ItemType Directory | Out-Null
    $config = Join-Path $fixture 'server.local.json'
    @{host='127.0.0.1';port=8770;apiToken='camera-handoff-test-token-not-production';dataDirectory='./data';delaySeconds=0} | ConvertTo-Json | Set-Content $config -Encoding UTF8
    $receiver = Start-Process $python -ArgumentList ('-u "{0}" --config "{1}"' -f (Join-Path $root 'server\server.py'),$config) -WorkingDirectory $root -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $root 'logs\camera-handoff-fixture.log') -RedirectStandardError (Join-Path $root 'logs\camera-handoff-fixture-errors.log')
    $until = (Get-Date).AddSeconds(15)
    do { try { $null = Invoke-RestMethod "$baseUrl/health" -TimeoutSec 1; break } catch { Start-Sleep -Milliseconds 200 } } while ((Get-Date) -lt $until)
    $edge = Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'
    $edgeProcess = Start-Process $edge -ArgumentList ('--headless=new --disable-gpu --no-first-run --no-default-browser-check --remote-debugging-port=9236 --user-data-dir="{0}" about:blank' -f $profile) -PassThru -RedirectStandardOutput (Join-Path $root 'logs\edge-camera-handoff-test.log') -RedirectStandardError (Join-Path $root 'logs\edge-camera-handoff-test-errors.log')
    $until = (Get-Date).AddSeconds(15); $target = $null
    do {
        try { $target = @((Invoke-RestMethod "$debugUrl/json/list" -TimeoutSec 1) | Where-Object type -eq 'page')[0] } catch { }
        if (!$target) { Start-Sleep -Milliseconds 200 }
    } until ($target -or (Get-Date) -gt $until)
    if (!$target) { throw 'Headless Edge did not start.' }
    $script:ws = [Net.WebSockets.ClientWebSocket]::new()
    $null = $script:ws.ConnectAsync([uri]$target.webSocketDebuggerUrl,[Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $null = Cdp 'Runtime.enable'; $null = Cdp 'Page.enable'
    $null = Cdp 'Emulation.setDeviceMetricsOverride' @{width=1440;height=1050;deviceScaleFactor=1;mobile=$false}
    $null = Cdp 'Storage.clearDataForOrigin' @{origin=$baseUrl;storageTypes='local_storage'}
    $null = Cdp 'Page.navigate' @{url=($baseUrl + '/system-live?device=192.168.100.61')}
    Wait-Js "document.getElementById('live-address')?.value==='192.168.100.61' && document.getElementById('live-clock').textContent.includes(':')" 'page initialized without starting a camera'
    if (@((Invoke-RestMethod "$debugUrl/json/list") | Where-Object type -eq 'page').Count -ne 1) { throw 'Page automatically opened a device connection.' }

    # Generate a harmless moving video with the browser itself. No camera, real
    # Portal credentials, external downloads or media runtime is needed.
    $script:clip=Js @'
new Promise(resolve=>{
  const canvas=document.createElement('canvas');canvas.width=320;canvas.height=180;
  const ctx=canvas.getContext('2d'),stream=canvas.captureStream(12),chunks=[];
  const recorder=new MediaRecorder(stream,{mimeType:'video/webm;codecs=vp8'});
  recorder.ondataavailable=e=>chunks.push(e.data);
  recorder.onstop=()=>{stream.getTracks().forEach(t=>t.stop());const reader=new FileReader();reader.onload=()=>resolve(reader.result.split(',')[1]);reader.readAsDataURL(new Blob(chunks,{type:'video/webm'}));};
  recorder.start();let frame=0;
  const timer=setInterval(()=>{ctx.fillStyle='#123d46';ctx.fillRect(0,0,320,180);ctx.fillStyle='#8ae9bc';ctx.fillRect(20+frame*8,65,40,40);ctx.font='16px sans-serif';ctx.fillText('SYNTHETIC LIVE TEST',30,35);if(++frame===16){clearInterval(timer);recorder.stop();}},80);
})
'@
    $null=Cdp 'Fetch.enable' @{patterns=@(@{urlPattern='https://192.168.100.61/api/holographic/stream/*';requestStage='Request'})}
    # This only adapts the fixture device address and loops the finite test clip.
    # All actual play/pause/load behavior, media requests and CSP are native.
    $null=Js @'
window.originalFetch=window.fetch.bind(window);
window.fetch=(url,options)=>{
  if(url==='/api/live-preview' && options?.body){const body=JSON.parse(options.body);if(body.deviceAddress)body.deviceAddress='127.0.0.1';options={...options,body:JSON.stringify(body)};}
  return originalFetch(url,options);
};
window.originalCreate=document.createElement.bind(document);
document.createElement=(tag,...rest)=>{const node=originalCreate(tag,...rest);if(tag==='video')node.loop=true;return node;};
window.opened=[];window.open=(url)=>{const child={closed:false,opener:window,location:{replace(value){child.url=value;}},close(){this.closed=true;}};opened.push(child);return child;};
'@
    $null=Js "document.getElementById('live-address').value='https://user:pass@192.168.100.61/';document.getElementById('live-start').click()"
    Wait-Js "!document.getElementById('live-error').hidden && !document.querySelector('video') && opened.length===0" 'URLs and credentials rejected'
    $script:holdMedia=$true
    $null=Js "document.getElementById('live-address').value='192.168.100.61';document.getElementById('live-start').click()" -UserGesture
    Wait-Js "document.querySelector('video')?.getAttribute('src')?.includes('live_med.mp4')" 'media request starts inline'
    if (!(Js "document.getElementById('live-player').dataset.state==='loading' && !document.getElementById('live-placeholder').hidden && !document.getElementById('live-status').textContent.includes('已收到')")) { throw 'UI claimed a picture before decoding video.' }
    $script:holdMedia=$false
    foreach ($request in $script:heldMedia) { Serve-Media $request }; $script:heldMedia.Clear()
    Wait-Js "document.getElementById('live-player').dataset.state==='playing' && document.querySelector('video').videoWidth===320 && document.querySelector('video').currentTime>0.1" 'real embedded video decodes and plays'
    if (!(Js "opened.length===0 && document.querySelector('video').muted && !document.querySelector('video').hasAttribute('crossorigin') && document.querySelector('video').src.includes('live_med.mp4?holo=true&pv=true&mic=false&loopback=false&streamSession=')")) { throw 'Inline stream parameters were incorrect or a popup opened.' }
    if (@((Invoke-RestMethod "$debugUrl/json/list") | Where-Object type -eq 'page').Count -ne 1) { throw 'A live popup was created.' }
    $null=Js "document.getElementById('live-fullscreen').click()" -UserGesture
    Wait-Js "document.fullscreenElement?.id==='live-player'" 'inline player enters fullscreen'
    $null=Js "document.getElementById('live-fullscreen').click()" -UserGesture
    Wait-Js "!document.fullscreenElement" 'fullscreen exits to the same page'
    Shot 'inline-live-desktop.png'
    Start-Sleep -Seconds 6
    $null=Js "window.previousVideo=document.querySelector('video')"
    $lease=[guid]::NewGuid().ToString('N')
    if ((Camera 'acquire' $lease).ready) { throw 'Camera was granted before stream unload.' }
    Wait-Camera $lease
    Wait-Js "document.getElementById('live-player').dataset.state==='capturing' && !document.querySelector('video') && !previousVideo.hasAttribute('src') && previousVideo.networkState===0 && !previousVideo.isConnected" 'capture ACK follows native video unload'
    Shot 'inline-live-capturing.png'
    $null=Camera 'release' $lease
    Wait-Js "document.getElementById('live-player').dataset.state==='playing' && document.querySelector('video')!==previousVideo && document.querySelector('video').videoWidth===320 && document.getElementById('live-status').textContent.includes('自动恢复') && opened.length===0" 'inline video resumes without another user gesture'
    # Restarting while the camera lease is held must not make a video request.
    $lease=[guid]::NewGuid().ToString('N');$null=Camera 'acquire' $lease;Wait-Camera $lease
    $requests=$script:mediaRequests.Count
    $null=Js "document.getElementById('live-quality').value='low';document.getElementById('live-holograms').checked=false;document.getElementById('live-start').click()"
    Wait-Js "document.getElementById('live-player').dataset.state==='capturing' && !document.querySelector('video')" 'restart during capture keeps camera free'
    if ($script:mediaRequests.Count -ne $requests) { throw 'Restart competed for camera during capture.' }
    $null=Camera 'release' $lease
    Wait-Js "document.getElementById('live-player').dataset.state==='playing' && document.querySelector('video').src.includes('live_low.mp4?holo=false')" 'resume applies selected quality'
    $lease=[guid]::NewGuid().ToString('N');$null=Camera 'acquire' $lease;Wait-Camera $lease
    $null=Js "document.getElementById('live-stop').click()"
    $null=Camera 'release' $lease
    Start-Sleep -Milliseconds 300
    Wait-Js "!document.querySelector('video') && document.getElementById('live-stop').disabled && opened.length===0" 'manual stop during capture cannot resurrect video'
    # Errors must release the source, not leave the hidden camera streaming.
    $script:failMedia=$true
    $null=Js "document.getElementById('live-start').click()"
    Wait-Js "document.getElementById('live-player').dataset.state==='error' && !document.querySelector('video') && document.getElementById('live-error').textContent.includes('登录')" 'authentication/media failure gives guidance and releases camera'
    $script:failMedia=$false
    $null=Js "document.getElementById('live-start').click()"
    Wait-Js "document.getElementById('live-player').dataset.state==='playing'" 'playback recovers after retry'
    $null=Js "window.previousVideo=document.querySelector('video');window.fetch=(url,options)=>url==='/api/live-preview' && JSON.parse(options.body).action==='poll'?Promise.reject(new Error('fixture disconnect')):window.originalFetch(url,options)"
    Wait-Js "!document.querySelector('video') && !previousVideo.hasAttribute('src') && document.getElementById('live-stop').disabled && !document.getElementById('live-error').hidden" 'coordination loss unloads video'
    $null=Js @'
window.fetch=(url,options)=>{if(url==='/api/live-preview' && options?.body){const body=JSON.parse(options.body);if(body.deviceAddress)body.deviceAddress='127.0.0.1';options={...options,body:JSON.stringify(body)};}return originalFetch(url,options);};
'@
    $null=Js "document.getElementById('live-start').click()"
    Wait-Js "document.getElementById('live-player').dataset.state==='playing'" 'final stream starts'
    $null=Cdp 'Emulation.setDeviceMetricsOverride' @{width=390;height=844;deviceScaleFactor=1;mobile=$true}
    Wait-Js "document.documentElement.scrollWidth<=innerWidth+1" 'mobile layout has no horizontal overflow'
    Shot 'inline-live-mobile.png'
    $null=Js "document.getElementById('live-login').click();window.previousVideo=document.querySelector('video');window.dispatchEvent(new Event('pagehide'))"
    Wait-Js "!document.querySelector('video') && !previousVideo.hasAttribute('src') && opened.length===1 && !opened[0].closed && opened[0].opener===null && opened[0].url==='https://192.168.100.61/'" 'page cleanup unloads video and preserves explicit login tab'
    $saved=Js "JSON.parse(localStorage.getItem('hololens-system-live'))"
    if (@($saved.PSObject.Properties.Name).Count -ne 3) { throw 'Unexpected stored preferences.' }
    if ($script:browserErrors.Count) { throw ($script:browserErrors -join "`n") }
    [ordered]@{passed=$true;verifiedAt=(Get-Date).ToString('o');fixtureUrl=$baseUrl;browserErrors=@($script:browserErrors);mediaRequests=$script:mediaRequests.Count;checks=@('no live popup','native video decoding','fullscreen enter/exit','loading vs actual picture','silent audio','no CORS attribute','native unload before ACK','autoplay resume without gesture','restart waits for lease','stop during pause','media failure cleanup','coordinator failure cleanup','page cleanup','no saved credentials','mobile layout');actualDeviceVideo='Synthetic moving WebM served by DevTools interception. Actual Portal login and HoloLens stream require a separate device check.'} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $root 'logs\inline-live-browser-smoke.json') -Encoding UTF8
    Write-Output 'INLINE_LIVE_BROWSER_SMOKE_PASSED'
} catch {
    try { Write-Output (Js "JSON.stringify({status:document.getElementById('live-status')?.textContent,error:document.getElementById('live-error')?.textContent})") } catch {}
    throw
} finally {
    if ($script:ws -and $script:ws.State -eq [Net.WebSockets.WebSocketState]::Open) { try { $null = Cdp 'Browser.close' } catch { } }
    if ($script:ws) { $script:ws.Dispose() }
    if ($edgeProcess -and !$edgeProcess.HasExited -and !$edgeProcess.WaitForExit(5000)) { $edgeProcess | Stop-Process -ErrorAction SilentlyContinue }
    if ($receiver -and !$receiver.HasExited) { $receiver | Stop-Process -ErrorAction SilentlyContinue }
    if (Test-Path $fixture) { Remove-Item $fixture -Recurse -Force }
}
