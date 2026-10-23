param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "dist")
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "AnnouncementServer\AnnouncementServer.csproj"
$publishDir = Join-Path $PSScriptRoot "AnnouncementServer\bin\publish-win-x64"
$packageName = "NTE-GUITAR-AnnouncementServer-win-x64"
$packageDir = Join-Path $OutputDirectory $packageName
$archivePath = Join-Path $OutputDirectory "$packageName.zip"

if (-not (Test-Path $project)) { throw "公告服务项目不存在：$project" }
if (-not (Test-Path $OutputDirectory)) { New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null }
if (Test-Path $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
if (Test-Path $packageDir) { Remove-Item -LiteralPath $packageDir -Recurse -Force }
if (Test-Path $archivePath) { Remove-Item -LiteralPath $archivePath -Force }

$dotnetExe = "dotnet"
$dotnetRoot = $env:DOTNET_ROOT
if ($dotnetRoot -and (Test-Path (Join-Path $dotnetRoot "dotnet.exe"))) {
    $dotnetExe = Join-Path $dotnetRoot "dotnet.exe"
} elseif (Test-Path (Join-Path $env:USERPROFILE ".dotnet\dotnet.exe")) {
    $dotnetExe = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
}

& $dotnetExe publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "公告服务发布失败，退出代码 $LASTEXITCODE" }

New-Item -ItemType Directory -Path $packageDir -Force | Out-Null
Copy-Item -Path (Join-Path $publishDir "*") -Destination $packageDir -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "AnnouncementServer\README.md") -Destination $packageDir -Force
Compress-Archive -Path (Join-Path $packageDir "*") -DestinationPath $archivePath -CompressionLevel Optimal

Write-Host "公告服务压缩包已生成：$archivePath"
