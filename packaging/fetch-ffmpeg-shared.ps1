<#
.SYNOPSIS
    Fetch the ffmpeg SHARED build required by the FFME video preview (T-019).

.DESCRIPTION
    FFME.Windows 7.0.361-beta.1 binds to FFmpeg.AutoGen 7.0.0, i.e. the ffmpeg 7.x
    ABI (avcodec-61 / avformat-61 / avutil-59 / ...). It does NOT ship the native
    libraries. This script downloads a matching ffmpeg 7.1 SHARED win64 build from
    BtbN and lays the required DLLs + ffmpeg.exe / ffprobe.exe FLAT into the
    repo-local  ffmpeg-shared/  folder.

    These binaries are gitignored (see root .gitignore). Every dev / CI box that
    wants to run the FFME preview must run this script once.

    App startup (src/App/App.xaml.cs) points Unosquare.FFME.Library.FFmpegDirectory
    at this folder (in dev) so FFME can P/Invoke-load the shared libs.

.NOTES
    Source : https://github.com/BtbN/FFmpeg-Builds/releases (latest, n7.1 gpl shared)
    Verify : the resulting ffmpeg-shared/ must contain avcodec-61.dll (ffmpeg major 7).
#>
[CmdletBinding()]
param(
    [string] $Url  = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n7.1-latest-win64-gpl-shared-7.1.zip",
    [string] $Dest = (Join-Path $PSScriptRoot ".." | Join-Path -ChildPath "ffmpeg-shared")
)

$ErrorActionPreference = "Stop"

$Dest = [System.IO.Path]::GetFullPath($Dest)
Write-Host "Fetching ffmpeg SHARED build (ffmpeg 7.x, for FFME preview)..."
Write-Host "  URL : $Url"
Write-Host "  Dest: $Dest"

$tmp    = New-Item -ItemType Directory -Path ([System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), [System.Guid]::NewGuid().ToString()))
$zip    = Join-Path $tmp "ffmpeg-shared.zip"

try {
    Invoke-WebRequest -Uri $Url -OutFile $zip
    Expand-Archive -Path $zip -DestinationPath $tmp -Force

    $bin = Get-ChildItem -Path $tmp -Directory -Recurse |
           Where-Object { $_.Name -eq "bin" } |
           Select-Object -First 1
    if (-not $bin) { throw "Could not find bin/ folder inside the extracted archive." }

    New-Item -ItemType Directory -Path $Dest -Force | Out-Null

    # Copy the shared DLLs plus ffmpeg.exe / ffprobe.exe FLAT into ffmpeg-shared/.
    Get-ChildItem -Path $bin.FullName -Filter *.dll | Copy-Item -Destination $Dest -Force
    foreach ($exe in @("ffmpeg.exe", "ffprobe.exe")) {
        $src = Join-Path $bin.FullName $exe
        if (Test-Path $src) { Copy-Item $src -Destination $Dest -Force }
    }

    if (-not (Get-ChildItem -Path $Dest -Filter "avcodec-*.dll")) {
        throw "avcodec-*.dll missing after copy — the FFME preview will not load."
    }

    Write-Host ""
    Write-Host "Done. ffmpeg-shared/ now contains:"
    Get-ChildItem -Path $Dest | Select-Object -ExpandProperty Name | ForEach-Object { Write-Host "  $_" }
    Write-Host ""
    Write-Host "These files are gitignored and MUST NOT be committed."
}
finally {
    Remove-Item -Path $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
