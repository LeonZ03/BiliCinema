# BiliCinema Cloud Rooms

The fixed domain serves v2 rooms at `/v2/ws`. Each room is a SQLite Durable Object;
its hibernating WebSockets retain private membership attachments while inactive.
Playback advances from a persisted position/time/rate anchor, without a ticking
server loop or per-second writes. Devices fetch video directly from Bilibili.
The Worker never receives Bilibili cookies or playback URLs.

Connect without a room query and send `{"type":"create"}`. The welcome returns a
128-bit invitation code and a private reconnect `clientId`. Join with
`/v2/ws?room=CODE` and `{"type":"join","roomCode":"CODE"}`; reconnect includes the
saved `clientId`. Public snapshots/chat use a distinct `memberId`.

One host and four guests share host-only selection/play/pause/seek/rate controls.
The host owns the timeline: only an unready or buffering host pauses the room,
preserving play intent until the host can resume. An explicit host pause clears
that intent. Guest joins, reconnects, preparation, buffering, backgrounding,
disconnections and departures do not pause anyone else or reset the host's
timeline. Guests report their real readiness and align to the current host
position when they recover.
Host loss pauses playback, clears pending play intent and retains a two-minute
reconnect window; the host explicitly starts again after reconnecting.
Guest credentials expire after five minutes offline; an offline
guest may yield its seat to a new member when full. Host leave/close ends the room;
an alarm expires abandoned rooms and the 24-hour maximum lifetime. Chat is never
persisted or replayed. Only public media identifiers enter selection messages.

The service accepts text JSON up to 4096 UTF-8 bytes with command-specific fields,
limits messages per socket and applies shared creation admission limits. Per-IP
admission keys are daily salted hashes, not raw IP addresses. Limits are a cost
containment boundary, not a guarantee of availability under abuse or exhausted
account quotas.

`npm ci && npm test` runs legacy routing regressions and real Miniflare/workerd
SQLite + WebSocket tests, including restart recovery and expiration. See
[deployment](DEPLOYMENT.md) for automatic deployment and configuration.

The legacy `/ws` and `/api/v1/hosts` endpoints remain available for released v1
clients. They route to a host-owned Named Tunnel and require the legacy Tunnel/DNS
management secrets. New v2 clients do not use these endpoints or require those
management secrets. The original `router-v1` migration must remain immutable;
`router-v2` adds the separate cloud room class.
