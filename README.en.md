# NTE-GUITAR Lite

**Windows desktop music arranging and game-instrument performance tool** · C# / .NET 8 / WinForms / WebView2

[简体中文](README.md) · [Algorithms and data flow](docs/ALGORITHMS.en.md) · [Build, run, and models](docs/BUILD_AND_MODELS.en.md) · [Third-party notices](docs/THIRD_PARTY.en.md) · [Architecture map](docs/ARCHITECTURE.md)

> Lite is the public source distribution: application source, required UI assets, implemented algorithms, and reproducible Windows x64 packaging scripts. It does not bundle model weights, a Python environment, driver installers, song MIDI files, workspace build caches, or large media. See [GitHub Releases](https://github.com/xunfenghua/-/releases) for published versions.

## Overview

NTE-GUITAR is a Windows desktop music tool. Import MIDI or Guitar Pro notation, select tracks, and convert music for a 36-key script keyboard, a game piano, or a game guitar. Manage reusable performances in a local library, preview MIDI timelines, and send timed keyboard, mouse, or optional virtual gamepad input to a bound game window. A separate semi-automatic guitar overlay supports chart following, practice, and rhythm judgement. The optional audio-to-score workflow uses local SheetSage2 models to create MIDI; audio is not uploaded to an application service.

This repository contains the client source, not a hosted online service, game content, or song collection. The announcement and recommended-score server is a separately buildable companion project. The project code is licensed under Apache-2.0; separately distributed models, drivers, NuGet packages, and media have their own terms. See [third-party notices](docs/THIRD_PARTY.md).

## Features

### Import, arrangement, and notation editing

- Import Standard MIDI (`.mid` / `.midi`); inspect tracks, note counts, and pitch ranges; audition tracks; select all, individual tracks, or an automatically extracted lead melody.
- Import Guitar Pro 3–7 files (`.gp`, `.gp3`, `.gp4`, `.gp5`, `.gpx`); choose full score or melody, tracks, target instrument, and transposition.
- MIDI piano-roll editor: edit note position, pitch, and duration; move, resize, quantize, audition, zoom, and inspect full-screen; save to the local library.
- Optional local audio-to-score workflow; inference uses a local Python environment and local models and produces editable MIDI.
- Preview, audition, rename, delete, reconvert to another instrument, or create reusable guitar charts from library scores.

### Instruments and performance modes

| Target | Implementation |
|---|---|
| 36-key script keyboard | Maps MIDI pitches to 36 key indices for E3–D#6 (52–87); transposition, octave folding or out-of-range dropping, track deduplication, and editable key presets. |
| Game piano | Independent piano mapping profiles; 36-key chromatic or 21-key diatonic layout; calibration, playability checks, compile preview, and session controls. |
| Game guitar | Fret solo, 24-chord accompaniment, and full hybrid modes. The hybrid compiler plans fret/chord actions across the piece, including page, slot, and strum operations. |
| Guitar simulator | Interactive view of pages, strings, frets, slots, and strums for learning the mapping and calibrating controls. |
| Semi-auto overlay | Practice, rhythm, harder rhythm, showcase, and MIDI-following sessions; manual or semi-automatic position changes, rhythm judgement, and chart guidance. |

### Playback, calibration, and companion features

- Playback uses absolute microsecond timing and the MIDI tempo map, with automatic song speed recommendations and playback-speed controls.
- Keyboard, mouse strum, and optional ViGEmBus virtual gamepad output; bind the game window and calibrate guitar wheels and key mappings.
- Default global F8 play/stop hotkey; target-window and focus-loss checks; all held input is released on completion, error, or shutdown.
- The local library, presets, calibration, and settings are stored independently from the original MIDI path.
- Browse a remote recommended-score catalogue, download MIDI, or submit charts for review; these features need a deployed announcement/recommended-score service.
- Startup announcements and client-version heartbeats are optional network features. Local import, arrangement, library, and playback do not require the server.

For the module map and algorithm detail, see the [architecture map](docs/ARCHITECTURE.md) and [algorithm implementation notes](docs/ALGORITHMS.en.md).

## Download

Download the Windows x64 ZIP marked `Lite` from [Releases](https://github.com/xunfenghua/-/releases), extract it to a writable directory, and run `NTE-GUITAR.exe`. The application needs Windows 10/11 x64 and the Microsoft Edge WebView2 Runtime. Most current Windows installations already have it. If missing, install the Evergreen Runtime from Microsoft's [official WebView2 page](https://developer.microsoft.com/microsoft-edge/webview2/).

Each Release ZIP includes `SHA256SUMS.txt`. Verify it in PowerShell:

```powershell
Get-FileHash .\NTE-GUITAR-Lite-3.4.0-win-x64.zip -Algorithm SHA256
Get-Content .\SHA256SUMS.txt
```

This is a portable desktop application. It does not include an installer, model weights, or the virtual gamepad driver. Do not grant driver installation permissions unless you need that input method.

## Build from source

### Requirements

- Windows 10/11 x64;
- .NET 8 SDK for Windows x64, available from the [.NET 8 download page](https://dotnet.microsoft.com/download/dotnet/8.0);
- Git and network access to NuGet;
- (Optional) Visual Studio 2022 with the “.NET desktop development” workload;
- (Optional) system WebView2 Runtime to run the application;
- (Optional) ViGEmBus driver for virtual gamepad output;
- (Optional) 64-bit Python 3.12, FFmpeg, and disk space for audio transcription. Model weights total about 2.8 GB; allow more room for runtime setup and temporary downloads.

```powershell
git clone https://github.com/xunfenghua/-.git
Set-Location -LiteralPath .\-
dotnet restore .\NTE-GUITAR.csproj
dotnet build .\NTE-GUITAR.csproj -c Release
```

If the `Set-Location` path does not match your clone directory, replace it with the directory you cloned into. Building does not download audio models or install system drivers. `dotnet restore` downloads pinned dependencies from NuGet.

Run the source build:

```powershell
dotnet run --project .\NTE-GUITAR.csproj -c Release
```

Run the built-in acceptance test runner:

```powershell
dotnet run --project .\NTE-GUITAR.csproj -c Release -- --test
```

### Create the Lite ZIP and checksum

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\Publish-Lite.ps1
```

The script publishes a self-contained `win-x64` app into `dist/release/` and writes a SHA-256 manifest. The package includes the .NET runtime; Windows still supplies WebView2 Runtime. The package script does not embed Python, FFmpeg, or model weights.

### Optional virtual gamepad driver

Virtual gamepad input requires the ViGEmBus kernel driver. Lite does not bundle its installer. If you need this input method, first review the upstream [archived project and releases](https://github.com/nefarius/ViGEmBus/releases), then run:

```powershell
.\scripts\Download-ViGEmBus.ps1
```

The script downloads the fixed upstream `v1.22.0` release, verifies its SHA-256, and saves the installer under `dist/dependencies/`. **It does not run the installer.** The upstream ViGEmBus repository is archived; driver compatibility depends on the device, Windows version, and game. Keyboard/mouse mode does not require this driver.

## Audio-to-score models

Regular MIDI/Guitar Pro import, editing, conversion, and playback do not use machine learning models. Audio-to-score is optional and uses SheetSage2 and MERT-v2-FullSong. Release packages contain no model parameters and do not download models until the user explicitly starts model preparation.

See the [build, run, and model guide](docs/BUILD_AND_MODELS.en.md) and [audio module guide](AudioTranscription/README.md) for exact setup steps, pinned model revisions, dependency installation, GPU/CPU behavior, hash verification, and license restrictions. Weights come from the [SheetSage2 model card](https://huggingface.co/m-a-p/SheetSage2) and [MERT-v2-FullSong model card](https://huggingface.co/m-a-p/MERT-v2-FullSong), both under CC BY-NC 4.0, which restricts commercial use. Review each model card and license before downloading. The downloader only reads files pinned in `AudioTranscription/downloads.json` and verifies fixed SHA-256 values; source availability does not grant rights to redistribute model weights.

## Data, network, and privacy

- Local settings, key presets, caches, and library data are usually stored in `%LocalAppData%\NTE-GUITAR\`. Removing the source or app does not automatically remove that directory; back up user data separately if needed.
- Audio transcription runs locally. Temporary job files are stored in the user's data directory. Do not attach private audio or personal logs to public issues.
- Online announcements, recommended scores, and version heartbeats make HTTP(S) requests. Heartbeats report the app version and a random per-process session ID; no persistent device identifier is stored. Inspect `Services/AnnouncementCheckService.cs`, `Services/RecommendedScoreService.cs`, and `Services/ClientPresenceService.cs` for details.
- Playback sends input to the selected foreground game window. Calibrate and test first. Game rules, anti-cheat behavior, and third-party service terms are determined by their respective owners.
- Lite excludes personal logs, crash dumps, packaged song MIDI, public catalogue data, and machine-learning weights.

## Source map

| Path | Contents |
|---|---|
| `Program.cs`, `UI/` | WinForms shell, startup, settings, calibration, and dialogs. |
| `wwwroot/`, `Web/SemiAutoOverlay/` | WebView2 front end, editor, player, and overlay UI. |
| `Bridge/` | JSON message protocol and event publication between web and native code. |
| `Midi/`, `Recognition/`, `Models/` | MIDI parsing/conversion, melody extraction, and shared models. |
| `Guitar/` | Guitar fret, chord, hybrid compilers, action plans, and scheduler. |
| `Piano/` | Game-piano pitch mapping, score compiler, and sessions. |
| `SemiAuto/` | Guitar charts, position planning, practice/rhythm judgement, and overlay controllers. |
| `Gtp/`, `Services/`, `Library/` | Guitar Pro conversion, settings/calibration services, and local/recommended libraries. |
| `Input/`, `Core/` | Win32/virtual-gamepad input, safe release, scheduler, and speed engine. |
| `AudioTranscription/` | Local Python worker, runtime bootstrap, and pinned model manifest. |
| `AnnouncementServer/` | Separately buildable announcement and recommended-score API. |
| `Testing/` | In-process algorithm and integration acceptance tests. |

## Version, limitations, and contribution

- The project version is defined in `<Version>` in `NTE-GUITAR.csproj`; release tags and asset names should match it.
- This is the Lite source distribution. Large runtime caches, model weights, third-party driver installers, sample songs, media video, internal plans, and drafts are excluded from Git history.
- Do not commit personal data, unlicensed scores/recordings, or model snapshots. Read [contribution guidelines](CONTRIBUTING.md) and [third-party notices](docs/THIRD_PARTY.md) before contributing.
- The application code is licensed under Apache-2.0; third-party dependencies and downloadable models are governed by their own licenses. Do not use the project to violate game rules or other people's rights.

## Acknowledgements

Thanks to the maintainers of .NET, WebView2, AlphaTab, DryWetMIDI, ViGEm, and SheetSage2/MERT. Exact versions and sources are listed in [third-party notices](docs/THIRD_PARTY.md).
