# Build, run, and model preparation

## Windows desktop application

### Required software

1. Windows 10/11 x64.
2. [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0); the project targets `net8.0-windows`. The SDK includes the compiler and Windows Desktop targeting pack.
3. Git and access to `api.nuget.org` so NuGet can restore packages from the project file.
4. Microsoft Edge WebView2 Runtime to run the UI: [official WebView2 downloads](https://developer.microsoft.com/microsoft-edge/webview2/). The SDK package is restored from NuGet; the Windows Evergreen Runtime is a separate component.

Visual Studio is optional. In Visual Studio 2022, install the “.NET desktop development” workload. After cloning:

```powershell
dotnet --info
dotnet restore .\NTE-GUITAR.csproj
dotnet build .\NTE-GUITAR.csproj -c Release
```

Run the source build with `dotnet run --project .\NTE-GUITAR.csproj -c Release`. Run the acceptance runner with `dotnet run --project .\NTE-GUITAR.csproj -c Release -- --test`.

### Build a Release package

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\Publish-Lite.ps1
```

The script writes to `dist/release/`. It publishes a self-contained `win-x64` app with single-file options and creates a compressed ZIP. The .NET runtime ships with the app; Windows still provides WebView2 Evergreen Runtime. The archive SHA-256 is in `SHA256SUMS.txt`. Before publishing a public Release, extract and launch it under a clean Windows account or VM and verify at least one input path.

Manual publish example:

```powershell
dotnet publish .\NTE-GUITAR.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EmbedAudioRuntime=false `
  -o .\dist\publish-win-x64
```

Web, configuration, and native content assets may remain as separate files; archive the complete publish directory. Lite packaging excludes media video, sample songs, models, Python runtime, FFmpeg, and system drivers.

## Optional ViGEmBus virtual gamepad

The kernel driver is only needed when virtual gamepad output is enabled; keyboard/mouse mode does not require it. The driver is not redistributed in this source repository or Release package.

```powershell
.\scripts\Download-ViGEmBus.ps1
```

The script downloads the upstream 1.22.0 setup into `dist/dependencies/`, checks a pinned SHA-256, and stops. Review upstream instructions and install manually. The ViGEmBus repository is archived; see the [upstream release](https://github.com/nefarius/ViGEmBus/releases/tag/v1.22.0). Driver installation may require administrator permission and a restart.

## Audio transcription: Python and FFmpeg

Audio-to-score is optional. Python, FFmpeg, and models are not needed to import, edit, or play existing MIDI/Guitar Pro files.

1. Install 64-bit Python 3.12 from the [python.org Windows downloads page](https://www.python.org/downloads/windows/) or your organization's software source. The bootstrap looks for the Python Launcher (`py -3.12`) or `python.exe` on `PATH`.
2. Install FFmpeg and make `ffmpeg.exe` available on `PATH`; the project does not install FFmpeg. Choose an appropriate build using [FFmpeg's official download guide](https://ffmpeg.org/download.html) and follow its distributor terms.
3. Allow about 6 GiB for first runtime setup. The two model snapshots total about 2.8 GB, plus temporary download space.
4. Read `AudioTranscription/README.md` for model cards, pinned revisions, licenses, and runtime details, then run from the source root:

```powershell
$data = Join-Path $env:LOCALAPPDATA 'NTE-GUITAR'
py -3.12 .\AudioTranscription\bootstrap.py --data-root $data --install-runtime
py -3.12 .\AudioTranscription\bootstrap.py --data-root $data --prepare-models
```

Initial preparation installs PyTorch 2.8.0 and torchaudio 2.8.0 in `%LocalAppData%\NTE-GUITAR\audio-transcription\runtime\python312`; other pinned Python dependencies are in `AudioTranscription/requirements.txt`. Model snapshots are downloaded from immutable revisions and verified against hashes in `AudioTranscription/downloads.json`. The downloader uses ModelScope first, with the documented Hugging Face Hub fallback. It fetches only the listed SheetSage2 and MERT files, not Qwen or ASR/alignment models.

Model weights and custom code are subject to **CC BY-NC 4.0**, including a non-commercial restriction. Do not include model weights in this project's Releases. Contact the rights holders for permission before commercial use. `trust_remote_code` runs pinned model Python code in a local process; review the pinned source files and decide whether to trust them.

If CUDA is unavailable, the worker asks for CPU-inference confirmation before starting. CPU performance/resource use depends on hardware. For CUDA builds, use the [official PyTorch install selector](https://pytorch.org/get-started/locally/) for the installed driver; the CUDA 12.8 example in source comments is not universal.

## Optional FFmpeg embedding for maintainers

Lite does not embed FFmpeg by default. For a private package that includes a lawfully obtained FFmpeg binary, pass its path via `-p:EmbeddedFfmpegPath=C:\tools\ffmpeg\bin\ffmpeg.exe`. Confirm that the specific build permits redistribution and include its license/source with the package. Do not republish arbitrary user-downloaded binaries without review.

## Paths and user data

User data is normally stored under `%LocalAppData%\NTE-GUITAR\`, including configuration, presets, parse cache, library, audio jobs, and downloaded models. Installation/upgrade should not delete this user directory. Users should back it up explicitly before migrating computers.
