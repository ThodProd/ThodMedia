<#
  Builds ThodMedia and packs it for ThodHUB.

  The script is intentionally self-contained: pinned tool versions, SHA-256
  verification, self-contained worker publish, staging, then ThodHUB packaging.

  Example:
    .\scripts\build.ps1
    .\scripts\build.ps1 -Version 1.0.1 -FfmpegZip D:\tools\ffmpeg.zip -ModelFile D:\tools\isnet-general-use.onnx
#>
[CmdletBinding()]
param(
    [string]$Version = '',
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = '',
    [string]$FfmpegZip = '',
    [string]$ModelFile = '',
    [switch]$SkipPackage
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$manifestPath = Join-Path $root 'hub\manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

if (-not $Version) { $Version = $manifest.version }
if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw "Version must look like 1.2.3, got '$Version'."
}

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts' }
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$cache = Join-Path $root '.cache'
$stage = Join-Path $OutputDirectory "stage-$Version"
$ffmpegSha = '60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba'
$modelSha = '60920e99c45464f2ba57bee2ad08c919a52bbf852739e96947fbb4358c0d964a'
$ffmpegUrl = 'https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip'
$modelUrl = 'https://github.com/danielgatis/rembg/releases/download/v0.0.0/isnet-general-use.onnx'

function Get-ToolFile {
    param([string]$Path, [string]$Url, [string]$Sha256, [string]$Name)
    if (-not $Path) {
        New-Item -ItemType Directory -Path $cache -Force | Out-Null
        $Path = Join-Path $cache $Name
    }
    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Host "Downloading $Name ..."
        Invoke-WebRequest -Uri $Url -OutFile $Path -UseBasicParsing
    }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Sha256) {
        throw "SHA-256 mismatch for $Name. Expected $Sha256, got $actual."
    }
    return (Resolve-Path -LiteralPath $Path).Path
}

Write-Host "ThodMedia $Version"
Write-Host "Workspace: $root"

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

# --- worker (self-contained) -------------------------------------------------
$workerProject = Join-Path $root 'src\ThodMedia.Worker\ThodMedia.Worker.csproj'
$workerOut = Join-Path $stage 'worker'
Write-Host 'Publishing worker (win-x64, self-contained) ...'
& dotnet publish $workerProject `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:Version=$Version `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:PublishSingleFile=false `
    -o $workerOut
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

# --- UI ---------------------------------------------------------------------
$uiOut = Join-Path $stage 'ui'
New-Item -ItemType Directory -Path $uiOut -Force | Out-Null
foreach ($file in 'index.html', 'styles.css', 'app.js', 'thodhub.js') {
    Copy-Item -LiteralPath (Join-Path $root "ui\$file") -Destination $uiOut -Force
}

# --- manifest ---------------------------------------------------------------
$manifest.version = $Version
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText((Join-Path $stage 'manifest.json'), ($manifest | ConvertTo-Json -Depth 10), $utf8NoBom)

# --- bundled docs / licenses ------------------------------------------------
foreach ($file in 'README.md', 'THIRD-PARTY-NOTICES.md', 'LICENSE') {
    Copy-Item -LiteralPath (Join-Path $root $file) -Destination $stage -Force
}
$licensesOut = Join-Path $stage 'licenses'
New-Item -ItemType Directory -Path $licensesOut -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'licenses\Apache-2.0.txt') -Destination $licensesOut -Force
Copy-Item -LiteralPath (Join-Path $root 'licenses\onnxruntime-MIT.txt') -Destination $licensesOut -Force
Copy-Item -LiteralPath (Join-Path $root 'licenses\FFMPEG-SOURCE-OFFER.txt') -Destination $licensesOut -Force

# --- ffmpeg -----------------------------------------------------------------
$ffmpegArchive = Get-ToolFile -Path $FfmpegZip -Url $ffmpegUrl -Sha256 $ffmpegSha -Name 'ffmpeg-9.0.2-essentials_build.zip'
$ffmpegExtract = Join-Path $cache 'ffmpeg-extract'
if (Test-Path -LiteralPath $ffmpegExtract) { Remove-Item -LiteralPath $ffmpegExtract -Recurse -Force }
Write-Host 'Extracting ffmpeg ...'
Expand-Archive -LiteralPath $ffmpegArchive -DestinationPath $ffmpegExtract -Force
$ffmpegBin = Get-ChildItem -LiteralPath $ffmpegExtract -Recurse -Filter 'ffmpeg.exe' | Select-Object -First 1
if (-not $ffmpegBin) { throw 'ffmpeg.exe was not found in the archive.' }
$runtimeOut = Join-Path $stage 'worker\runtime\ffmpeg'
New-Item -ItemType Directory -Path $runtimeOut -Force | Out-Null
Copy-Item -LiteralPath $ffmpegBin.FullName -Destination $runtimeOut -Force
$ffprobe = Join-Path $ffmpegBin.Directory.FullName 'ffprobe.exe'
if (-not (Test-Path -LiteralPath $ffprobe)) { throw 'ffprobe.exe was not found in the archive.' }
Copy-Item -LiteralPath $ffprobe -Destination $runtimeOut -Force
$ffmpegLicense = Join-Path (Split-Path $ffmpegBin.Directory.FullName -Parent) 'LICENSE'
if (Test-Path -LiteralPath $ffmpegLicense) {
    Copy-Item -LiteralPath $ffmpegLicense -Destination (Join-Path $licensesOut 'ffmpeg-COPYING.txt') -Force
} else {
    throw 'ffmpeg LICENSE was not found in the archive.'
}

# --- package ----------------------------------------------------------------
$zip = $null
if (-not $SkipPackage) {
    $packageScript = Join-Path $root 'scripts\package-module.ps1'
    $zip = & $packageScript `
        -PackageDirectory $stage `
        -Repository 'ThodProd/ThodMedia' `
        -OutputDirectory $OutputDirectory `
        -Notes "ThodMedia $Version - Конвертер фото, видео и аудио. Редактор с удалением фона перенесён в отдельный ThodCanvas."
    if (-not $zip -or -not (Test-Path -LiteralPath $zip)) { throw 'Packaging failed: ZIP was not produced.' }

    $releasePath = Join-Path $OutputDirectory 'thodhub-release.json'
    $release = Get-Content -LiteralPath $releasePath -Raw | ConvertFrom-Json
    $actualHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    $actualSize = (Get-Item -LiteralPath $zip).Length
    if ($release.sha256 -ne $actualHash -or [long]$release.size -ne $actualSize) {
        throw 'Packaging verification failed: SHA-256/size do not match thodhub-release.json.'
    }
    Write-Host ''
    Write-Host "Package : $zip"
    Write-Host "SHA-256 : $actualHash"
    Write-Host "Size    : $actualSize bytes"
    Write-Host "Release : $releasePath"
} else {
    Write-Host "Staged  : $stage"
}
