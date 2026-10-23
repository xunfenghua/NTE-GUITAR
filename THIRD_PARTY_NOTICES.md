# Third-Party Notices

NTE-GUITAR Lite includes or references third-party software. The project source is licensed under Apache-2.0, but that license does not replace the separate terms below.

- **AlphaTab 1.8.4** — MPL-2.0. Guitar Pro/music notation support. Source: https://github.com/CoderLine/alphaTab
- **Melanchall.DryWetMidi 8.0.3** — MIT. MIDI file handling. Source: https://github.com/melanchall/drywetmidi
- **Microsoft.Web.WebView2 1.0.4258.31** — Microsoft package terms. The Windows WebView2 Runtime is installed separately. Source: https://developer.microsoft.com/microsoft-edge/webview2/
- **Nefarius.ViGEm.Client 1.21.256** — MIT. Optional managed client for the separately installed ViGEmBus driver. Source: https://github.com/nefarius/ViGEmClient
- **.NET 8 / Windows Forms** — Microsoft .NET terms. Source and downloads: https://dotnet.microsoft.com/download/dotnet/8.0
- **SheetSage2 and MERT-v2-FullSong** — optional user-downloaded model/code snapshots under CC BY-NC 4.0. They are not included in this package. See `AudioTranscription/README.md` and `docs/THIRD_PARTY.md` before enabling audio transcription.
- **ViGEmBus driver** — optional, not included. See the upstream license and archived release page before installing: https://github.com/nefarius/ViGEmBus/releases

This list covers direct project dependencies and named optional components. A framework-dependent or self-contained package can also include transitive runtime components. Review the NuGet package metadata, generated `.deps.json`, and applicable upstream license files before redistributing a modified binary.

The Lite package excludes user songs, model weights, FFmpeg, Python, ViGEmBus setup, and large media assets.
