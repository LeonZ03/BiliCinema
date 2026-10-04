param([string]$DotnetPath = "dotnet", [string]$Configuration = "Release", [switch]$SkipBuild)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "DownKyi.RoomServer.csproj"
if (-not $SkipBuild) {
    & $DotnetPath build $project -c $Configuration --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Room server build failed." }
}

$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()

$oldListenUrl = [Environment]::GetEnvironmentVariable("RoomServer__ListenUrl", "Process")
[Environment]::SetEnvironmentVariable("RoomServer__ListenUrl", "http://127.0.0.1:$port", "Process")
$dll = Join-Path $PSScriptRoot "bin/$Configuration/net10.0/DownKyi.RoomServer.dll"
$server = Start-Process -FilePath $DotnetPath -ArgumentList @($dll) -PassThru -WindowStyle Hidden
[Environment]::SetEnvironmentVariable("RoomServer__ListenUrl", $oldListenUrl, "Process")

function Send-Json([Net.WebSockets.ClientWebSocket]$socket, [string]$json) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $segment = [ArraySegment[byte]]::new($bytes)
    [void]$socket.SendAsync($segment, [Net.WebSockets.WebSocketMessageType]::Text, $true,
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
}

function Read-Json([Net.WebSockets.ClientWebSocket]$socket) {
    $bytes = [byte[]]::new(4096)
    $segment = [ArraySegment[byte]]::new($bytes)
    $timeout = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(5))
    try {
        $result = $socket.ReceiveAsync($segment, $timeout.Token).GetAwaiter().GetResult()
        if ($result.MessageType -ne [Net.WebSockets.WebSocketMessageType]::Text) {
            throw "Unexpected WebSocket close or binary message."
        }
        if (-not $result.EndOfMessage) { throw "Unexpected fragmented server message." }
        [Text.Encoding]::UTF8.GetString($bytes, 0, $result.Count) | ConvertFrom-Json
    } finally {
        $timeout.Dispose()
    }
}

function Read-Until([Net.WebSockets.ClientWebSocket]$socket, [string]$type, [scriptblock]$condition = { $true }) {
    for ($i = 0; $i -lt 30; $i++) {
        $message = Read-Json $socket
        if ($message.type -eq $type -and (& $condition $message)) { return $message }
    }
    throw "Expected room message was not received."
}

function Connect-Room {
    $socket = [Net.WebSockets.ClientWebSocket]::new()
    [void]$socket.ConnectAsync([Uri]::new("ws://127.0.0.1:$port/ws"),
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    return $socket
}

$hostSocket = $null
$guest = $null
try {
    for ($i = 0; $i -lt 50; $i++) {
        try {
            $health = Invoke-WebRequest -Uri "http://127.0.0.1:$port/health" -TimeoutSec 1
            if ($health.StatusCode -eq 200) { break }
        } catch { Start-Sleep -Milliseconds 100 }
    }

    $hostSocket = Connect-Room
    Send-Json $hostSocket '{"type":"create"}'
    $created = Read-Until $hostSocket "welcome"
    if ($created.roomCode.Length -ne 22 -or $created.clientId.Length -ne 22) {
        throw "Create did not return high-entropy identifiers."
    }

    $guest = Connect-Room
    Send-Json $guest ('{"type":"join","roomCode":"' + $created.roomCode + '"}')
    $joined = Read-Until $guest "welcome"
    if ($joined.role -ne "guest" -or $joined.clientId -eq $created.clientId) {
        throw "Second client did not receive an independent guest identity."
    }

    $qrUrl = 'https://passport.bilibili.com/h5-app/passport/login/scan?key=smoke'
    Send-Json $guest ('{"type":"login_qr","loginUrl":"' + $qrUrl + '"}')
    $forwardedQr = Read-Until $hostSocket 'login_qr'
    if ($forwardedQr.loginUrl -ne $qrUrl) { throw 'Guest QR was not relayed to the host.' }
    Send-Json $guest '{"type":"login_done"}'
    [void](Read-Until $hostSocket 'login_done')
    Send-Json $guest '{"type":"login_qr","loginUrl":"https://example.invalid/qr"}'
    $badQr = Read-Until $guest 'error'
    if ($badQr.code -ne 'invalid_login_qr') { throw 'External QR URL was accepted.' }
    Send-Json $hostSocket ('{"type":"login_qr","loginUrl":"' + $qrUrl + '"}')
    $hostQr = Read-Until $hostSocket 'error'
    if ($hostQr.code -ne 'guest_only') { throw 'Host QR relay was accepted.' }

    Send-Json $guest '{"type":"pause"}'
    $permission = Read-Until $guest "error"
    if ($permission.code -ne "host_only") { throw "Guest authority was not enforced." }

    Send-Json $hostSocket '{"type":"select","media":{"episodeId":251429,"url":"https://example.invalid/video"}}'
    $urlRejected = Read-Until $hostSocket "error"
    if ($urlRejected.code -ne "unexpected_field") { throw "Media URLs were not rejected." }

    Send-Json $hostSocket '{"type":"select","media":{"episodeId":251429}}'
    $selected = Read-Until $hostSocket "snapshot" { param($m) $m.snapshot.media.episodeId -eq 251429 }
    Send-Json $hostSocket '{"type":"ready","ready":true}'
    Send-Json $guest '{"type":"ready","ready":true}'
    Send-Json $hostSocket '{"type":"play"}'
    $playing = Read-Until $hostSocket "snapshot" { param($m) $m.snapshot.playing -eq $true }
    if ($playing.snapshot.version -le $selected.snapshot.version) { throw "State version did not increase." }

    Send-Json $hostSocket '{"type":"seek","positionSeconds":120.5}'
    $seeked = Read-Until $hostSocket "snapshot" { param($m) $m.snapshot.positionSeconds -ge 120.5 }
    Send-Json $guest '{"type":"buffering","buffering":true}'
    $waiting = Read-Until $hostSocket "snapshot" { param($m) $m.snapshot.waitingForReady -eq $true -and $m.snapshot.playing -eq $false }
    Send-Json $guest '{"type":"buffering","buffering":false}'
    $resumed = Read-Until $hostSocket "snapshot" { param($m) $m.snapshot.playing -eq $true -and $m.snapshot.version -gt $waiting.snapshot.version }

    $guest.Dispose()
    $guest = $null
    $offline = Read-Until $hostSocket "snapshot" { param($m) $m.snapshot.guest.online -eq $false }
    $guest = Connect-Room
    Send-Json $guest ('{"type":"join","roomCode":"' + $created.roomCode + '","clientId":"' + $joined.clientId + '"}')
    $rejoined = Read-Until $guest "welcome"
    if ($rejoined.snapshot.playing -eq $true -or $rejoined.snapshot.guest.ready -eq $true) {
        throw "Reconnect did not pause and clear readiness."
    }

    Send-Json $hostSocket '{"type":"close"}'
    $closed = Read-Until $guest "closed"
    $guest.Dispose()
    $guest = $null
    $hostSocket.Dispose()
    $hostSocket = $null

    $hostSocket = Connect-Room
    Send-Json $hostSocket '{"type":"create"}'
    $newRoom = Read-Until $hostSocket "welcome"
    Send-Json $hostSocket '{"type":"select","media":{"episodeId":251429}}'
    Send-Json $hostSocket '{"type":"ready","ready":true}'
    Send-Json $hostSocket '{"type":"play"}'
    $pending = Read-Until $hostSocket "snapshot" { param($m) $m.snapshot.waitingForReady -eq $true }
    $guest = Connect-Room
    Send-Json $guest ('{"type":"join","roomCode":"' + $newRoom.roomCode + '"}')
    $newGuest = Read-Until $guest "welcome"
    Send-Json $guest '{"type":"ready","ready":true}'
    $startedTogether = Read-Until $hostSocket "snapshot" { param($m) $m.snapshot.playing -eq $true }
    Send-Json $hostSocket '{"type":"close"}'
    Write-Host "Room server smoke passed: create/join, QR relay validation, host authority, media URL rejection, ready/play, seek, buffering recovery, reconnect, close, and deferred start."
} finally {
    if ($null -ne $guest) { $guest.Dispose() }
    if ($null -ne $hostSocket) { $hostSocket.Dispose() }
    if ($null -ne $server -and -not $server.HasExited) { Stop-Process -Id $server.Id }
}
