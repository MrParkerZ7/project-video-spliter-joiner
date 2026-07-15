#Requires -Version 5.1
<#
.SYNOPSIS
    Build a self-contained, single-file win-x64 distributable of VideoSplitJoiner with a
    bundled ffmpeg, then zip it (T-010).

.DESCRIPTION
    Steps:
      1. `dotnet publish` the WPF app single-file / self-contained into dist/publish/.
      2. Copy ffmpeg.exe + ffprobe.exe into dist/publish/ffmpeg/ (from -FfmpegSource).
      3. Copy the ffmpeg LICENSE + THIRD-PARTY-NOTICES.md into dist/publish/.
      4. Zip dist/publish/ -> dist/VideoSplitJoiner-v<Version>-win-x64.zip.

    The <Version> is read from Directory.Build.props. Every step echoes; the script fails
    loudly (throws) if the ffmpeg source binaries are missing.

    Note: ffmpeg binaries are NOT committed to the repo. They are copied in here at package
    time from -FfmpegSource. See THIRD-PARTY-NOTICES.md for the GPL licensing caveat.

.PARAMETER FfmpegSource
    Folder containing ffmpeg.exe + ffprobe.exe. Defaults to the local essentials build.

.PARAMETER Dotnet
    Path to the dotnet executable (dotnet is NOT on PATH in this environment).

.EXAMPLE
    powershell -File packaging/package.ps1
.EXAMPLE
    powershell -File packaging/package.ps1 -FfmpegSource "C:\ffmpeg-lgpl\bin"
#>
[CmdletBinding()]
param(
    [string]$FfmpegSource = 'D:\_env_storeage\ffmpeg-7.1.1-essentials_build\bin',
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

Write-Step "Repo root: $RepoRoot"

# --- Read <Version> from Directory.Build.props -------------------------------
if (-not (Test-Path $PropsFile)) { throw "Directory.Build.props not found at '$PropsFile'." }
$Version = ([xml](Get-Content -Raw $PropsFile)).Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($Version)) { throw "Could not read <Version> from '$PropsFile'." }
Write-Step "Version: $Version"

# --- Validate ffmpeg source BEFORE doing expensive publish -------------------
$FfmpegExe = Join-Path $FfmpegSource 'ffmpeg.exe'
$FfprobeExe = Join-Path $FfmpegSource 'ffprobe.exe'
if (-not (Test-Path $FfmpegExe))  { throw "ffmpeg.exe not found in -FfmpegSource '$FfmpegSource'." }
if (-not (Test-Path $FfprobeExe)) { throw "ffprobe.exe not found in -FfmpegSource '$FfmpegSource'." }
Write-Step "ffmpeg source OK: $FfmpegSource"

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

# --- 2. Bundle ffmpeg next to the exe ---------------------------------------
$FfmpegDest = Join-Path $PublishDir 'ffmpeg'
New-Item -ItemType Directory -Force -Path $FfmpegDest | Out-Null
Write-Step "Copying ffmpeg.exe + ffprobe.exe -> $FfmpegDest"
Copy-Item $FfmpegExe  -Destination $FfmpegDest -Force
Copy-Item $FfprobeExe -Destination $FfmpegDest -Force

# --- 3. Copy licenses / notices ---------------------------------------------
Write-Step "Copying THIRD-PARTY-NOTICES.md + ffmpeg LICENSE"
if (-not (Test-Path $NoticesSrc)) { throw "THIRD-PARTY-NOTICES.md not found at '$NoticesSrc'." }
Copy-Item $NoticesSrc -Destination (Join-Path $PublishDir 'THIRD-PARTY-NOTICES.md') -Force

# ffmpeg LICENSE lives one level up from the bin folder in the essentials build.
$FfmpegLicense = Join-Path (Split-Path -Parent $FfmpegSource) 'LICENSE'
if (Test-Path $FfmpegLicense) {
    Copy-Item $FfmpegLicense -Destination (Join-Path $PublishDir 'LICENSE') -Force
    Write-Step "Bundled ffmpeg LICENSE"
} else {
    Write-Warning "ffmpeg LICENSE not found at '$FfmpegLicense' - skipped. Ensure attribution ships."
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
