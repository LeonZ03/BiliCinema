# BiliCinema Android

Native Java Android client (Android 8+). The device's Android System WebView handles Bilibili login and media playback; cookies never enter the room service. The interface follows the existing cinema dark theme, uses native controls with 48dp minimum targets, and retains the player during rotation.

The room endpoint is `wss://bilicinema.leonz03.dpdns.org/v2/ws`. Creation connects without a room query; joining uses `?room=...`. JSON follows `src/DownKyi.RoomServer/README.md`: one host and four guests, host controls, per-member ready/buffering, transient chat, private reconnect credentials and a bounded reconnect window. Only public media identifiers are sent to the room. On transport or player errors the app shows an actionable message. TLS errors cancel the load; WebView renderer loss is handled with a refresh action.

Enter a BV/av identifier (including `?p=N`) or an `ep` episode link. The official Bilibili view/season API resolves the complete aid/bvid/cid/episode identity locally. Guests map the selected CID back to the exact page; unresolved or inconsistent media never becomes ready. The API refuses redirects so its manually supplied local cookie cannot follow a redirect to another origin.

`MediaSession` owns resolution generations, expected media, actual player verification and readiness. Its states are empty, resolving, loading, verified, changed and error. The WebView bridge uses AndroidX WebKit's origin-restricted message listener and requires the main frame. It accepts only exact HTTPS `www.bilibili.com`/`m.bilibili.com` origins, verifies the page and a positive matching live player CID, and revokes readiness on navigation or a CID change. Older documents and asynchronous resolver results retain their original generation and cannot publish into a new selection. Guest-side unexpected changes restore the selected media through the resolver; the host can explicitly select the new page or restore the room movie. Devices whose WebView lacks secure web messaging receive an update prompt and do not claim readiness.

The app opens Bilibili's official desktop HTML5 page and applies room snapshots without echoing programmatic controls. Bilibili's page compatibility, QR login, premium entitlement, device audio/video and background behavior must be verified on actual Android devices before a stable release. No Android device was attached during initial local compilation.

The WebView currently sends a fixed Windows/Chrome desktop user agent to request Bilibili's desktop HTML5 player. This changes the requested page; it does not add Windows codecs, DRM support or Bilibili app permissions to Android. Playback remains subject to the installed Android System WebView, the device's codecs/DRM capabilities, the logged-in account's membership, content rights and regional restrictions. The client has no separate native media/DRM playback backend. Some videos may require Bilibili's own app or fail in WebView, including premium or protected content. Playback of such content has not been validated on a phone. Login, membership and room transport tests do not establish actual media playback support.

Room readiness also requires the official page's live `player.getCid()` or `bilibiliPlayer.getCid()` API to identify the selected part. If Bilibili changes that API or the player does not expose it on a device, the client keeps readiness false and offers refresh/restore instead of claiming to be synchronized.

## Build and signing

Use JDK 17, Node.js 18+ (for the built-in test runner), Android SDK 35 and the checked-in Gradle wrapper:

```powershell
pwsh -File script/build-android.ps1
```

The script runs the player script behavior tests, release unit tests, release lint and release assembly, then writes `artifacts/releases/<version>/android/BiliCinema-<version>-android-universal.apk` and `SHA256SUMS.txt`. `-SkipChecks` skips the Node tests, unit tests and lint together and is intended only for local iteration. The version and Android version code derive from root `version.txt`.

Both Windows and Linux use the same checked-in wrapper JAR and properties. The Gradle 8.9 binary distribution is pinned to the official SHA-256 `d725d707bfabd4dfdc958c624003b3c80accc03f7037b5122c4b1d0ef15cecab`; the wrapper verifies the downloaded ZIP against it before extraction. Linux CI can run `chmod +x android/gradlew` followed by the commands below and must preserve this checksum property.

For CI set `BILICINEMA_KEYSTORE`, `BILICINEMA_KEY_ALIAS`, `BILICINEMA_KEYSTORE_PASSWORD` and `BILICINEMA_KEY_PASSWORD`. Local builds with no supplied identity create and reuse an ignored key and credential file under `android/.keys`. Back up this directory securely and import that same key into the repository's signing secrets before publishing the first APK. Replacing it prevents installed APKs from updating. Keys and passwords must never be committed, printed or included in artifacts.

Updates use GitHub's latest stable `bilicinema-v<version>` release, the exact Android asset name above and `SHA256SUMS.txt`. The app verifies SHA-256, package name, newer version and the installed signing certificate before invoking Android's system installer. `UpdateJournal` synchronously commits both owned output names, hash, version and phase in private SharedPreferences before the first write. Interrupted downloads are removed, interrupted commits and installer launches are revalidated on startup, and completed installations clean up their recorded package. Android requires the user to grant installation permission and confirm installation. Failed downloads leave the installed app intact.

## Checks

```powershell
android/gradlew.bat -p android :app:testReleaseUnitTest :app:lintRelease :app:assembleRelease
node --test android/tests/player.test.mjs
```

Unit regressions cover exact cross-platform media identity, CID/page verification, media session transitions, old generations, readiness withdrawal, buffering, update ownership and crash recovery, numeric version order and exact checksum matching. The player script regressions cover live CID changes, document generations and remote control echo suppression. Device acceptance should cover phone/tablet portrait and landscape, large text, actual login and premium content, five devices, host control, buffering, Wi-Fi loss/recovery, room closure, and updating an installed signed APK.
