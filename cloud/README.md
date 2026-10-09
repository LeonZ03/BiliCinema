# BiliCinema Cloud Router

This Worker is the fixed-domain gateway for BiliCinema rooms. It routes each
room WebSocket to the Named Tunnel owned by the host computer while leaving
room playback state in the local `DownKyi.RoomServer` process.

The Worker requires these secrets, configured with Wrangler and never shipped
inside BiliCinema.exe:

- `CLOUDFLARE_API_TOKEN`: account-scoped Tunnel edit and zone DNS edit token.
- `CLOUDFLARE_ACCOUNT_ID`: the Cloudflare account containing the Tunnel.
- `CLOUDFLARE_ZONE_ID`: the `leonz03.dpdns.org` zone ID.

The Worker creates one remotely managed Tunnel per active host session. Its
public origin hostname uses `bilicinema-h-<session>.leonz03.dpdns.org`
and is never placed in the invitation URL. The main invitation remains under
`bilicinema.leonz03.dpdns.org`.

Hostnames stay one level below the zone so the free Universal SSL certificate
covers them without requiring Advanced Certificate Manager.

Deploy only after verifying the Zone and permissions in Cloudflare. The
Worker does not read or store Bilibili cookies, media URLs, video or audio.
