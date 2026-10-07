# DownKyi private room server

This .NET 10 service relays room selection and playback control state. Each desktop client resolves and plays its own Bilibili media. The server never receives cookies, account IDs, signed media URLs, video, or audio. Room state is held in memory and is lost on restart.

## Start locally

```powershell
dotnet run --project src/DownKyi.RoomServer/DownKyi.RoomServer.csproj -c Release
```

The BiliCinema EXE starts this service in-process when the host creates a room, so normal users do not need to start it separately. The default endpoint is `ws://127.0.0.1:5077/ws`; `http://127.0.0.1:5077/health` is a basic process check. For standalone development, override the listening address with `RoomServer__ListenUrl` or `RoomServer:ListenUrl` in configuration. The local smoke test is:

```powershell
pwsh -File src/DownKyi.RoomServer/smoke.ps1
```

## Private internet deployment

For temporary remote viewing, the BiliCinema EXE creates the local room first, then starts its bundled `cloudflared` client and verifies the public WebSocket upgrade used by guests. The host connects locally while the invitation route is preparing; a failed route can be retried without recreating the local room. Ending or leaving a hosted room closes the owned local service and Tunnel; application shutdown does the same. Creation waits for prior teardown and always starts a fresh Tunnel. Old invitations cannot join the new room. Quick Tunnels are intended for temporary use and have no uptime guarantee.

For a stable hostname, set up a named Cloudflare Tunnel or run the service on a host you control with a TLS reverse proxy. Keep the default loopback listener. For example, a Caddy site can use:

```caddyfile
room.example.com {
    reverse_proxy 127.0.0.1:5077
}
```

Replace the domain with your own, configure DNS and the host firewall for HTTPS, then use `wss://room.example.com/ws` in all clients. The service rejects plain WebSocket connections from non-loopback peers. It also rejects browser `Origin` headers; the intended clients are the DownKyi desktop applications. Do not expose the local HTTP listener directly. No media ports or player IPC need to be publicly reachable.

The server keeps at most 256 rooms, with at most five members per room (one host and four guests). A sixth member is rejected with `room_full`. A disconnected host has two minutes to reconnect. Guest credentials are retained for up to five minutes while their seat is available; a new guest can take the oldest disconnected seat if all four slots are allocated. The host leaving or closing a room ends it immediately. A service restart ends all rooms. Invitation codes and client IDs contain 128 random bits each; share only the invitation code. Keep the returned `clientId` private on its own client and use it to reclaim that slot on reconnect.

## Wire protocol (JSON over WebSocket)

Messages are UTF-8 JSON objects, at most 4096 bytes each. The first message must be `create` or `join`:

```json
{"type":"create"}
{"type":"join","roomCode":"<22-character invitation code>"}
{"type":"join","roomCode":"<same code>","clientId":"<this client's saved 22-character ID>"}
```

The server responds with a private welcome message:

```json
{"type":"welcome","roomCode":"...","clientId":"...","memberId":"<public member ID>","role":"host","snapshot":{"version":1,"media":null,"positionSeconds":0,"playing":false,"rate":1,"serverTimeUnixMs":1760000000000,"waitingForReady":false,"memberCount":1,"capacity":5,"host":{"memberId":"<public member ID>","online":true,"ready":false,"buffering":false},"guests":[]}}
```

`role` is `host` or `guest`. Each member has a public `memberId`, distinct from the private `clientId` reconnect credential. Snapshots contain `host` and a `guests` array; each member view includes `memberId`, `online`, `ready` and `buffering`. Clients must look up their own `memberId` when reporting readiness. `memberCount` counts online members, including the host; `capacity` is five. All participants must upgrade together to the multi-member protocol introduced in BiliCinema 1.3.0. Every state change broadcasts a `snapshot` message containing the same nested snapshot object. `version` increases monotonically within a room. The client should ignore older or repeated versions and discard commands associated with an earlier `media` after a selection change. `serverTimeUnixMs` is the server's send time; `positionSeconds` is the media position at that time. While `playing` is true, estimate the current position from elapsed time and `rate`; use ping/pong to estimate round-trip delay and clock offset. This protocol sends no actual media address.

Only the host may send the authority commands:

```json
{"type":"select","media":{"episodeId":251429}}
{"type":"play"}
{"type":"pause"}
{"type":"seek","positionSeconds":120.5}
{"type":"rate","rate":1.25}
{"type":"sync","media":{"episodeId":12345},"positionSeconds":120.5,"rate":1,"playing":false}
{"type":"close"}
```

The `media` object accepts positive `episodeId`, `aid`, or `cid` integers and/or a 12-character `bvid` starting with `BV`. At least one identifier is required. No URL, title, account name, or extra fields are accepted. Seek position is 0–86400 seconds; rate is 0.25–3. A selection resets every member's ready state and pauses the room. The host may send `play` before all peers are ready: `waitingForReady` then becomes true, and the room starts when the host and every online guest are ready and not buffering. A member reporting buffering pauses the room; playback resumes when all online members are ready and clear buffering. A joining guest also pauses playback until ready. Guest disconnection or departure removes that guest from the readiness barrier, allowing remaining ready members to continue. Host disconnection pauses the room and clears the pending start; the host explicitly starts again after reconnecting.

`sync` atomically publishes the host player's actual position, rate and play
intent after page initialization/history restoration, or on a manual sync. Its
media identity must equal the selected identity; stale-media samples and guest
commands are rejected. The snapshot's `syncRevision` increases for each accepted
sample so guests align immediately even when drift is below the normal tolerance.
Readiness and buffering still gate playback; synchronization cannot bypass them.

All members may send:

```json
{"type":"ready","ready":true}
{"type":"buffering","buffering":true}
{"type":"ping","clientTimeUnixMs":1760000000000}
{"type":"leave"}
```

All members may also send a room chat message. Nicknames are 1–24 characters and reject control characters. Message text is 1–500 characters, allows LF newlines (CRLF is normalized to LF), and rejects other control characters and empty/whitespace-only values. The complete UTF-8 WebSocket message remains limited to 4096 bytes. Clients must send only the nickname and plain text; `memberId`, role, and time are assigned by the server from the authenticated room connection:

```json
{"type":"chat","nickname":"MovieFan","text":"Ready when you are!"}
```

The server broadcasts the same transient event to currently connected members, including the sender:

```json
{"type":"chat","memberId":"<public member ID>","role":"guest","nickname":"MovieFan","text":"Ready when you are!","sentAtUnixMs":1760000000000}
```

Chat messages are not added to room snapshots, replayed in `welcome`, or written to disk. The server accepts chat only from the connection's current room member and broadcasts only to that room's online members. Chat carries no login credentials, account identifiers, media identifiers, stream URLs, or media content. Clients should display `text` as plain text, not interpret it as markup.

`ready:false` and `buffering:false` clear those flags. Ping may include a short `nonce`; pong echoes `clientTimeUnixMs` and the optional `nonce`, and includes `serverTimeUnixMs`. A guest `leave` frees their slot; host `leave` closes the room. The service emits `{"type":"left"}` to a departing guest or `{"type":"closed"}` when the room ends. Invalid commands produce `{"type":"error","code":"..."}`; a guest sending host commands receives `host_only`. The client should treat closure or transport loss as a visible room state, preserve its own playback position, and reconnect only with its own `clientId` while the relevant grace period remains.

The server does not synchronize the local player's volume, fullscreen mode, quality, or signed stream URL. Each side selects quality and resolves media locally. After receiving a remote snapshot, clients must apply it without re-emitting the resulting player event as a new host command.
