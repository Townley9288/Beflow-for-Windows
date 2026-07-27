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

function Build-BBDownWithBeflowQualityPatch([string]$SourceArchive, $Entry) {
    $SourceExpanded = Expand-VerifiedArchive $SourceArchive "bbdown-source-$($Entry.commit.Substring(0, 7))"
    $SourceRoot = Get-ChildItem -LiteralPath $SourceExpanded -Directory |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'BBDown\BBDown.csproj') -PathType Leaf } |
        Select-Object -First 1
    if (-not $SourceRoot) { throw 'The verified BBDown source archive does not contain BBDown\BBDown.csproj.' }

    $BuildRoot = Join-Path $CacheDirectory "expanded\bbdown-$($Entry.version)"
    $Publish = Join-Path $BuildRoot 'publish'
    $Executable = Join-Path $Publish 'BBDown.exe'
    $Marker = Join-Path $BuildRoot 'beflow-quality-patch.complete'
    if ((Test-Path -LiteralPath $Executable -PathType Leaf) -and (Test-Path -LiteralPath $Marker -PathType Leaf)) { return $Publish }

    $WorkingDirectory = Join-Path $CacheDirectory "work\bbdown-$($Entry.version)-$PID"
    New-Item -ItemType Directory -Force -Path $WorkingDirectory, $Publish | Out-Null
    Copy-Item -Path (Join-Path $SourceRoot.FullName '*') -Destination $WorkingDirectory -Recurse -Force

    $ConfigPath = Join-Path $WorkingDirectory 'BBDown.Core\Config.cs'
    $ConfigContent = [IO.File]::ReadAllText($ConfigPath)
    $Needle = '{"127","8K 超高清" }, {"126","杜比视界" }, {"125","HDR 真彩" }, {"120","4K 超清" }'
    $Replacement = '{"127","8K 超高清" }, {"126","杜比视界" }, {"125","HDR 真彩" }, {"122","4K·SDR增强" }, {"120","4K 超清" }'
    if (-not $ConfigContent.Contains($Needle)) { throw 'The pinned BBDown quality table changed; refusing to apply an unverified source patch.' }
    $ConfigContent = $ConfigContent.Replace($Needle, $Replacement)
    [IO.File]::WriteAllText($ConfigPath, $ConfigContent, [Text.UTF8Encoding]::new($false))

    $Project = Join-Path $WorkingDirectory 'BBDown\BBDown.csproj'
    & dotnet publish $Project -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:ManagePackageVersionsCentrally=false -p:Version=$($Entry.version) -o $Publish | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Patched BBDown build failed with exit code $LASTEXITCODE" }
    if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) { throw 'Patched BBDown build did not produce BBDown.exe.' }
    [IO.File]::WriteAllText($Marker, "source=$($Entry.commit)`nquality=122:4K·SDR增强`n", [Text.UTF8Encoding]::new($false))
    return $Publish
}

$BBDownSourceArchive = Get-Archive $Manifest.bbdownSource
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

$BBDownExpanded = Build-BBDownWithBeflowQualityPatch $BBDownSourceArchive $Manifest.bbdownSource
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
