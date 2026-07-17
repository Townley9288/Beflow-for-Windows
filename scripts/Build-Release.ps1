param(
    [string]$Version = '1.0.5.0',
    [string]$FfmpegArchiveUrl = $env:FFMPEG_ARCHIVE_URL,
    [switch]$RequireNativeUpdater
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Artifacts = Join-Path $Root 'artifacts'
$BuildRoot = Join-Path $Artifacts "build-$Version-$PID"
$Publish = Join-Path $BuildRoot 'publish'
$Portable = Join-Path $BuildRoot 'portable'
$UpdaterPublish = Join-Path $BuildRoot 'updater'
$Release = Join-Path $Artifacts 'release'

function Assert-ChildPath([string]$Path) {
    $ResolvedRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $ResolvedPath = [IO.Path]::GetFullPath($Path)
    if (-not $ResolvedPath.StartsWith($ResolvedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to modify path outside repository: $ResolvedPath" }
}

foreach ($Directory in @($Artifacts, $BuildRoot, $Publish, $Portable, $UpdaterPublish, $Release)) { Assert-ChildPath $Directory }
if (Test-Path -LiteralPath $BuildRoot) { Remove-Item -LiteralPath $BuildRoot -Recurse -Force }
if (Test-Path -LiteralPath $Release) { Remove-Item -LiteralPath $Release -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Publish, $Portable, $UpdaterPublish, $Release | Out-Null

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
Invoke-Checked { dotnet restore $UpdaterProject -r win-x64 --locked-mode } 'Updater restore'
Invoke-Checked { dotnet test $Solution -c Release -p:Platform=x64 --no-restore } 'Tests'
Invoke-Checked { dotnet publish $AppProject -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:Version=$Version -p:ApplicationManifest=$GeneratedManifest -o $Publish --no-restore } 'Publish'

& dotnet publish $UpdaterProject -c Release -r win-x64 --self-contained true -p:Version=$Version -o $UpdaterPublish --no-restore
if ($LASTEXITCODE -ne 0) {
    if ($RequireNativeUpdater) { throw "Native AOT updater publish failed with exit code $LASTEXITCODE" }
    Write-Warning 'Native AOT updater publish failed; creating a managed self-contained single-file updater for this local build.'
    if (Test-Path -LiteralPath $UpdaterPublish) { Remove-Item -LiteralPath $UpdaterPublish -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $UpdaterPublish | Out-Null
    Invoke-Checked { dotnet publish $UpdaterProject -c Release -r win-x64 --self-contained true -p:Version=$Version -p:PublishAot=false -p:PublishSingleFile=true -p:PublishTrimmed=true -o $UpdaterPublish --no-restore } 'Managed updater fallback publish'
}
Copy-Item -LiteralPath (Join-Path $UpdaterPublish 'Beflow.Updater.exe') -Destination (Join-Path $Publish 'Beflow.Updater.exe') -Force

$RequiredPublishFiles = @(
    'Beflow.exe',
    'Beflow.Updater.exe',
    'Beflow.pri',
    'App.xbf',
    'MainWindow.xbf',
    'Pages\DownloadPage.xbf',
    'Pages\DualAudioPage.xbf',
    'Pages\RenamePage.xbf',
    'Pages\RenameTemplatesPage.xbf',
    'Pages\RenameHistoryPage.xbf',
    'Pages\HistoryPage.xbf',
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

$PortableManifestName = 'Beflow.files.txt'
$PortableManifestPath = Join-Path $Publish $PortableManifestName
$PortableManifestFiles = Get-ChildItem -LiteralPath $Publish -Recurse -File |
    ForEach-Object { [IO.Path]::GetRelativePath($Publish, $_.FullName) } |
    Sort-Object -Unique
$PortableManifestFiles += $PortableManifestName
[IO.File]::WriteAllLines($PortableManifestPath, $PortableManifestFiles, [Text.UTF8Encoding]::new($false))

Copy-Item -Path (Join-Path $Publish '*') -Destination $Portable -Recurse -Force
New-Item -ItemType File -Force -Path (Join-Path $Portable 'portable.flag') | Out-Null
$PortableZip = Join-Path $Release "Beflow-for-Windows-v$Version-win-x64-portable.zip"
Compress-Archive -Path (Join-Path $Portable '*') -DestinationPath $PortableZip -CompressionLevel Optimal

$IsccCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
$IsccCandidates = @(
    $IsccCommand.Source,
    "$env:ChocolateyInstall\bin\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)
$Iscc = $IsccCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
if ($Iscc) {
    $ChineseMessages = Join-Path $Artifacts 'ChineseSimplified.isl'
    $AppIconFile = Join-Path $Root 'src\BBDownForWindows.App\Assets\AppIcon.ico'
    Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/jrsoftware/issrc/main/Files/Languages/ChineseSimplified.isl' -OutFile $ChineseMessages
    Invoke-Checked { & $Iscc "/DMyAppVersion=$Version" "/DSourceDir=$Publish" "/DOutputDir=$Release" "/DChineseMessages=$ChineseMessages" "/DAppIconFile=$AppIconFile" (Join-Path $Root 'installer\BBDownForWindows.iss') } 'Inno Setup compile'
} elseif ($RequireNativeUpdater) {
    throw 'Inno Setup 6 was not found; official release builds require the setup installer.'
} else {
    Write-Warning 'Inno Setup 6 was not found; portable package was created but installer was skipped.'
}

Get-ChildItem -LiteralPath $Release -File | Where-Object { $_.Extension -in @('.exe', '.zip') } | ForEach-Object {
    $Hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
    Set-Content -LiteralPath ($_.FullName + '.sha256') -Value "$Hash  $($_.Name)" -Encoding ascii
}
Get-ChildItem -LiteralPath $Release -File | Select-Object Name, Length | Format-Table -AutoSize
Write-Host "Release staging directory: $BuildRoot"
