# Third-party notices

This package is an unofficial extension for SmartTV by Moddy / modestimpala. The original SmartTV is installed as the dependency `Moddy-SmartTV-0.9.6`; none of its PAK assets are redistributed here.

## Bundled executables

### yt-dlp 2026.07.04

- Project: https://github.com/yt-dlp/yt-dlp
- License: Unlicense (`overlay/yt-dlp/licenses/yt-dlp-UNLICENSE.txt`)
- Binary: `overlay/yt-dlp/yt-dlp-real.exe`
- SHA-256: `52fe3c26dcf71fbdc85b528589020bb0b8e383155cfa81b64dd447bbe35e24b8`

### Deno 2.9.2

- Project: https://github.com/denoland/deno
- License: MIT (`overlay/yt-dlp/licenses/Deno-MIT.txt`)
- Binary: `overlay/yt-dlp/deno.exe`
- SHA-256: `a5270c2bb75a2ec12fef53185730327267d9e9fe6be6a962c5d1d5a050f93c88`

### FFmpeg N-121910-gcac5018eb9-20251127

- Project source: https://github.com/FFmpeg/FFmpeg/tree/cac5018eb9
- Windows build scripts: https://github.com/BtbN/FFmpeg-Builds
- License: GNU GPL version 3 (`overlay/yt-dlp/licenses/FFmpeg-GPL-3.0.txt`)
- Binary: `overlay/yt-dlp/ffmpeg/ffmpeg.exe`
- SHA-256: `44906eadd0670ae965bf59d602b072979402b5c62df29c3f555eff9629a6f517`
- The binary reports `--enable-gpl --enable-version3` and extra version `20251127`.

### SmartTV Live relay shim

- Source: `overlay/yt-dlp/source/YtDlpShim.cs` and https://github.com/forderdev/VotV-SmartTV-Live
- Binary: `overlay/yt-dlp/yt-dlp.exe`
- SHA-256: `1e3e8e4c169b679a243a8f04a7723e4be3e195bcf992b8f30093f56b3137a26f`

The relay listens only on `127.0.0.1` and is started by SmartTV when a supported live stream is requested. This release contains no download or installation scripts.
