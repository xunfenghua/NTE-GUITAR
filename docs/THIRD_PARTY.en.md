# Third-party components, models, and assets

This inventory is based on the current project file and upstream pages. It does not replace licenses shipped with each package, asset, or model. NuGet transitive dependencies may have additional terms. Before distributing or modifying binaries, review package license/notice metadata and include required notices.

## Direct .NET dependencies

| Component | Pinned version | Purpose | Source / license |
|---|---:|---|---|
| AlphaTab | 1.8.4 | Guitar Pro file and notation support | [NuGet](https://www.nuget.org/packages/AlphaTab/1.8.4) · [upstream MPL-2.0](https://github.com/CoderLine/alphaTab/blob/develop/LICENSE) |
| Melanchall.DryWetMidi | 8.0.3 | Standard MIDI files, note and tempo data | [NuGet](https://www.nuget.org/packages/Melanchall.DryWetMidi/8.0.3) · MIT |
| Microsoft.Web.WebView2 | 1.0.4258.31 | Chromium WebView2 control for WinForms | [NuGet](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4258.31) · Microsoft package terms; Windows WebView2 Runtime is also required |
| Nefarius.ViGEm.Client | 1.21.256 | Managed client for optional virtual gamepad driver | [NuGet](https://www.nuget.org/packages/Nefarius.ViGEm.Client/1.21.256) · MIT; separate ViGEmBus driver required |
| .NET 8 / Windows Forms | 8.x | Runtime, WinForms, system APIs | [Microsoft .NET downloads and licensing](https://dotnet.microsoft.com/download/dotnet/8.0) |

This is the direct-reference list. The published ZIP may also contain Microsoft .NET runtime, WebView2 loader/native files, and transitive NuGet dependencies. Review the corresponding `*.deps.json` and package metadata for a complete binary inventory. Modifications to third-party source must follow their original terms. AlphaTab is MPL-2.0; observe MPL requirements for any modified covered files.

## External runtimes and driver

| Component | Needed when | Source | How Lite handles it |
|---|---|---|---|
| Microsoft Edge WebView2 Evergreen Runtime | Running the desktop UI | [Microsoft official download](https://developer.microsoft.com/microsoft-edge/webview2/) | Installed separately; the Lite package does not bundle its installer. |
| ViGEmBus driver 1.22.0 | Virtual gamepad input is selected | [Upstream GitHub release](https://github.com/nefarius/ViGEmBus/releases/tag/v1.22.0) | Excluded from Git and Release ZIP; `scripts/Download-ViGEmBus.ps1` downloads and verifies it but does not install it. The upstream repository is archived. |
| Python 3.12 x64 | Audio transcription is enabled | [Python official downloads](https://www.python.org/downloads/release/python-3120/) | Installed by the user; the worker prepares a private environment in AppData. |
| FFmpeg | Audio transcription is enabled | [FFmpeg official download guide](https://ffmpeg.org/download.html) | The user selects and installs a trusted build. Not included in Lite. Builds from different download providers may have separate terms. |

## Optional AI models and Python packages

| Item | Source / pinned revision | Purpose / license |
|---|---|---|
| SheetSage2 | [Model card](https://huggingface.co/m-a-p/SheetSage2), revision `398b22834dac7dd05e09b9c4e40a39fc479ec502` | Audio-to-score inference. Model weights and custom code are marked **CC BY-NC 4.0**. |
| MERT-v2-FullSong | [Model card](https://huggingface.co/m-a-p/MERT-v2-FullSong), revision `d8ba1c745e733b3908ce6ad16ebeb17ac7600a42` | Music audio encoder required by SheetSage2. Model and code are marked **CC BY-NC 4.0**. |
| PyTorch / torchaudio | [PyTorch installation guide](https://pytorch.org/get-started/locally/); worker pins 2.8.0 | Local inference runtime. Follow the license and dependency notices from the selected distribution source. |
| Transformers and packages in `AudioTranscription/requirements.txt` | [PyPI](https://pypi.org/) / each package site | Python worker inference dependencies; each keeps its own license. |

`AudioTranscription/downloads.json` pins revisions and SHA-256 values for selected model/code files. The downloader does not put models in GitHub release assets. CC BY-NC does not allow commercial use without additional permission; contact model rights holders for commercial use or redistribution. The worker loads upstream model code through Transformers `trust_remote_code=True`. Revisions and hashes are pinned and local snapshots load offline, but users should still review the source and accept the security implications of executing model code locally.

## Excluded third-party content

The Lite repository and binary do not include popular-song MIDI files from the original workspace, user-packed presets/catalogue data, the ViGEmBus setup executable, FFmpeg, a Python venv, roughly 2.8 GB of model weights, or a large hidden video. The project's source license does not grant rights to those materials. User-imported songs and recordings remain the property of their respective rights holders.

## Project license

The desktop client and companion service's own source code is licensed under Apache License 2.0 in the root [`LICENSE`](../LICENSE). Apache-2.0 does not cover the third-party dependencies, models, drivers, runtimes, fonts/media, or user-imported works listed above.
