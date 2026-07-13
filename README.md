# SmartTV Live Relay

**SmartTV Live Relay is an unofficial extension for SmartTV by Moddy / modestimpala.** It is not affiliated with or endorsed by the original SmartTV author.

The original SmartTV package is installed through the dependency list. This package does not include or replace SmartTV's PAK, models, Blueprints, store entries or UI assets.

## What it does

SmartTV can already resolve normal videos through yt-dlp. This extension adds a small local relay for public YouTube Live and Twitch streams so Unreal's Windows Media player can open them reliably.

Normal videos continue to use SmartTV's original path. Live HLS sources are repackaged locally as fragmented MP4 without video re-encoding.

## Installation

Use **Install with Mod Manager** on Thunderstore or install the package from r2modman.

There is no first-run downloader, PowerShell installer or folder-selection step. The required runtime files are included and placed into the game's `Binaries\Win64\yt-dlp` directory through unreal-shimloader's overlay system.

The original SmartTV, Fusion and unreal-shimloader are installed automatically as dependencies.

## Usage

1. Open a SmartTV and choose **Media**.
2. Add a public YouTube Live URL or Twitch channel URL.
3. Play the entry normally.

Starting a live stream can take a few seconds while the source is resolved and the local relay is prepared.

## Included runtime

- `yt-dlp.exe` — SmartTV Live relay shim
- `yt-dlp-real.exe` — yt-dlp 2026.07.04
- `deno.exe` — Deno 2.9.2
- `ffmpeg/ffmpeg.exe` — FFmpeg `N-121910-gcac5018eb9-20251127`
- `source/YtDlpShim.cs` — source for the relay shim
- `licenses/` — third-party license texts

The package performs no runtime downloads. Binary hashes and source information are listed in `THIRD_PARTY_NOTICES.md`.

## Limitations

Public streams work best. Private, members-only, age-restricted, geo-blocked or DRM-protected streams may require authentication and may not work.

## Türkçe

Bu paket, Moddy / modestimpala tarafından yapılan orijinal SmartTV modu için hazırlanmış bağımsız ve resmî olmayan bir canlı yayın eklentisidir. Orijinal SmartTV paketi bağımlılık olarak otomatik kurulur; televizyon modelleri, mağaza girdileri ve SmartTV PAK dosyası bu paketin içinde bulunmaz.

Thunderstore veya r2modman üzerinden **Install with Mod Manager** seçeneğine basman yeterli. Ek kurulum dosyası çalıştırılmaz ve ilk açılışta internetten program indirilmez.

SmartTV'nin **Media** bölümüne herkese açık bir YouTube Live veya Twitch kanal bağlantısı ekleyerek kullanabilirsin.

## Credits

- SmartTV: Moddy / modestimpala
- Fusion: NynrahGhost
- unreal-shimloader: Thunderstore contributors
- yt-dlp contributors
- Deno contributors
- FFmpeg contributors
- Live relay and packaging: forderdev
