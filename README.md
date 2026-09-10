# Akashic Records

Personal desktop "digital memory" app for Windows (C#/.NET 8, WPF). Full vision doc: `d:\workspace\akashic records.md`. Build notes / architecture: [HANDOFF.md](HANDOFF.md).

## Requirements
- Windows
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- (Optional, for the music downloader) [yt-dlp](https://github.com/yt-dlp/yt-dlp) + [ffmpeg](https://github.com/yt-dlp/FFmpeg-Builds) — see below

## Running the app

From the repo root:

```powershell
dotnet run --project src\AkashicRecords.App\AkashicRecords.App.csproj
```

If `dotnet` isn't on your PATH, call it directly:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" run --project "src\AkashicRecords.App\AkashicRecords.App.csproj"
```

The app is portable — on first run it creates, next to the built exe (`src\AkashicRecords.App\bin\Debug\net8.0-windows\`):
- `data\akashic.db` — SQLite database (schema auto-created/migrated on startup)
- `config\config.json` — app settings
- `media\` — imported images (covers, photos, artwork, etc.)
- `music\` — put your own `.mp3` files here (in subfolders = albums); the player scans this folder

## Using the app
- The app runs as a **transparent full-screen overlay** with just a header bar — the rest of your screen stays click-through until you open a section.
- **Global hotkey**: `Ctrl+Alt+Space` shows/hides the whole window.
- **Header nav**: Organisation / Journaux / Collections / Archives, plus a search box and a `♪` button (floating music player).
- **System tray icon** (bottom-right, may be under the `^` "show hidden icons" chevron): right-click for Show/Hide, **Film du jour**, **Rappels bureau**, **Téléchargements**, Start with Windows, and **Exit** (the only thing that actually quits the app — closing windows just hides them).

## Building / rebuilding
Always stop the running app first — its own `.exe` locks its DLLs, causing an `MSB3027` file-lock build error otherwise:

```powershell
Get-Process -Name "AkashicRecords.App" -ErrorAction SilentlyContinue | Stop-Process
dotnet build AkashicRecords.sln
```

## Optional: music downloader (yt-dlp)
The "Téléchargements" window can drive a locally-installed [yt-dlp](https://github.com/yt-dlp/yt-dlp) to fetch audio into your `music\` folder. Akashic does **not** bundle or auto-invoke this against any specific site — you install/configure the tool and are responsible for what you point it at and for respecting each source's terms of use.

To install yt-dlp + ffmpeg locally, run the setup script once from the build output folder:

```powershell
cd src\AkashicRecords.App\bin\Debug\net8.0-windows
.\setup-tools.ps1
```

This downloads both tools into a `tools\` folder next to the exe; the app auto-detects them there (or on PATH, or at a path you set in Téléchargements → *Réglages de l'outil*).

## Project structure
```
src/
├── AkashicRecords.Domain          — plain data classes/enums, no dependencies
├── AkashicRecords.Infrastructure  — SQLite persistence, config, Windows integration, downloading
└── AkashicRecords.App             — WPF UI (MainWindow + Views + floating windows/widgets)
```

See [HANDOFF.md](HANDOFF.md) for full architecture notes, what's implemented, and known gotchas.
