param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$CacheDirectory = "",
    [string]$FfmpegArchivePath = "",
    [string]$FfmpegArchiveUrl = $env:FFMPEG_ARCHIVE_URL
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
if (-not $CacheDirectory) { $CacheDirectory = Join-Path $Root 'tools\cache' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$CacheDirectory = [IO.Path]::GetFullPath($CacheDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory, $CacheDirectory | Out-Null
$Manifest = Get-Content -Raw -LiteralPath (Join-Path $Root 'tools\tools.json') | ConvertFrom-Json

function Assert-Hash([string]$Path, [string]$Expected) {
    $Actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
    if ($Actual -ne $Expected) { throw "SHA-256 mismatch for $Path. Expected $Expected, got $Actual" }
}

function Get-Archive($Entry, [string]$LocalCandidate = '', [string]$OverrideUrl = '') {
    $Destination = Join-Path $CacheDirectory $Entry.archive
    if (Test-Path -LiteralPath $Destination) {
        try { Assert-Hash $Destination $Entry.sha256; return $Destination } catch { Remove-Item -LiteralPath $Destination -Force }
    }
    if ($LocalCandidate -and (Test-Path -LiteralPath $LocalCandidate)) {
        Copy-Item -LiteralPath $LocalCandidate -Destination $Destination
    } else {
        $Url = if ($OverrideUrl) { $OverrideUrl } else { $Entry.url }
        if (-not $Url) { throw "No download URL or local archive is available for $($Entry.archive)" }
        Invoke-WebRequest -Uri $Url -OutFile $Destination
    }
    Assert-Hash $Destination $Entry.sha256
    return $Destination
}

function Expand-VerifiedArchive([string]$Archive, [string]$Name) {
    $Destination = Join-Path $CacheDirectory "expanded\$Name"
    if (-not (Test-Path -LiteralPath $Destination)) {
        New-Item -ItemType Directory -Force -Path $Destination | Out-Null
        Expand-Archive -LiteralPath $Archive -DestinationPath $Destination -Force
    }
    return $Destination
}

$BBDownArchive = Get-Archive $Manifest.bbdown
$AriaArchive = Get-Archive $Manifest.aria2
if (-not $FfmpegArchivePath) {
    $ArchiveName = $Manifest.ffmpeg.archive
    $LocalCandidates = @(
        (Join-Path (Split-Path -Parent (Split-Path -Parent $Root)) $ArchiveName),
        (Join-Path (Split-Path -Parent $Root) $ArchiveName)
    )
    foreach ($Drive in [IO.DriveInfo]::GetDrives() | Where-Object { $_.DriveType -eq [IO.DriveType]::Fixed -and $_.IsReady }) {
        $LocalCandidates += Join-Path $Drive.RootDirectory.FullName (Join-Path 'Software' $ArchiveName)
    }
    $FfmpegArchivePath = $LocalCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
$FfmpegArchive = Get-Archive $Manifest.ffmpeg $FfmpegArchivePath $FfmpegArchiveUrl

$BBDownExpanded = Expand-VerifiedArchive $BBDownArchive 'bbdown-1.6.3'
$AriaExpanded = Expand-VerifiedArchive $AriaArchive 'aria2-1.37.0'
$FfmpegExpanded = Expand-VerifiedArchive $FfmpegArchive 'ffmpeg-20240110'

$ToolsRoot = Join-Path $OutputDirectory 'tools'
$LicensesRoot = Join-Path $OutputDirectory 'licenses'
New-Item -ItemType Directory -Force -Path (Join-Path $ToolsRoot 'BBDown'), (Join-Path $ToolsRoot 'aria2'), (Join-Path $ToolsRoot 'ffmpeg'), $LicensesRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $BBDownExpanded 'BBDown.exe') -Destination (Join-Path $ToolsRoot 'BBDown\BBDown.exe') -Force
Copy-Item -LiteralPath (Join-Path $Root 'third_party\BBDown-LICENSE.txt') -Destination (Join-Path $LicensesRoot 'BBDown-LICENSE.txt') -Force
$AriaRoot = Get-ChildItem -LiteralPath $AriaExpanded -Directory | Select-Object -First 1
Copy-Item -LiteralPath (Join-Path $AriaRoot.FullName 'aria2c.exe') -Destination (Join-Path $ToolsRoot 'aria2\aria2c.exe') -Force
Copy-Item -LiteralPath (Join-Path $AriaRoot.FullName 'COPYING') -Destination (Join-Path $LicensesRoot 'aria2-COPYING.txt') -Force
$FfmpegRoot = Get-ChildItem -LiteralPath $FfmpegExpanded -Directory | Select-Object -First 1
Copy-Item -LiteralPath (Join-Path $FfmpegRoot.FullName 'bin\ffmpeg.exe') -Destination (Join-Path $ToolsRoot 'ffmpeg\ffmpeg.exe') -Force
Copy-Item -LiteralPath (Join-Path $FfmpegRoot.FullName 'bin\ffprobe.exe') -Destination (Join-Path $ToolsRoot 'ffmpeg\ffprobe.exe') -Force
Copy-Item -LiteralPath (Join-Path $FfmpegRoot.FullName 'LICENSE.txt') -Destination (Join-Path $LicensesRoot 'ffmpeg-LICENSE.txt') -Force

Write-Host "Runtime tools prepared in $ToolsRoot"
