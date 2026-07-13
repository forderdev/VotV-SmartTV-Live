from pathlib import Path
import hashlib, shutil, zipfile

ROOT = Path(__file__).resolve().parents[1]
RUNTIME = ROOT / "runtime" / "yt-dlp"
DIST = ROOT / "dist"
STAGE = DIST / "stage"
OUT = DIST / "VotV_SmartTV_Live-0.9.24-Thunderstore-r2modman.zip"

required = [
    RUNTIME / "yt-dlp.exe",
    RUNTIME / "yt-dlp-real.exe",
    RUNTIME / "deno.exe",
    RUNTIME / "ffmpeg" / "ffmpeg.exe",
]
missing = [str(p) for p in required if not p.is_file()]
if missing:
    raise SystemExit("Missing runtime files:\n" + "\n".join(missing))

if STAGE.exists():
    shutil.rmtree(STAGE)
(STAGE / "overlay" / "yt-dlp" / "ffmpeg").mkdir(parents=True)
(STAGE / "overlay" / "yt-dlp" / "source").mkdir(parents=True)
(STAGE / "overlay" / "yt-dlp" / "licenses").mkdir(parents=True)

for name in ("README.md", "CHANGELOG.md", "manifest.json", "THIRD_PARTY_NOTICES.md", "MODERATOR_NOTES.md", "icon.png"):
    shutil.copy2(ROOT / name, STAGE / name)
shutil.copy2(ROOT / "src" / "YtDlpShim.cs", STAGE / "overlay" / "yt-dlp" / "source" / "YtDlpShim.cs")
for license_file in (ROOT / "licenses").iterdir():
    if license_file.is_file():
        shutil.copy2(license_file, STAGE / "overlay" / "yt-dlp" / "licenses" / license_file.name)

for source, relative in [
    (RUNTIME / "yt-dlp.exe", "yt-dlp.exe"),
    (RUNTIME / "yt-dlp-real.exe", "yt-dlp-real.exe"),
    (RUNTIME / "deno.exe", "deno.exe"),
    (RUNTIME / "ffmpeg" / "ffmpeg.exe", "ffmpeg/ffmpeg.exe"),
]:
    target = STAGE / "overlay" / "yt-dlp" / relative
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, target)

config = "--js-runtimes deno\r\n"
for name in ("yt-dlp.conf", "yt-dlp-real.conf"):
    (STAGE / "overlay" / "yt-dlp" / name).write_text(config, encoding="ascii", newline="")

(STAGE / "overlay" / "yt-dlp" / "README-runtime.txt").write_text(
    "SmartTV Live Relay runtime\r\n\r\n"
    "Installed through unreal-shimloader's overlay route.\r\n"
    "No first-run downloader or installer is used.\r\n",
    encoding="utf-8",
    newline="",
)

checksum_lines = []
for path in sorted(STAGE.rglob("*")):
    if path.is_file():
        checksum_lines.append(f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.relative_to(STAGE).as_posix()}")
(STAGE / "PACKAGE-CHECKSUMS.txt").write_text("\n".join(checksum_lines) + "\n", encoding="ascii", newline="\n")

DIST.mkdir(exist_ok=True)
OUT.unlink(missing_ok=True)
with zipfile.ZipFile(OUT, "w", zipfile.ZIP_DEFLATED, compresslevel=1, allowZip64=True) as z:
    for path in sorted(STAGE.rglob("*")):
        if path.is_file():
            z.write(path, path.relative_to(STAGE).as_posix())

with zipfile.ZipFile(OUT) as z:
    if z.testzip() is not None:
        raise SystemExit("ZIP integrity test failed")
    names = z.namelist()
    if any(name.lower().endswith((".bat", ".ps1", ".lua")) for name in names):
        raise SystemExit("Unexpected bootstrap script found")
print(OUT)
print(hashlib.sha256(OUT.read_bytes()).hexdigest())
