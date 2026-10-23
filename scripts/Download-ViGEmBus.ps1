param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "..\dist\dependencies")
)

$ErrorActionPreference = "Stop"
$version = "1.22.0"
$assetName = "ViGEmBus_1.22.0_x64_x86_arm64.exe"
$expectedSha256 = "89220a7865076b342892f98865f3499fb7c4cfd673159e89d352c360fd014c6a"
$url = "https://github.com/nefarius/ViGEmBus/releases/download/v$version/$assetName"
$destinationRoot = [IO.Path]::GetFullPath($OutputDirectory)
$destination = Join-Path $destinationRoot $assetName

New-Item -ItemType Directory -Path $destinationRoot -Force | Out-Null
Invoke-WebRequest -Uri $url -OutFile $destination
$actualSha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualSha256 -ne $expectedSha256) {
    Remove-Item -LiteralPath $destination -Force
    throw "SHA-256 verification failed for $assetName. Expected $expectedSha256; got $actualSha256."
}

Write-Host "Verified upstream ViGEmBus $version installer: $destination"
Write-Host "SHA-256: $actualSha256"
Write-Host "The installer was not executed. Review the upstream release notes and install manually if required."
