$ErrorActionPreference = "Stop"
$outputDirectory = Join-Path $PSScriptRoot "dist"
$publishDirectory = Join-Path $outputDirectory "recommended-scores-server-publish-linux-x64"
$packageDirectory = Join-Path $outputDirectory "NTE-GUITAR-RecommendedScoresServer-linux-x64"
$tarArchive = Join-Path $outputDirectory "NTE-GUITAR-RecommendedScoresServer-linux-x64.tar.gz"
$zipArchive = Join-Path $outputDirectory "NTE-GUITAR-RecommendedScoresServer-linux-x64.zip"
$project = Join-Path $PSScriptRoot "AnnouncementServer\AnnouncementServer.csproj"

# Only clear this feature's own staging/package paths. Existing announcement-server
# packages and archives are deliberately left untouched.
foreach ($path in @($publishDirectory, $packageDirectory)) {
    if (Test-Path -LiteralPath $path) {
        $resolved = (Resolve-Path -LiteralPath $path).Path
        $distRoot = [IO.Path]::GetFullPath($outputDirectory)
        if (-not $resolved.StartsWith($distRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clear path outside the output directory: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
foreach ($archive in @($tarArchive, $zipArchive)) {
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
}
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

$dotnet = if ($env:DOTNET_ROOT -and (Test-Path (Join-Path $env:DOTNET_ROOT "dotnet.exe"))) {
    Join-Path $env:DOTNET_ROOT "dotnet.exe"
} elseif (Test-Path (Join-Path $env:USERPROFILE ".dotnet\dotnet.exe")) {
    Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
} else { "dotnet" }

& $dotnet publish $project -c Release -r linux-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:UseAppHost=true -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw "Linux Recommended Scores service publish failed: $LASTEXITCODE" }

$entryPoint = Join-Path $publishDirectory "NTE-GUITAR-AnnouncementServer"
$announcementFile = Join-Path $publishDirectory "announcement.json"
if (-not (Test-Path $entryPoint) -or -not (Test-Path $announcementFile) -or -not (Test-Path (Join-Path $publishDirectory "admin.html"))) {
    throw "Linux publish output is missing the executable, admin.html, or announcement.json."
}

New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $publishDirectory "*") -Destination $packageDirectory -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "AnnouncementServer\README.md") -Destination $packageDirectory -Force
& tar -czf $tarArchive -C $packageDirectory .
if ($LASTEXITCODE -ne 0) { throw "Failed to create the Linux Recommended Scores tar.gz archive." }
Compress-Archive -Path (Join-Path $packageDirectory "*") -DestinationPath $zipArchive -CompressionLevel Optimal

Write-Host "Linux x64 Recommended Scores packages created:"
Write-Host "  $tarArchive"
Write-Host "  $zipArchive"


