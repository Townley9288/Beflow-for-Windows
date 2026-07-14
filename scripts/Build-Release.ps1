param(
    [string]$Version = '1.0.0',
    [string]$FfmpegArchiveUrl = $env:FFMPEG_ARCHIVE_URL
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$Artifacts = Join-Path $Root 'artifacts'
$Publish = Join-Path $Artifacts 'publish'
$Portable = Join-Path $Artifacts 'portable'
$Release = Join-Path $Artifacts 'release'

function Assert-ChildPath([string]$Path) {
    $ResolvedRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $ResolvedPath = [IO.Path]::GetFullPath($Path)
    if (-not $ResolvedPath.StartsWith($ResolvedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to modify path outside repository: $ResolvedPath" }
}

foreach ($Directory in @($Artifacts, $Publish, $Portable, $Release)) { Assert-ChildPath $Directory }
if (Test-Path -LiteralPath $Artifacts) { Remove-Item -LiteralPath $Artifacts -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Publish, $Portable, $Release | Out-Null

function Invoke-Checked([scriptblock]$Command, [string]$Description) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Description failed with exit code $LASTEXITCODE" }
}

$Solution = Join-Path $Root 'BBDown-for-Windows.sln'
$AppProject = Join-Path $Root 'src\BBDownForWindows.App\BBDownForWindows.App.csproj'
Invoke-Checked { dotnet restore $Solution --locked-mode } 'Solution restore'
Invoke-Checked { dotnet restore $AppProject -r win-x64 --locked-mode } 'Win-x64 runtime restore'
Invoke-Checked { dotnet test $Solution -c Release -p:Platform=x64 --no-restore } 'Tests'
Invoke-Checked { dotnet publish $AppProject -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:Version=$Version -o $Publish --no-restore } 'Publish'

$RequiredPublishFiles = @(
    'BBDownForWindows.exe',
    'BBDownForWindows.pri',
    'App.xbf',
    'MainWindow.xbf',
    'Pages\DownloadPage.xbf',
    'Pages\DualAudioPage.xbf',
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

& (Join-Path $PSScriptRoot 'AcquireTools.ps1') -OutputDirectory $Publish -FfmpegArchiveUrl $FfmpegArchiveUrl
Copy-Item -LiteralPath (Join-Path $Root 'LICENSE'), (Join-Path $Root 'THIRD_PARTY_NOTICES.md'), (Join-Path $Root 'README.md') -Destination $Publish

Copy-Item -Path (Join-Path $Publish '*') -Destination $Portable -Recurse -Force
New-Item -ItemType File -Force -Path (Join-Path $Portable 'portable.flag') | Out-Null
$PortableZip = Join-Path $Release "BBDown-for-Windows-v$Version-win-x64-portable.zip"
Compress-Archive -Path (Join-Path $Portable '*') -DestinationPath $PortableZip -CompressionLevel Optimal

$IsccCandidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles(x86)\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)
$Iscc = $IsccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if ($Iscc) {
    $ChineseMessages = Join-Path $Artifacts 'ChineseSimplified.isl'
    Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/jrsoftware/issrc/main/Files/Languages/ChineseSimplified.isl' -OutFile $ChineseMessages
    Invoke-Checked { & $Iscc "/DMyAppVersion=$Version" "/DSourceDir=$Publish" "/DOutputDir=$Release" "/DChineseMessages=$ChineseMessages" (Join-Path $Root 'installer\BBDownForWindows.iss') } 'Inno Setup compile'
} else {
    Write-Warning 'Inno Setup 6 was not found; portable package was created but installer was skipped.'
}

Get-ChildItem -LiteralPath $Release -File | ForEach-Object {
    $Hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
    Set-Content -LiteralPath ($_.FullName + '.sha256') -Value "$Hash  $($_.Name)" -Encoding ascii
}
Get-ChildItem -LiteralPath $Release -File | Select-Object Name, Length | Format-Table -AutoSize
