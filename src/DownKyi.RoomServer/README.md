# DownKyi private room server

This .NET 10 service relays **control state only**. Each desktop client resolves and plays its own Bilibili media. The server never receives cookies, account IDs, signed media URLs, video, or audio. Room state is held in memory and is lost on restart.

## Start locally

```powershell
dotnet run --project src/DownKyi.RoomServer/DownKyi.RoomServer.csproj -c Release
```

The default endpoint is `ws://127.0.0.1:5077/ws`; `http://127.0.0.1:5077/health` is a basic process check. Override the listening address with `RoomServer__ListenUrl` or `RoomServer:ListenUrl` in configuration. The local smoke test is:

```powershell
pwsh -File src/DownKyi.RoomServer/smoke.ps1
```

## Private internet deployment

For temporary remote viewing, run `Start-Room-Server.cmd` on the host PC and install `cloudflared` from the [official downloads page](https://developers.cloudflare.com/tunnel/downloads/). In the watch window, creating a room from the default local address starts a Quick Tunnel, waits for its public health check, and puts its `wss://…trycloudflare.com/ws` address into the invitation. The guest needs only the invitation. The server stays bound to loopback; closing the host watch window ends its Tunnel, and a new Tunnel gets a new address. Quick Tunnels are intended for temporary use and have no uptime guarantee.

For a stable hostname, set up a named Cloudflare Tunnel or run the service on a host you control with a TLS reverse proxy. Keep the default loopback listener. For example, a Caddy site can use:

```caddyfile
room.example.com {
    reverse_proxy 127.0.0.1:5077
}
```

Replace the domain with your own, configure DNS and the host firewall for HTTPS, then use `wss://room.example.com/ws` in both clients. The service rejects plain WebSocket connections from non-loopback peers. It also rejects browser `Origin` headers; the intended clients are the DownKyi desktop applications. Do not expose the local HTTP listener directly. No media ports or player IPC need to be publicly reachable.

The server keeps at most 256 rooms, with two members per room. A disconnected host has two minutes to reconnect; a disconnected guest keeps their slot for five minutes. The host leaving or closing a room ends it immediately. A service restart ends all rooms. Invitation codes and client IDs contain 128 random bits each; share only the invitation code. Keep the returned `clientId` private on its own client and use it to reclaim that slot on reconnect.

## Wire protocol (JSON over WebSocket)

Messages are UTF-8 JSON objects, at most 4096 bytes each. The first message must be `create` or `join`:

```json
{"type":"create"}
{"type":"join","roomCode":"<22-character invitation code>"}
{"type":"join","roomCode":"<same code>","clientId":"<this client's saved 22-character ID>"}
```

The server responds with a private welcome message:

```json
{"type":"welcome","roomCode":"...","clientId":"...","role":"host","snapshot":{"version":1,"media":null,"positionSeconds":0,"playing":false,"rate":1,"serverTimeUnixMs":1760000000000,"waitingForReady":false,"host":{"online":true,"ready":false,"buffering":false},"guest":null}}
```

`role` is `host` or `guest`. Every state change broadcasts a `snapshot` message containing the same nested snapshot object. `version` increases monotonically within a room. The client should ignore older or repeated versions and discard commands associated with an earlier `media` after a selection change. `serverTimeUnixMs` is the server's send time; `positionSeconds` is the media position at that time. While `playing` is true, estimate the current position from elapsed time and `rate`; use ping/pong to estimate round-trip delay and clock offset. This protocol sends no actual media address.

Only the host may send the authority commands:

```json
{"type":"select","media":{"episodeId":251429}}
{"type":"play"}
{"type":"pause"}
{"type":"seek","positionSeconds":120.5}
{"type":"rate","rate":1.25}
{"type":"close"}
```

The `media` object accepts positive `episodeId`, `aid`, or `cid` integers and/or a 12-character `bvid` starting with `BV`. At least one identifier is required. No URL, title, account name, or extra fields are accepted. Seek position is 0–86400 seconds; rate is 0.25–3. A selection resets both members' ready state and pauses the room. The host may send `play` before both peers are ready: `waitingForReady` then becomes true, and the room starts when both are online, ready, and not buffering. A peer reporting buffering pauses both; playback resumes when both clear buffering and remain ready. Disconnection pauses the room and clears the pending start, so the host explicitly starts again after reconnection.

Both members may send:

```json
{"type":"ready","ready":true}
{"type":"buffering","buffering":true}
{"type":"ping","clientTimeUnixMs":1760000000000}
{"type":"leave"}
```

`ready:false` and `buffering:false` clear those flags. Ping may include a short `nonce`; pong echoes `clientTimeUnixMs` and the optional `nonce`, and includes `serverTimeUnixMs`. A guest `leave` frees their slot; host `leave` closes the room. The service emits `{"type":"left"}` to a departing guest or `{"type":"closed"}` when the room ends. Invalid commands produce `{"type":"error","code":"..."}`; a guest sending host commands receives `host_only`. The client should treat closure or transport loss as a visible room state, preserve its own playback position, and reconnect only with its own `clientId` while the relevant grace period remains.

The server does not synchronize the local player's volume, fullscreen mode, quality, or signed stream URL. Each side selects quality and resolves media locally. After receiving a remote snapshot, clients must apply it without re-emitting the resulting player event as a new host command.
