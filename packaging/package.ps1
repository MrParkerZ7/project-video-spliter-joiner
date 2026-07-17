#Requires -Version 5.1
<#
.SYNOPSIS
    Build a self-contained, single-file win-x64 distributable of VideoSplitJoiner with a
    bundled ffmpeg SHARED build (DLLs the FFME preview P/Invokes + the exes the split/join
    engine shells out to), then zip it (T-010, T-025).

.DESCRIPTION
    Steps:
      1. `dotnet publish` the WPF app single-file / self-contained into dist/publish/.
      2. Copy the ffmpeg SHARED build into dist/publish/ffmpeg/ (from -FfmpegSource):
         all shared *.dll (avcodec-61, avformat-61, avutil-59, avfilter-10, avdevice-61,
         swscale-8, swresample-5, postproc-58) AND ffmpeg.exe + ffprobe.exe. This single
         folder satisfies BOTH the app's FFME Library.FFmpegDirectory (= <BaseDirectory>/ffmpeg,
         finds the DLLs) AND the engine's FfmpegBinaryLocator (app-local ffmpeg/, finds the exes).
      3. Copy the ffmpeg LICENSE (if present) + THIRD-PARTY-NOTICES.md into dist/publish/.
      4. Zip dist/publish/ -> dist/VideoSplitJoiner-v<Version>-win-x64.zip.

    The <Version> is read from Directory.Build.props. Every step echoes; the script fails
    loudly (throws) if the ffmpeg source is missing — with a pointer to the fetch script.

    Note: ffmpeg binaries are NOT committed to the repo. They are copied in here at package
    time from -FfmpegSource. See THIRD-PARTY-NOTICES.md for the GPL licensing caveat.

.PARAMETER FfmpegSource
    Folder containing the ffmpeg SHARED build (shared *.dll + ffmpeg.exe + ffprobe.exe).
    Defaults to the repo-local ffmpeg-shared/ folder populated by
    packaging/fetch-ffmpeg-shared.ps1 (or .sh). To ship an LGPL (permissive) release,
    point this at an LGPL shared build's folder instead.

.PARAMETER Dotnet
    Path to the dotnet executable (dotnet is NOT on PATH in this environment).

.EXAMPLE
    powershell -File packaging/package.ps1
.EXAMPLE
    powershell -File packaging/package.ps1 -FfmpegSource "C:\ffmpeg-lgpl-shared"
#>
[CmdletBinding()]
param(
    [string]$FfmpegSource,
    [string]$Dotnet       = 'D:\_env_storeage\dotnet\dotnet.exe'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step { param([string]$Msg) Write-Host "==> $Msg" -ForegroundColor Cyan }

# --- Resolve paths -----------------------------------------------------------
$RepoRoot   = Split-Path -Parent $PSScriptRoot
$AppProj    = Join-Path $RepoRoot 'src\App\VideoSplitJoiner.App.csproj'
$PropsFile  = Join-Path $RepoRoot 'Directory.Build.props'
$NoticesSrc = Join-Path $RepoRoot 'THIRD-PARTY-NOTICES.md'
$DistDir    = Join-Path $RepoRoot 'dist'
$PublishDir = Join-Path $DistDir  'publish'

# Default the ffmpeg source to the repo-local SHARED build (populated by
# packaging/fetch-ffmpeg-shared.ps1). One folder serves BOTH the FFME preview
# (shared DLLs) AND the split/join engine (ffmpeg.exe / ffprobe.exe).
if ([string]::IsNullOrWhiteSpace($FfmpegSource)) {
    $FfmpegSource = Join-Path $RepoRoot 'ffmpeg-shared'
}

Write-Step "Repo root: $RepoRoot"

# --- Read <Version> from Directory.Build.props -------------------------------
if (-not (Test-Path $PropsFile)) { throw "Directory.Build.props not found at '$PropsFile'." }
$Version = ([xml](Get-Content -Raw $PropsFile)).Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($Version)) { throw "Could not read <Version> from '$PropsFile'." }
Write-Step "Version: $Version"

# --- Validate ffmpeg SHARED source BEFORE doing expensive publish ------------
# The shared build must supply BOTH the shared *.dll (for the FFME preview) AND
# ffmpeg.exe / ffprobe.exe (for the engine). Fail loudly with a fetch pointer so
# we never produce a silently-broken package.
$FetchHint = "Run  packaging/fetch-ffmpeg-shared.ps1  (or .sh) to (re)download the ffmpeg SHARED build."
if (-not (Test-Path $FfmpegSource)) {
    throw "ffmpeg SHARED source folder not found: '$FfmpegSource'. $FetchHint"
}
$FfmpegExe  = Join-Path $FfmpegSource 'ffmpeg.exe'
$FfprobeExe = Join-Path $FfmpegSource 'ffprobe.exe'
if (-not (Test-Path $FfmpegExe))  { throw "ffmpeg.exe not found in -FfmpegSource '$FfmpegSource'. $FetchHint" }
if (-not (Test-Path $FfprobeExe)) { throw "ffprobe.exe not found in -FfmpegSource '$FfmpegSource'. $FetchHint" }

$SharedDlls = @(Get-ChildItem -Path $FfmpegSource -Filter '*.dll' -File)
if ($SharedDlls.Count -eq 0) {
    throw "No shared *.dll found in -FfmpegSource '$FfmpegSource' — this is not a SHARED build. $FetchHint"
}
# Sanity-check the ABI marker the FFME preview binds to (ffmpeg 7.x = avcodec-61).
if (-not ($SharedDlls | Where-Object { $_.Name -like 'avcodec-*.dll' })) {
    throw "avcodec-*.dll missing from -FfmpegSource '$FfmpegSource' — the FFME preview will not load. $FetchHint"
}
Write-Step "ffmpeg SHARED source OK: $FfmpegSource ($($SharedDlls.Count) DLLs + ffmpeg.exe + ffprobe.exe)"

# --- Clean prior publish output ---------------------------------------------
if (Test-Path $PublishDir) {
    Write-Step "Removing prior publish output: $PublishDir"
    Remove-Item -Recurse -Force $PublishDir
}
New-Item -ItemType Directory -Force -Path $PublishDir | Out-Null

# --- 1. dotnet publish (single-file, self-contained, win-x64) ----------------
Write-Step "Publishing (single-file, self-contained, win-x64) ..."
if (-not (Test-Path $Dotnet)) { throw "dotnet not found at '$Dotnet'." }
& $Dotnet publish $AppProj -c Release -r win-x64 -p:PublishSingleFile=true -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

$AppExe = Join-Path $PublishDir 'VideoSplitJoiner.App.exe'
if (-not (Test-Path $AppExe)) { throw "Published exe not found at '$AppExe'." }
$AppExeMb = [math]::Round((Get-Item $AppExe).Length / 1048576, 1)
Write-Step "Published exe: $AppExe ($AppExeMb MB)"

# --- 2. Bundle the ffmpeg SHARED build next to the exe ----------------------
# Copy all shared *.dll (for FFME's Library.FFmpegDirectory) AND ffmpeg.exe /
# ffprobe.exe (for the engine's FfmpegBinaryLocator) into one ffmpeg/ folder.
$FfmpegDest = Join-Path $PublishDir 'ffmpeg'
New-Item -ItemType Directory -Force -Path $FfmpegDest | Out-Null
Write-Step "Copying ffmpeg SHARED build ($($SharedDlls.Count) DLLs + ffmpeg.exe + ffprobe.exe) -> $FfmpegDest"
$SharedDlls | Copy-Item -Destination $FfmpegDest -Force
Copy-Item $FfmpegExe  -Destination $FfmpegDest -Force
Copy-Item $FfprobeExe -Destination $FfmpegDest -Force

# --- 3. Copy licenses / notices ---------------------------------------------
Write-Step "Copying THIRD-PARTY-NOTICES.md + ffmpeg LICENSE"
if (-not (Test-Path $NoticesSrc)) { throw "THIRD-PARTY-NOTICES.md not found at '$NoticesSrc'." }
Copy-Item $NoticesSrc -Destination (Join-Path $PublishDir 'THIRD-PARTY-NOTICES.md') -Force

# ffmpeg LICENSE: check inside the source folder first (some shared builds ship it
# alongside the binaries), then one level up (essentials-build layout).
$FfmpegLicense = @(
    (Join-Path $FfmpegSource 'LICENSE'),
    (Join-Path $FfmpegSource 'LICENSE.txt'),
    (Join-Path (Split-Path -Parent $FfmpegSource) 'LICENSE')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($FfmpegLicense) {
    Copy-Item $FfmpegLicense -Destination (Join-Path $PublishDir 'LICENSE') -Force
    Write-Step "Bundled ffmpeg LICENSE (from $FfmpegLicense)"
} else {
    Write-Warning "ffmpeg LICENSE not found near '$FfmpegSource' - skipped. The GPL text is referenced in THIRD-PARTY-NOTICES.md; ensure attribution ships."
}

# --- 4. Zip ------------------------------------------------------------------
$ZipName = "VideoSplitJoiner-v$Version-win-x64.zip"
$ZipPath = Join-Path $DistDir $ZipName
if (Test-Path $ZipPath) { Remove-Item -Force $ZipPath }
Write-Step "Zipping $PublishDir -> $ZipPath"
Compress-Archive -Path (Join-Path $PublishDir '*') -DestinationPath $ZipPath -CompressionLevel Optimal

$ZipMb = [math]::Round((Get-Item $ZipPath).Length / 1048576, 1)
Write-Step "Done. Zip: $ZipPath ($ZipMb MB)"
Write-Host ""
Write-Host "Package tree:" -ForegroundColor Green
Get-ChildItem -Recurse $PublishDir | ForEach-Object {
    if (-not $_.PSIsContainer) {
        $rel = $_.FullName.Substring($PublishDir.Length).TrimStart('\')
        $kb  = [math]::Round($_.Length / 1024, 0)
        Write-Host ("  " + $rel.PadRight(45) + " " + $kb + " KB")
    }
}
