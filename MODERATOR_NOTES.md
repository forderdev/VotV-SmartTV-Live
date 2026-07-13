# Moderator notes

SmartTV Live Relay is an unofficial extension for **SmartTV by Moddy / modestimpala**.

- The original SmartTV package is installed through `Moddy-SmartTV-0.9.6`.
- This archive does not contain SmartTV's PAK, models, Blueprints, UI or store assets.
- Version 0.9.24 contains no PowerShell, BAT or Lua bootstrap and performs no runtime downloads.
- The package uses the supported `overlay/` route to expose `yt-dlp/` under the game's `Binaries/Win64` directory.
- The relay shim source is included at `overlay/yt-dlp/source/YtDlpShim.cs`.
- Third-party versions, hashes, source links and license information are in `THIRD_PARTY_NOTICES.md`.

The bundled runtime is needed because SmartTV invokes yt-dlp from `Binaries/Win64/yt-dlp`, while FFmpeg and Deno are required for current YouTube Live extraction and the local HLS-to-fragmented-MP4 relay.
