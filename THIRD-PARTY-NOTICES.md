
# Third-Party Notices

## Bundled Windows tools

The Windows single-file build embeds cloudflared 2026.9.0 from
<https://github.com/cloudflare/cloudflared/releases/tag/2026.9.0>, aria2 1.37.0
from <https://github.com/crazysmile-PhD/downkyi-aria2-static-build>, and the
FFmpeg build identified in `script/assets/external-assets.json` from
<https://github.com/BtbN/FFmpeg-Builds>. Build inputs are verified with SHA-256
before embedding. These tools are extracted only when their feature is used.
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
