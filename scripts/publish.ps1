<#
.SYNOPSIS
  Builds a portable, self-contained ScreenRecorder into <repo>/publish, with FFmpeg alongside the exe.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$outDir   = Join-Path $repoRoot 'publish'

& (Join-Path $PSScriptRoot 'setup.ps1')
if ($LASTEXITCODE -ne 0) { throw 'setup.ps1 failed' }

dotnet publish (Join-Path $repoRoot 'src\ScreenRecorder.App\ScreenRecorder.App.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $outDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Copy-Item -Path (Join-Path $repoRoot 'tools\ffmpeg\*') -Destination $outDir -Force
Write-Host "Published to $outDir"
