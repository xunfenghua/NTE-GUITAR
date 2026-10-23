param(
    [string]$RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $repoRoot "NTE-GUITAR.csproj"
$releaseRoot = Join-Path $repoRoot "dist\release"
$stage = Join-Path $releaseRoot "_staging"

if (-not (Test-Path -LiteralPath $project)) { throw "Project not found: $project" }
[xml]$projectXml = Get-Content -LiteralPath $project
$version = [string]$projectXml.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) { throw "Could not read <Version> from $project" }
if ($RuntimeIdentifier -ne "win-x64") { throw "Lite packaging currently supports win-x64 only." }

New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
if (Test-Path -LiteralPath $stage) {
    $resolvedStage = (Resolve-Path -LiteralPath $stage).Path
    $resolvedRoot = [IO.Path]::GetFullPath($releaseRoot)
    if (-not $resolvedStage.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clear a staging path outside the release directory: $resolvedStage"
    }
    Remove-Item -LiteralPath $resolvedStage -Recurse -Force
}
New-Item -ItemType Directory -Path $stage -Force | Out-Null

$publishDir = Join-Path $stage "publish"
$dotnetExe = $null
if ($env:DOTNET_ROOT -and (Test-Path (Join-Path $env:DOTNET_ROOT "dotnet.exe"))) {
    $dotnetExe = Join-Path $env:DOTNET_ROOT "dotnet.exe"
} elseif (Test-Path (Join-Path $HOME ".dotnet\dotnet.exe")) {
    $dotnetExe = Join-Path $HOME ".dotnet\dotnet.exe"
} else {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dotnetCommand) { $dotnetExe = $dotnetCommand.Source }
}
if (-not $dotnetExe) { throw ".NET 8 SDK is required. Install it from https://dotnet.microsoft.com/download/dotnet/8.0" }

& $dotnetExe publish $project -c Release -r $RuntimeIdentifier --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EmbedAudioRuntime=false `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$packageName = "NTE-GUITAR-Lite-v$version-$RuntimeIdentifier"
$zipPath = Join-Path $releaseRoot "$packageName.zip"
$manifestPath = Join-Path $releaseRoot "SHA256SUMS.txt"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Copy-Item -LiteralPath (Join-Path $repoRoot "README.md") -Destination $publishDir
Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination $publishDir
Copy-Item -LiteralPath (Join-Path $repoRoot "THIRD_PARTY_NOTICES.md") -Destination $publishDir
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($zipPath))" | Set-Content -LiteralPath $manifestPath -Encoding ascii
Write-Host "Release ZIP: $zipPath"
Write-Host "SHA-256: $hash"
