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

function Build-BBDownWithBeflowPatches([string]$SourceArchive, $Entry) {
    $SourceExpanded = Expand-VerifiedArchive $SourceArchive "bbdown-source-$($Entry.commit.Substring(0, 7))"
    $SourceRoot = Get-ChildItem -LiteralPath $SourceExpanded -Directory |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'BBDown\BBDown.csproj') -PathType Leaf } |
        Select-Object -First 1
    if (-not $SourceRoot) { throw 'The verified BBDown source archive does not contain BBDown\BBDown.csproj.' }

    $BuildRoot = Join-Path $CacheDirectory "expanded\bbdown-$($Entry.version)"
    $Publish = Join-Path $BuildRoot 'publish'
    $Executable = Join-Path $Publish 'BBDown.exe'
    $Marker = Join-Path $BuildRoot 'beflow-patches.complete'
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

    $Quality100Needle = '{"112","1080P 高码率" }, {"80","1080P 高清" }'
    $Quality100Replacement = '{"112","1080P 高码率" }, {"100","智能修复" }, {"80","1080P 高清" }'
    if (-not $ConfigContent.Contains($Quality100Needle)) { throw 'The pinned BBDown 1080P quality table changed; refusing to add qn 100.' }
    $ConfigContent = $ConfigContent.Replace($Quality100Needle, $Quality100Replacement)

    $ConfigMethodNeedle = '        };'
    if ([regex]::Matches($ConfigContent, [regex]::Escape($ConfigMethodNeedle)).Count -ne 1) { throw 'The pinned BBDown quality table terminator changed; refusing to add the safe quality lookup.' }
    $ConfigMethodReplacement = $ConfigMethodNeedle + [Environment]::NewLine + [Environment]::NewLine +
        '        public static string GetQualityName(string qualityId)' + [Environment]::NewLine +
        '        {' + [Environment]::NewLine +
        '            return qualitys.TryGetValue(qualityId, out var qualityName)' + [Environment]::NewLine +
        '                ? qualityName' + [Environment]::NewLine +
        '                : $"未知画质 {qualityId}";' + [Environment]::NewLine +
        '        }'
    $ConfigContent = $ConfigContent.Replace($ConfigMethodNeedle, $ConfigMethodReplacement)
    [IO.File]::WriteAllText($ConfigPath, $ConfigContent, [Text.UTF8Encoding]::new($false))

    $ParserPath = Join-Path $WorkingDirectory 'BBDown.Core\Parser.cs'
    $ParserContent = [IO.File]::ReadAllText($ParserPath)
    $ParserNeedle = 'apiBuilder.Append($"avid={aid}&cid={cid}&fnval=4048&fnver=0&fourk=1");'
    $ParserReplacement = 'string webFnval = bangumi ? "143312" : "4048";' + [Environment]::NewLine +
        '                apiBuilder.Append($"avid={aid}&cid={cid}&fnval={webFnval}&fnver=0&fourk=1");' + [Environment]::NewLine +
        '                if (bangumi) apiBuilder.Append("&drm_tech_type=3");'
    if (-not $ParserContent.Contains($ParserNeedle)) { throw 'The pinned BBDown WEB playurl request changed; refusing to apply an unverified source patch.' }
    $ParserContent = $ParserContent.Replace($ParserNeedle, $ParserReplacement)

    $IntlStreamNeedle = 'var videoId = stream.GetProperty("stream_info").GetProperty("quality").ToString();'
    $IntlStreamReplacement = 'var streamInfo = stream.GetProperty("stream_info");' + [Environment]::NewLine +
        '                            var videoId = streamInfo.GetProperty("quality").ToString();'
    if (-not $ParserContent.Contains($IntlStreamNeedle)) { throw 'The pinned BBDown international stream metadata changed; refusing to apply the safe quality lookup.' }
    $ParserContent = $ParserContent.Replace($IntlStreamNeedle, $IntlStreamReplacement)

    $VideoQualityIndex = [regex]::new('dfn = Config\.qualitys\[videoId\],')
    if ($VideoQualityIndex.Matches($ParserContent).Count -ne 2) { throw 'The pinned BBDown DASH quality lookups changed; refusing to apply the safe quality lookup.' }
    $ParserContent = $VideoQualityIndex.Replace($ParserContent, 'dfn = ResolveQualityName(streamInfo, videoId),', 1)
    $ParserContent = $VideoQualityIndex.Replace($ParserContent, 'dfn = ResolveQualityName(root, videoId),', 1)

    $DurlQualityNeedle = 'dfn = Config.qualitys[quality],'
    if (-not $ParserContent.Contains($DurlQualityNeedle)) { throw 'The pinned BBDown durl quality lookup changed; refusing to apply the safe quality lookup.' }
    $ParserContent = $ParserContent.Replace($DurlQualityNeedle, 'dfn = ResolveQualityName(root, quality),')

    $ResolverNeedle = '        private static string GetVideoCodec(string code)'
    if (-not $ParserContent.Contains($ResolverNeedle)) { throw 'The pinned BBDown parser helper layout changed; refusing to add the safe quality lookup.' }
    $ResolverReplacement = @'
        private static string ResolveQualityName(JsonElement source, string qualityId)
        {
            if (source.ValueKind == JsonValueKind.Object)
            {
                if (TryReadQualityDescription(source, qualityId, out var description)) return description;
                if (source.TryGetProperty("support_formats", out JsonElement formats) && formats.ValueKind == JsonValueKind.Array)
                {
                    foreach (var format in formats.EnumerateArray())
                    {
                        if (TryReadQualityDescription(format, qualityId, out description)) return description;
                    }
                }
            }

            return Config.GetQualityName(qualityId);
        }

        private static bool TryReadQualityDescription(JsonElement node, string qualityId, out string description)
        {
            description = "";
            if (node.ValueKind != JsonValueKind.Object ||
                !node.TryGetProperty("quality", out JsonElement quality) ||
                quality.ToString() != qualityId)
            {
                return false;
            }

            foreach (var propertyName in new[] { "new_description", "display_desc", "description" })
            {
                if (node.TryGetProperty(propertyName, out JsonElement value) &&
                    value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    description = value.GetString()!;
                    return true;
                }
            }

            return false;
        }

'@
    $ParserContent = $ParserContent.Replace($ResolverNeedle, $ResolverReplacement + $ResolverNeedle)
    if ($ParserContent.Contains('Config.qualitys[')) { throw 'An unsafe BBDown parser quality lookup remains after patching.' }
    [IO.File]::WriteAllText($ParserPath, $ParserContent, [Text.UTF8Encoding]::new($false))

    $ProgramPath = Join-Path $WorkingDirectory 'BBDown\Program.cs'
    $ProgramContent = [IO.File]::ReadAllText($ProgramPath)
    $DolbyNeedle = 'Config.qualitys["126"]'
    $InteractiveNeedle = 'Config.qualitys[key]'
    if (-not $ProgramContent.Contains($DolbyNeedle) -or -not $ProgramContent.Contains($InteractiveNeedle)) { throw 'The pinned BBDown program quality lookups changed; refusing to apply the safe quality lookup.' }
    $ProgramContent = $ProgramContent.Replace($DolbyNeedle, 'Config.GetQualityName("126")')
    $ProgramContent = $ProgramContent.Replace($InteractiveNeedle, 'Config.GetQualityName(key)')
    if ($ProgramContent.Contains('Config.qualitys[')) { throw 'An unsafe BBDown program quality lookup remains after patching.' }
    [IO.File]::WriteAllText($ProgramPath, $ProgramContent, [Text.UTF8Encoding]::new($false))

    $Project = Join-Path $WorkingDirectory 'BBDown\BBDown.csproj'
    & dotnet publish $Project -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:ManagePackageVersionsCentrally=false -p:Version=$($Entry.version) -o $Publish | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Patched BBDown build failed with exit code $LASTEXITCODE" }
    if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) { throw 'Patched BBDown build did not produce BBDown.exe.' }
    [IO.File]::WriteAllText($Marker, "source=$($Entry.commit)`nquality=122:4K·SDR增强`nquality=100:智能修复`nquality_lookup=support_formats_then_safe_fallback`npgc_web_fnval=143312`npgc_drm_tech_type=3`nugc_web_fnval=4048`n", [Text.UTF8Encoding]::new($false))
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

$BBDownExpanded = Build-BBDownWithBeflowPatches $BBDownSourceArchive $Manifest.bbdownSource
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
