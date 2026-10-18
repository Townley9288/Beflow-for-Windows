param([Parameter(Mandatory = $true)][string]$WorkingDirectory)
$ErrorActionPreference = 'Stop'

function Replace-TvSource([string]$RelativePath, [string]$Needle, [string]$Replacement) {
    $Path = Join-Path $WorkingDirectory $RelativePath
    $Content = [IO.File]::ReadAllText($Path).Replace("`r`n", "`n")
    $Needle = $Needle.Replace("`r`n", "`n")
    if ([regex]::Matches($Content, [regex]::Escape($Needle)).Count -ne 1) {
        throw "Pinned TV session patch did not match $RelativePath exactly."
    }
    [IO.File]::WriteAllText($Path, $Content.Replace($Needle, $Replacement.Replace("`r`n", "`n")), [Text.UTF8Encoding]::new($false))
}

Replace-TvSource 'BBDown.Core/Parser.cs' '            if (webJson.Contains("\"大会员专享限制\""))' `
    '            if (!tvApi && webJson.Contains("\"大会员专享限制\""))'
Replace-TvSource 'BBDown.Core/Parser.cs' @'
            string webJson = await GetWebSourceAsync(api);
            //以下情况从网页源代码尝试解析
'@ @'
            string webJson;
            try { webJson = await GetWebSourceAsync(api); }
            catch (Exception ex) when (tvApi)
            {
                throw new BeflowTvSessionException("TV 接口请求失败：" + ex.Message);
            }
            if (tvApi)
            {
                JsonDocument tvDocument;
                try { tvDocument = JsonDocument.Parse(webJson); }
                catch (JsonException) { throw new BeflowTvSessionException("TV 接口响应结构异常，请手动重试。"); }
                using var tvResponse = tvDocument;
                var tvRoot = tvResponse.RootElement;
                if (tvRoot.ValueKind != JsonValueKind.Object || !tvRoot.TryGetProperty("code", out var tvCode)
                    || tvCode.ValueKind != JsonValueKind.Number || !tvCode.TryGetInt32(out var tvCodeValue))
                    throw new BeflowTvSessionException("TV 接口响应结构异常，请手动重试。");
                if (tvCodeValue != 0)
                {
                    var tvMessage = tvRoot.TryGetProperty("message", out var text) ? text.ToString() : "接口请求失败";
                    throw new BeflowTvSessionException($"TV 接口错误 {tvCodeValue}：{tvMessage}。已停止，请手动重试。");
                }
            }
            //以下情况从网页源代码尝试解析
'@
Replace-TvSource 'BBDown/Program.cs' '                if (++retryCount > 2) throw;' @'
                if (ex is BeflowTvSessionException) throw;
                if (++retryCount > 2) throw;
'@
[IO.File]::WriteAllText((Join-Path $WorkingDirectory 'BBDown.Core/BeflowTvSessionException.cs'), @'
namespace BBDown.Core;
public sealed class BeflowTvSessionException : System.InvalidOperationException
{
    public BeflowTvSessionException(string message) : base(message) { }
}
'@, [Text.UTF8Encoding]::new($false))
Replace-TvSource 'BBDown/Program.cs' @'
                Log("视频为互动视频，暂时不支持tv下载，修改为默认下载");
                myOption.UseTvApi = false;
'@ @'
                throw new NotSupportedException("TV 接口不支持互动视频，已停止，请手动处理。");
'@

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'patches/BeflowTvStreamMetadata.cs') `
    -Destination (Join-Path $WorkingDirectory 'BBDown.Core/BeflowTvStreamMetadata.cs') -Force
Replace-TvSource 'BBDown.Core/Parser.cs' @'
                        if (!tvApi && !appApi)
                        {
                            v.res = node.GetProperty("width").ToString() + "x" + node.GetProperty("height").ToString();
                            v.fps = node.GetProperty("frame_rate").ToString();
                        }
'@ @'
                        if (!tvApi && !appApi)
                        {
                            v.res = node.GetProperty("width").ToString() + "x" + node.GetProperty("height").ToString();
                            v.fps = node.GetProperty("frame_rate").ToString();
                        }
                        else if (tvApi)
                        {
                            var metadata = BeflowTvStreamMetadata.ReadApi(node);
                            v.res = metadata.Resolution;
                            v.fps = metadata.FrameRate;
                        }
'@
Replace-TvSource 'BBDown/Program.cs' @'
                List<AudioMaterial> audioMaterial = new();
'@ @'
                if (myOption.UseTvApi)
                {
                    try
                    {
                        foreach (var track in parsedResult.VideoTracks)
                        {
                            if (BeflowTvStreamMetadata.IsComplete(track.res, track.fps)) continue;
                            Log($"正在探测 TV {track.dfn} · {track.codecs} 的真实分辨率和帧率...");
                            var probeHost = string.IsNullOrEmpty(myOption.UposHost) && myOption.ForceReplaceHost ? BACKUP_HOST : myOption.UposHost;
                            var metadata = await BeflowTvStreamMetadata.ProbeAsync(track.baseUrl, myOption.FFmpegPath, probeHost, "Mozilla/5.0");
                            track.res = metadata.Resolution;
                            track.fps = metadata.FrameRate;
                        }
                    }
                    catch (Exception ex) { throw new BeflowTvSessionException(ex.Message); }
                }
                List<AudioMaterial> audioMaterial = new();
'@
