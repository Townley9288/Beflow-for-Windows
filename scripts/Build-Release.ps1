param(
    [string]$Version = '1.1.1.17',
    [string]$FfmpegArchiveUrl = $env:FFMPEG_ARCHIVE_URL,
    [string]$ReleaseDirectory
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '\A[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?\z') { throw "Invalid release version: $Version" }
$Root = Split-Path -Parent $PSScriptRoot
$Artifacts = Join-Path $Root 'artifacts'
$BuildRoot = Join-Path $Artifacts "build-$Version-$PID"
$Publish = Join-Path $BuildRoot 'publish'
$Release = if ($ReleaseDirectory) { [IO.Path]::GetFullPath($ReleaseDirectory, $Root) } else { Join-Path $Artifacts 'release' }
$Portable = Join-Path $BuildRoot 'portable'

function Assert-ChildPath([string]$Path) {
    $ResolvedRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $ResolvedPath = [IO.Path]::GetFullPath($Path)
    if (-not $ResolvedPath.StartsWith($ResolvedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to modify path outside repository: $ResolvedPath" }
}

foreach ($Directory in @($Artifacts, $BuildRoot, $Publish, $Portable, $Release)) { Assert-ChildPath $Directory }
if (Test-Path -LiteralPath $BuildRoot) { Remove-Item -LiteralPath $BuildRoot -Recurse -Force }

New-Item -ItemType Directory -Force -Path $Publish, $Release | Out-Null

function Invoke-Checked([scriptblock]$Command, [string]$Description) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Description failed with exit code $LASTEXITCODE" }
}

$Solution = Join-Path $Root 'BBDown-for-Windows.sln'
$AppProject = Join-Path $Root 'src\BBDownForWindows.App\BBDownForWindows.App.csproj'
$UpdaterProject = Join-Path $Root 'src\Beflow.Updater\Beflow.Updater.csproj'
$SourceManifest = Join-Path $Root 'src\BBDownForWindows.App\app.manifest'
$GeneratedManifest = Join-Path $BuildRoot 'app.manifest'
$ParsedVersion = [Version]::Parse($Version)
$ManifestVersion = "$($ParsedVersion.Major).$($ParsedVersion.Minor).$([Math]::Max(0, $ParsedVersion.Build)).$([Math]::Max(0, $ParsedVersion.Revision))"
$ManifestContent = Get-Content -Raw -LiteralPath $SourceManifest
$ManifestReplacement = '${1}' + $ManifestVersion + '${2}'
[Regex]::Replace($ManifestContent, '(<assemblyIdentity\s+version=")[^"]+(")', $ManifestReplacement) | Set-Content -LiteralPath $GeneratedManifest -Encoding utf8
Invoke-Checked { dotnet restore $Solution --locked-mode } 'Solution restore'
Invoke-Checked { dotnet restore $AppProject -r win-x64 --locked-mode } 'Win-x64 runtime restore'
Invoke-Checked { dotnet test $Solution -c Release -p:Platform=x64 --no-restore } 'Tests'
Invoke-Checked { dotnet publish $AppProject -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:Version=$Version -p:ApplicationManifest=$GeneratedManifest -o $Publish --no-restore } 'Publish'

$RequiredPublishFiles = @(
    'Beflow.exe',
    'Beflow.pri',
    'App.xbf',
    'MainWindow.xbf',
    'Controls\RenameHistoryDetailContent.xbf',
    'Pages\DownloadPage.xbf',
    'Pages\PersonalSpacePage.xbf',
    'Pages\DownloadQueuePage.xbf',
    'Pages\DualAudioPage.xbf',
    'Pages\RenamePage.xbf',
    'Pages\RenameTemplatesPage.xbf',
    'Pages\HistoryPage.xbf',
    'Pages\DownloadHistoryDetailPage.xbf',
    'Pages\SettingsPage.xbf',
    'Pages\AboutPage.xbf',
    'Assets\AppIcon.ico'
)
foreach ($RelativePath in $RequiredPublishFiles) {
    $PublishFile = Join-Path $Publish $RelativePath
    if (-not (Test-Path -LiteralPath $PublishFile -PathType Leaf)) {
        throw "Publish output is missing required WinUI resource: $RelativePath"
    }
}

# The Windows App SDK publish graph includes WebView2 even though this native
# application does not use WebView. Remove those unused runtime files so the
# shipped application has no browser-engine dependency.
foreach ($FileName in @('Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.Core.Projection.dll', 'WebView2Loader.dll')) {
    $WebViewFile = Join-Path $Publish $FileName
    if (Test-Path -LiteralPath $WebViewFile -PathType Leaf) { Remove-Item -LiteralPath $WebViewFile -Force }
}

# Debug symbols can contain local source paths and are not required by users.
Get-ChildItem -LiteralPath $Publish -Recurse -File -Filter '*.pdb' | Remove-Item -Force

# Windows App SDK self-contained publishing includes MUI resources for every
# supported locale. Beflow currently ships Chinese UI with English fallback,
# so keep only the locale resources needed by the supported audience.
$RetainedMuiLanguages = @('zh-CN', 'zh-TW', 'en-US')
$MuiLanguageDirectories = Get-ChildItem -LiteralPath $Publish -Directory | Where-Object {
    Get-ChildItem -LiteralPath $_.FullName -Recurse -File -Filter '*.mui' -ErrorAction SilentlyContinue | Select-Object -First 1
}
foreach ($Directory in $MuiLanguageDirectories) {
    if ($Directory.Name -notin $RetainedMuiLanguages) {
        Assert-ChildPath $Directory.FullName
        Remove-Item -LiteralPath $Directory.FullName -Recurse -Force
    }
}
$UnexpectedMuiLanguages = Get-ChildItem -LiteralPath $Publish -Directory | Where-Object {
    $_.Name -notin $RetainedMuiLanguages -and
    (Get-ChildItem -LiteralPath $_.FullName -Recurse -File -Filter '*.mui' -ErrorAction SilentlyContinue | Select-Object -First 1)
}
if ($UnexpectedMuiLanguages) {
    throw "Publish output still contains unsupported MUI language directories: $($UnexpectedMuiLanguages.Name -join ', ')"
}

& (Join-Path $PSScriptRoot 'AcquireTools.ps1') -OutputDirectory $Publish -FfmpegArchiveUrl $FfmpegArchiveUrl
Copy-Item -LiteralPath (Join-Path $Root 'LICENSE'), (Join-Path $Root 'THIRD_PARTY_NOTICES.md'), (Join-Path $Root 'THIRD_PARTY_SOURCES.md'), (Join-Path $Root 'README.md') -Destination $Publish

$IsccCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
$IsccCandidates = @(
    $IsccCommand.Source,
    "$env:ChocolateyInstall\bin\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)
$Iscc = $IsccCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
if (-not $Iscc) { throw 'Inno Setup 6 was not found; release builds require the setup installer.' }
$ChineseMessages = Join-Path $Root 'installer\ChineseSimplified.isl'
$AppIconFile = Join-Path $Root 'src\BBDownForWindows.App\Assets\AppIcon.ico'
Invoke-Checked { & $Iscc "/DMyAppVersion=$Version" "/DSourceDir=$Publish" "/DOutputDir=$Release" "/DChineseMessages=$ChineseMessages" "/DAppIconFile=$AppIconFile" (Join-Path $Root 'installer\BBDownForWindows.iss') } 'Inno Setup compile'

$AssetPrefix = "Beflow-for-Windows-v$Version-win-x64"
$Installer = Join-Path $Release "$AssetPrefix-setup.exe"
if (-not (Test-Path -LiteralPath $Installer -PathType Leaf)) { throw "Expected setup installer $Installer." }

New-Item -ItemType Directory -Path $Portable | Out-Null
Copy-Item -Path (Join-Path $Publish '*') -Destination $Portable -Recurse
$UpdaterPublish = Join-Path $BuildRoot 'updater'
Invoke-Checked { dotnet publish $UpdaterProject -c Release -r win-x64 -p:Version=$Version -o $UpdaterPublish --no-restore } 'Publish portable updater'
Copy-Item -LiteralPath (Join-Path $UpdaterPublish 'Beflow.Updater.exe') -Destination $Portable
New-Item -ItemType File -Path (Join-Path $Portable 'portable.flag') | Out-Null

$ManifestName = 'Beflow.files.txt'
$ManifestEntries = @(Get-ChildItem -LiteralPath $Portable -Recurse -File |
    Where-Object { $_.FullName -ne (Join-Path $Portable 'portable.flag') } |
    ForEach-Object { [IO.Path]::GetRelativePath($Portable, $_.FullName).Replace('\', '/') }) + $ManifestName
$ManifestEntries | Sort-Object | Set-Content -LiteralPath (Join-Path $Portable $ManifestName) -Encoding utf8
$ZipPath = Join-Path $Release "$AssetPrefix-portable.zip"
$StagedZip = Join-Path $BuildRoot "$AssetPrefix-portable.zip"
[IO.Compression.ZipFile]::CreateFromDirectory($Portable, $StagedZip, [IO.Compression.CompressionLevel]::Optimal, $false)
$Archive = [IO.Compression.ZipFile]::OpenRead($StagedZip)
try {
    $ExpectedEntries = @($ManifestEntries) + 'portable.flag'
    $ActualEntries = @($Archive.Entries | Where-Object { $_.Name } | ForEach-Object { $_.FullName.Replace('\', '/') })
    if (Compare-Object -ReferenceObject $ExpectedEntries -DifferenceObject $ActualEntries) {
        throw 'Portable ZIP entries do not match Beflow.files.txt and portable.flag.'
    }
    if ('Beflow.exe', 'Beflow.Updater.exe', 'tools/BBDown/BBDown.exe.sha256' | Where-Object { $_ -notin $ActualEntries }) {
        throw 'Portable ZIP is missing required executable files or the BBDown checksum.'
    }
}
finally { $Archive.Dispose() }
Move-Item -LiteralPath $StagedZip -Destination $ZipPath -Force
foreach ($Asset in @($Installer, $ZipPath)) {
    $Hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Asset).Hash
    Set-Content -LiteralPath ($Asset + '.sha256') -Value "$Hash  $([IO.Path]::GetFileName($Asset))" -Encoding ascii
}
Get-ChildItem -LiteralPath $Release -File -Filter "$AssetPrefix-*" | Select-Object Name, Length | Format-Table -AutoSize
Write-Host "Release staging directory: $BuildRoot"
