# OmniPlayer

A personal Windows media player built on **libVLC** (the engine behind VLC). It carries its own codecs, so it plays
**HEVC/H.265, AV1, VP9, H.264, MPEG-2/4, DivX/Xvid, WMV, ProRes, MKV, AVI, MOV, TS, FLAC, ALAC, DTS, AC-3, Opus**
and nearly everything else — no Windows codec extensions required.

## Download

**[⬇ OmniPlayer-win-x64.zip](../../releases/latest/download/OmniPlayer-win-x64.zip)** — unzip anywhere and run `OmniPlayer.exe`.

> First launch: Windows SmartScreen may say "Windows protected your PC" because the app isn't code-signed.
> Click **More info → Run anyway**. Keep the `libvlc` folder next to the .exe.

Every push to `main` rebuilds the app automatically (see the **Actions** tab).

## Features

- Playlist with drag & drop of files **and folders**, reordering, filter, durations, M3U load/save, restore on next launch
- Network streams (http/https, rtsp, rtmp, udp, ftp, smb)
- Audio, video and subtitle **track switching**; external subtitles (.srt .ass .ssa .vtt .sub .idx .smi), auto-matched by name
- Subtitle and audio **delay** adjustment
- 10-band **equalizer** with VLC presets
- Speed 0.25×–4×, A–B loop, frame step, jump to time / percent
- Resume where you left off in long files
- Snapshots to `Pictures\OmniPlayer`
- Fullscreen with auto-hiding controls, aspect-ratio override, always-on-top
- Volume up to 150 %, hardware decoding, spectrum visualizer for music
- Media info (codec, resolution, frame rate, channels)
- Dark theme, keyboard shortcuts (press **F1** in the app)

## Make it your default player

Right-click a video → **Open with → Choose another app → More apps → Look for another app on this PC** → pick `OmniPlayer.exe`,
and tick **Always use this app**.

## Build it yourself

Install the [.NET 8 SDK](https://dotnet.microsoft.com/download), then:

```
dotnet publish src/OmniPlayer.csproj -c Release -r win-x64 --self-contained true -o publish
```

Settings live in `%AppData%\OmniPlayer\settings.json`. libVLC is licensed under LGPL 2.1.
