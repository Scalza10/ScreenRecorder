<#
.SYNOPSIS
  Downloads the pinned FFmpeg build, verifies its SHA-256, and extracts ffmpeg.exe / ffprobe.exe
  into <repo>/tools/ffmpeg. Safe to re-run: does nothing if the pinned version is already installed.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\setup.ps1
#>
[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot  = Split-Path -Parent $PSScriptRoot
$lockPath  = Join-Path $PSScriptRoot 'ffmpeg.lock.json'
$toolsDir  = Join-Path $repoRoot 'tools\ffmpeg'
$stampPath = Join-Path $toolsDir 'VERSION'

$lock = Get-Content -Raw -Path $lockPath | ConvertFrom-Json

if (-not $Force -and (Test-Path $stampPath) -and ((Get-Content -Raw $stampPath).Trim() -eq $lock.sha256)) {
    Write-Host "FFmpeg $($lock.version) already installed in $toolsDir"
    exit 0
}

$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("screenrecorder-ffmpeg-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $tempDir | Out-Null
try {
    $zipPath = Join-Path $tempDir 'ffmpeg.zip'
    Write-Host "Downloading FFmpeg $($lock.version) from $($lock.url)"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is very slow with the progress bar on 5.1
    Invoke-WebRequest -Uri $lock.url -OutFile $zipPath -UseBasicParsing

    $actual = (Get-FileHash -Algorithm SHA256 -Path $zipPath).Hash.ToLowerInvariant()
    if ($actual -ne $lock.sha256.ToLowerInvariant()) {
        throw "SHA-256 mismatch! expected $($lock.sha256) but got $actual. Refusing to install."
    }
    Write-Host "SHA-256 verified: $actual"

    $extractDir = Join-Path $tempDir 'x'
    Expand-Archive -Path $zipPath -DestinationPath $extractDir
    $bin = Get-ChildItem -Path $extractDir -Recurse -Filter 'ffmpeg.exe' | Select-Object -First 1
    if ($null -eq $bin) { throw 'ffmpeg.exe not found inside the archive.' }

    if (Test-Path $toolsDir) { Remove-Item -Recurse -Force $toolsDir }
    New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
    foreach ($exe in 'ffmpeg.exe', 'ffprobe.exe') {
        Copy-Item -Path (Join-Path $bin.DirectoryName $exe) -Destination $toolsDir
    }
    $license = Get-ChildItem -Path $extractDir -Recurse -Filter 'LICENSE*' | Select-Object -First 1
    if ($null -ne $license) { Copy-Item $license.FullName (Join-Path $toolsDir 'FFMPEG-LICENSE.txt') }

    Set-Content -Path $stampPath -Value $lock.sha256 -Encoding ASCII
    Write-Host "FFmpeg $($lock.version) installed to $toolsDir"
}
finally {
    Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue
}
