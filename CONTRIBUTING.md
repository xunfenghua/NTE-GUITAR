# Contributing

Thanks for considering a contribution to NTE-GUITAR Lite.

## Before opening an issue or pull request

- Search existing issues and describe reproducible steps, the affected mode, expected behavior, and actual behavior.
- Do not attach copyrighted scores/recordings without permission, personal logs, model weights, access tokens, or crash dumps containing private paths.
- For security reports, do not publish exploit details in a public issue. Contact the repository owner privately through the GitHub profile.
- Keep changes focused. Include a short explanation of user-visible behavior and any input-timing or data-format changes.

## Development

- Target: Windows 10/11 x64 and .NET 8 (`NTE-GUITAR.csproj`).
- Restore/build: `dotnet restore .\NTE-GUITAR.csproj` and `dotnet build .\NTE-GUITAR.csproj -c Release`.
- In-process checks: `dotnet run --project .\NTE-GUITAR.csproj -c Release -- --test`.
- Keep WebView message contracts in `Bridge/` explicit and preserve the native/web boundary.
- Time-critical playback should use absolute monotonic deadlines and must preserve cancellation and release paths.
- Do not commit local build output, user data, model snapshots, MIDI songs, driver installers, or generated binaries.

## Licensing

Contributions are accepted under the repository's Apache-2.0 license. By submitting a contribution, you confirm that you have the right to submit it under that license. Do not copy code, scores, assets, or model files whose terms do not allow inclusion. Review `docs/THIRD_PARTY.md` before changing third-party dependencies or package contents.
