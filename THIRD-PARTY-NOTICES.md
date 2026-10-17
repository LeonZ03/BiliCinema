
# Third-Party Notices

## Android client

The Android client uses AndroidX WebKit 1.12.1 and its AndroidX dependencies
([AndroidX source](https://android.googlesource.com/platform/frameworks/support/)),
OkHttp 4.12.0 ([source and license](https://github.com/square/okhttp/tree/parent-4.12.0)),
and their Okio/Kotlin runtime dependencies. These libraries use the Apache License
2.0; their upstream distributions contain the corresponding copyright notices.
QR images are generated locally with ZXing core 3.5.4
([source and Apache 2.0 license](https://github.com/zxing/zxing/tree/zxing-3.5.4)).
The checked-in Gradle wrapper is a build tool from the
[Gradle project](https://github.com/gradle/gradle/tree/v8.9.0), also under Apache 2.0.
Android System WebView and Bilibili's player are supplied by the device/platform
and Bilibili respectively, rather than bundled as client source.

## Bundled Windows tools

The Windows single-file build embeds cloudflared 2026.9.0 from
<https://github.com/cloudflare/cloudflared/releases/tag/2026.9.0>, aria2 1.37.0
from <https://github.com/crazysmile-PhD/downkyi-aria2-static-build>, and the
FFmpeg build identified in `script/assets/external-assets.json` from
<https://github.com/BtbN/FFmpeg-Builds>. Build inputs are verified with SHA-256
before embedding. aria2 is prepared at startup; FFmpeg and cloudflared are
extracted when their features are used.
The FFmpeg build includes its GPLv3 license text in the source asset archive;
the BiliCinema repository's GPLv3 text is in `LICENSE`. Consult the upstream
projects for their source code and complete license notices.

## Avalonia.Controls.WebView

The watch window embeds Bilibili's official player through Avalonia.Controls.WebView
12.1.0 (Windows WebView2). Copyright 2019-2026 AvaloniaUI OÜ. MIT License.
Source: <https://github.com/AvaloniaUI/Avalonia.Controls.WebView>

## mpv (legacy optional Windows watch package)

The separately launched mpv player is not part of the DownKyi source tree.
Older local Windows watch builds used the mpv project's first-party CI full build
`v0.41.0-dev-g413ff0b1c` from
<https://github.com/mpv-player/mpv/releases/tag/git-release>.
It is distributed under GPLv3; its `LICENSE.GPL` is included beside `mpv.exe`
in the old watch package. The current only-watch window no longer launches mpv.


## BBDown

The built-in HTTP range download pipeline is adapted from BBDown's range segmentation,
worker queue, resumable chunk and stalled-read handling at commit
`10e1049aaa11abab81a01c4b18a2996ab2ad815b`.

Source: <https://github.com/aliveranme/BBDown>

MIT License

Copyright (c) 2020 nilaoda

Copyright (c) 2025 AliverAnme

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
