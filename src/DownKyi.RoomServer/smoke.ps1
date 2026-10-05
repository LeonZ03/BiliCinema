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
    if ($created.snapshot.memberCount -ne 1) { throw "A new room did not report its host as the only member." }

    $guest = Connect-Room
    Send-Json $guest ('{"type":"join","roomCode":"' + $created.roomCode + '"}')
    $joined = Read-Until $guest "welcome"
    if ($joined.role -ne "guest" -or $joined.clientId -eq $created.clientId) {
        throw "Second client did not receive an independent guest identity."
    }
    if ($joined.snapshot.memberCount -ne 2) { throw "A joined guest was not included in the room member count." }

    Send-Json $hostSocket '{"type":"chat","nickname":"房主","text":"晚上好"}'
    $hostChat = Read-Until $hostSocket "chat" { param($m) $m.text -eq "晚上好" }
    $guestChat = Read-Until $guest "chat" { param($m) $m.text -eq "晚上好" }
    foreach ($chat in @($hostChat, $guestChat)) {
        if ($chat.clientId -ne $created.clientId -or $chat.role -ne "host" -or
            $chat.nickname -ne "房主" -or $chat.sentAtUnixMs -le 0) {
            throw "Host chat did not carry server-verified identity and timestamp."
        }
    }

    Send-Json $guest '{"type":"chat","nickname":"访客","text":"你好"}'
    $hostGuestChat = Read-Until $hostSocket "chat" { param($m) $m.text -eq "你好" }
    $guestGuestChat = Read-Until $guest "chat" { param($m) $m.text -eq "你好" }
    foreach ($chat in @($hostGuestChat, $guestGuestChat)) {
        if ($chat.clientId -ne $joined.clientId -or $chat.role -ne "guest" -or
            $chat.nickname -ne "访客" -or $chat.sentAtUnixMs -le 0) {
            throw "Guest chat did not carry server-verified identity and timestamp."
        }
    }

    Send-Json $hostSocket '{"type":"chat","nickname":"房主","text":"第一行\n第二行"}'
    $lineFeedChat = Read-Until $hostSocket "chat" { param($m) $m.text -eq "第一行`n第二行" }
    $guestLineFeedChat = Read-Until $guest "chat" { param($m) $m.text -eq "第一行`n第二行" }
    if ($lineFeedChat.text -ne $guestLineFeedChat.text) { throw "Line feed chat was not broadcast consistently." }

    Send-Json $hostSocket '{"type":"chat","nickname":"房主","text":"第一行\r\n第二行"}'
    $normalizedLineEndings = Read-Until $hostSocket "chat" { param($m) $m.text -eq "第一行`n第二行" }
    if ($normalizedLineEndings.text -ne "第一行`n第二行") { throw "CRLF chat was not normalized to LF." }

    Send-Json $guest ('{"type":"chat","nickname":"访客","text":"伪造","clientId":"' + $created.clientId + '","role":"host"}')
    $spoofRejected = Read-Until $guest "error"
    if ($spoofRejected.code -ne "unexpected_field") { throw "Chat sender identity could be supplied by a client." }
    Send-Json $guest ('{"type":"chat","nickname":"访客","text":"' + ("x" * 501) + '"}')
    $oversizedChat = Read-Until $guest "error"
    if ($oversizedChat.code -ne "invalid_string") { throw "Overlong chat text was accepted." }
    Send-Json $guest '{"type":"chat","nickname":"访客","text":"   "}'
    $blankChat = Read-Until $guest "error"
    if ($blankChat.code -ne "invalid_chat") { throw "Blank chat text was accepted." }
    Send-Json $guest '{"type":"chat","nickname":"访客","text":"tab\t拒绝"}'
    $controlChat = Read-Until $guest "error"
    if ($controlChat.code -ne "invalid_string") { throw "Non-newline control character in chat was accepted." }
    Send-Json $guest '{"type":"chat","nickname":"访\t客","text":"昵称控制字符拒绝"}'
    $controlNickname = Read-Until $guest "error"
    if ($controlNickname.code -ne "invalid_string") { throw "Control character in chat nickname was accepted." }

    Send-Json $guest '{"type":"login_qr","loginUrl":"https://passport.bilibili.com/obsolete"}'
    $unsupported = Read-Until $guest 'error'
    if ($unsupported.code -ne 'unknown_type') { throw 'Obsolete login relay command was accepted.' }


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
    if ($offline.snapshot.memberCount -ne 1) { throw "A disconnected guest was counted as present." }
    $guest = Connect-Room
    Send-Json $guest ('{"type":"join","roomCode":"' + $created.roomCode + '","clientId":"' + $joined.clientId + '"}')
    $rejoined = Read-Until $guest "welcome"
    if ($rejoined.snapshot.playing -eq $true -or $rejoined.snapshot.guest.ready -eq $true) {
        throw "Reconnect did not pause and clear readiness."
    }
    if ($rejoined.PSObject.Properties.Name -contains "history") {
        throw "Welcome unexpectedly contained persisted chat history."
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
    $soloPlaying = Read-Until $hostSocket "snapshot" { param($m) $m.snapshot.playing -eq $true }
    if ($soloPlaying.snapshot.memberCount -ne 1) { throw "A solo host room did not report one member while playing." }
    $guest = Connect-Room
    Send-Json $guest ('{"type":"join","roomCode":"' + $newRoom.roomCode + '"}')
    $newGuest = Read-Until $guest "welcome"
    if ($newGuest.snapshot.memberCount -ne 2 -or $newGuest.snapshot.playing -or
        -not $newGuest.snapshot.waitingForReady) {
        throw "A guest joining a solo playback did not pause and wait for guest readiness."
    }
    Send-Json $guest '{"type":"ready","ready":true}'
    $startedTogether = Read-Until $hostSocket "snapshot" { param($m) $m.snapshot.playing -eq $true }
    if ($startedTogether.snapshot.memberCount -ne 2) { throw "Playback did not resume with both members counted." }
    $guest.Dispose()
    $guest = $null
    $continuedAlone = Read-Until $hostSocket "snapshot" {
        param($m) $m.snapshot.memberCount -eq 1 -and $m.snapshot.playing -eq $true
    }
    if ($continuedAlone.snapshot.waitingForReady) { throw "Host playback paused after the guest left." }
    Send-Json $hostSocket '{"type":"close"}'
    Write-Host "Room server smoke passed: create/join, member count, bidirectional room chat, server-verified chat identity and timestamps, LF chat, CRLF normalization, chat input validation, sender spoof rejection, solo playback, guest-join resynchronization, obsolete login relay rejection, host authority, media URL rejection, ready/play, seek, buffering recovery, reconnect without chat history, and close."
} finally {
    if ($null -ne $guest) { $guest.Dispose() }
    if ($null -ne $hostSocket) { $hostSocket.Dispose() }
    if ($null -ne $server -and -not $server.HasExited) { Stop-Process -Id $server.Id }
}
