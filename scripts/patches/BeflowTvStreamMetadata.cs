using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace BBDown.Core;

// Compiled into the pinned BBDown build and linked into the regression tests.
public static class BeflowTvStreamMetadata
{
    public const int MaximumHeaderBytes = 1024 * 1024;
    private static readonly HttpClient Media = new(new HttpClientHandler
    {
        UseProxy = false, UseCookies = false, AllowAutoRedirect = false
    });

    public sealed record Metadata(int Width, int Height, string FrameRate)
    {
        public string Resolution => Width > 0 && Height > 0 ? $"{Width}x{Height}" : string.Empty;
    }

    public static Metadata ReadApi(JsonElement stream) => new(
        ReadPositiveInt(stream, "width"), ReadPositiveInt(stream, "height"),
        stream.TryGetProperty("frame_rate", out var fps) && IsFrameRate(fps.ToString()) ? fps.ToString() : string.Empty);

    public static bool IsComplete(string? resolution, string? frameRate)
    {
        var parts = resolution?.Split('x');
        return parts?.Length == 2 && int.TryParse(parts[0], out var width) && width > 0
            && int.TryParse(parts[1], out var height) && height > 0 && IsFrameRate(frameRate);
    }

    public static async Task<Metadata> ProbeAsync(string url, string ffmpegPath, string host, string userAgent)
    {
        var executable = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ffmpegPath))!, "ffprobe.exe");
        if (!File.Exists(executable)) throw new InvalidOperationException("TV 流参数探测失败：找不到 ffprobe.exe，请检查内置工具。");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var header = await ReadHeaderAsync(Media, ProbeUri(url, host), userAgent, timeout.Token);
            using var process = new Process { StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = true
            } };
            foreach (var argument in new[] { "-v", "error", "-probesize", MaximumHeaderBytes.ToString(CultureInfo.InvariantCulture),
                "-analyzeduration", "1000000", "-select_streams", "v:0", "-show_entries", "stream=width,height,r_frame_rate", "-of", "json", "-i", "pipe:0" })
                process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) throw new InvalidOperationException("TV 流参数探测失败：无法启动 ffprobe。");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try
            {
                // Pipe the bounded sample without leaving temporary media files on cancellation.
                try { await process.StandardInput.BaseStream.WriteAsync(header, timeout.Token); }
                catch (IOException) { /* ffprobe may finish reading before the sample ends; its exit code and JSON remain mandatory. */ }
                finally { process.StandardInput.Close(); }
                await process.WaitForExitAsync(timeout.Token);
            }
            catch
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync();
                throw;
            }
            var json = await output;
            await error; // Never put signed media URLs or raw ffprobe diagnostics into the UI/log.
            if (process.ExitCode != 0) throw new InvalidOperationException("TV 流参数探测失败：ffprobe 无法识别媒体头，请手动重试。");
            return ReadProbe(json);
        }
        catch (OperationCanceledException) { throw new InvalidOperationException("TV 流参数探测超时（20 秒），已停止，请手动重试。"); }
        catch (HttpRequestException ex)
        {
            var status = ex.StatusCode is { } code ? $"HTTP {(int)code}" : "网络请求失败";
            throw new InvalidOperationException($"TV 流参数探测失败：{status}。已停止，请手动重试。");
        }
        catch (JsonException) { throw new InvalidOperationException("TV 流参数探测失败：ffprobe 返回了无法识别的数据。"); }
    }

    public static Uri ProbeUri(string url, string host)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("TV 流参数探测失败：媒体地址无效。");
        return string.IsNullOrWhiteSpace(host) ? uri : new UriBuilder(uri) { Host = host, Port = -1 }.Uri;
    }

    public static async Task<byte[]> ReadHeaderAsync(HttpClient client, Uri uri, string userAgent, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(0, MaximumHeaderBytes - 1);
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        // Match the existing media downloader: Android TV URLs reject a WEB Referer.
        if (!uri.Query.Contains("platform=android", StringComparison.Ordinal))
            request.Headers.Referrer = new Uri("https://www.bilibili.com/");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.From != 0)
            throw new InvalidOperationException("TV 流参数探测失败：服务器返回的媒体头范围不正确。");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        var bytes = new byte[MaximumHeaderBytes];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await input.ReadAsync(bytes.AsMemory(count), token);
            if (read == 0) break;
            count += read;
        }
        if (count == 0) throw new InvalidOperationException("TV 流参数探测失败：媒体头为空。");
        if (count < bytes.Length) Array.Resize(ref bytes, count);
        return bytes;
    }

    public static Metadata ReadProbe(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("streams", out var streams)
            || streams.ValueKind != JsonValueKind.Array || streams.GetArrayLength() != 1)
            throw new InvalidOperationException("TV 流参数探测失败：没有取得唯一的视频流信息。");
        var stream = streams[0];
        var metadata = new Metadata(ReadPositiveInt(stream, "width"), ReadPositiveInt(stream, "height"),
            stream.TryGetProperty("r_frame_rate", out var fps) ? fps.ToString() : string.Empty);
        if (!IsComplete(metadata.Resolution, metadata.FrameRate))
            throw new InvalidOperationException("TV 流参数探测失败：媒体头未提供有效的分辨率或帧率。");
        return metadata;
    }

    private static int ReadPositiveInt(JsonElement node, string name) => node.ValueKind == JsonValueKind.Object
        && node.TryGetProperty(name, out var value) && int.TryParse(value.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
        && number > 0 ? number : 0;

    private static bool IsFrameRate(string? value)
    {
        var parts = value?.Split('/');
        return parts is { Length: 1 or 2 }
            && decimal.TryParse(parts[0], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var numerator) && numerator > 0
            && (parts.Length == 1 || decimal.TryParse(parts[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var denominator) && denominator > 0);
    }
}
