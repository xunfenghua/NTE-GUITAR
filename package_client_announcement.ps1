$ErrorActionPreference = "Stop"
$outputDirectory = Join-Path $PSScriptRoot "dist"
$publishDirectory = Join-Path $outputDirectory "client-publish-win-x64"
$packageDirectory = Join-Path $outputDirectory "NTE-GUITAR-V3.0-announcement-win-x64"
$archivePath = "$packageDirectory.zip"

foreach ($path in @($publishDirectory, $packageDirectory)) {
    if (Test-Path -LiteralPath $path) {
        $resolved = (Resolve-Path -LiteralPath $path).Path
        if (-not $resolved.StartsWith($outputDirectory, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clear path outside dist: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

$dotnet = if ($env:DOTNET_ROOT -and (Test-Path (Join-Path $env:DOTNET_ROOT "dotnet.exe"))) {
    Join-Path $env:DOTNET_ROOT "dotnet.exe"
} else { "dotnet" }

& $dotnet publish (Join-Path $PSScriptRoot "NTE-GUITAR.csproj") -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw "Desktop publish failed with exit code $LASTEXITCODE" }

New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $publishDirectory "NTE-GUITAR.exe") -Destination $packageDirectory -Force
Compress-Archive -Path (Join-Path $packageDirectory "*") -DestinationPath $archivePath -CompressionLevel Optimal
Write-Host "公告客户端压缩包已生成：$archivePath"
