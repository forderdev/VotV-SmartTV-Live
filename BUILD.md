# Building the Thunderstore package

The release package uses the Voices of the Void `overlay/` route. unreal-shimloader overlays each wrapper package onto the game's `Binaries/Win64` directory, so `overlay/yt-dlp/yt-dlp.exe` is exposed as `Binaries/Win64/yt-dlp/yt-dlp.exe`.

The package contains no PowerShell, BAT or Lua bootstrap and performs no runtime downloads.

## Required runtime input

Place these files under a local `runtime/yt-dlp/` directory before running the build script:

- `yt-dlp.exe` — compiled relay shim
- `yt-dlp-real.exe` — yt-dlp 2026.07.04
- `deno.exe` — Deno 2.9.2
- `ffmpeg/ffmpeg.exe` — FFmpeg N-121910-gcac5018eb9-20251127

The large third-party executables are not stored in Git because FFmpeg exceeds GitHub's normal per-file limit. Their release hashes are documented in `THIRD_PARTY_NOTICES.md`.

Run:

```text
python tools/build_package.py
```

The script creates `dist/VotV_SmartTV_Live-0.9.24-Thunderstore-r2modman.zip`.
