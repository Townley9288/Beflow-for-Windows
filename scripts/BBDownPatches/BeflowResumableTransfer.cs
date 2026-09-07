using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace BBDown;

// Queue-only transport. A mismatch is an error, never permission to truncate or switch transport.
internal static class BeflowResumableTransfer
{
    private static readonly HttpClient client = new(new HttpClientHandler { UseProxy = false, AutomaticDecompression = DecompressionMethods.None })
    { Timeout = TimeSpan.FromMinutes(2) };
    private const long ChunkSize = 20 * 1024 * 1024;
    internal sealed class Manifest
    {
        public int Schema { get; set; } = 1;
        public string Resource { get; set; } = "";
        public long Size { get; set; }
        public string ETag { get; set; } = "";
        public string Modified { get; set; } = "";
        public string FirstBytesHash { get; set; } = "";
        public string Transport { get; set; } = "";
        public bool Assembling { get; set; }
        public bool Completed { get; set; }
        public string FinalHash { get; set; } = "";
        public List<Part> Parts { get; set; } = new();
    }
    internal sealed class Part
    {
        public long From { get; set; }
        public long To { get; set; }
        public long SavedBytes { get; set; }
        public string Hash { get; set; } = "";
    }

    internal static async Task DownloadAsync(string url, string path, BBDownDownloadUtil.DownloadConfig config)
    {
        if (config.ForceHttp) url = url.Replace("https:", "http:");
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var manifestPath = path + ".beflow.json";
        var probe = await ProbeAsync(url);
        var transport = config.UseAria2c ? "aria2" : config.MultiThread ? "internal-multi" : "internal-single";
        Manifest manifest;
        if (File.Exists(manifestPath))
        {
            manifest = JsonSerializer.Deserialize(await File.ReadAllTextAsync(manifestPath), ResumeJson.Default.Manifest)
                ?? throw new IOException("续传清单为空");
            if (manifest.Schema != 1 || manifest.Resource != probe.Resource || manifest.Size != probe.Size ||
                manifest.ETag != probe.ETag || manifest.Modified != probe.Modified || manifest.FirstBytesHash != probe.FirstBytesHash || manifest.Transport != transport)
                throw new IOException("下载资源或下载器配置已变化，不能复用断点");
        }
        else
        {
            if (File.Exists(path) || File.Exists(path + ".aria2") || File.Exists(path + ".assembling"))
                throw new IOException("输出已存在但没有本任务的续传清单");
            manifest = probe; manifest.Transport = transport;
            var size = config.MultiThread && !config.UseAria2c ? ChunkSize : manifest.Size;
            for (long from = 0; from < manifest.Size; from += size)
                manifest.Parts.Add(new Part { From = from, To = Math.Min(manifest.Size - 1, from + size - 1) });
            Save(manifestPath, manifest);
        }
        if (manifest.Completed)
        {
            VerifyFile(path, manifest.Size, manifest.FinalHash);
            CleanupCompletedParts(path, manifest);
            return;
        }
        if (config.UseAria2c)
        {
            if (File.Exists(path) && !File.Exists(path + ".aria2"))
                throw new IOException("aria2 文件缺少控制文件且尚未确认完成，请保留现场");
            await BBDownAria2c.DownloadFileByAria2cAsync(url, path, config.Aria2cArgs);
            if (File.Exists(path + ".aria2") || !File.Exists(path) || new FileInfo(path).Length != manifest.Size)
                throw new IOException("aria2 未确认完整传输");
            manifest.FinalHash = HashFile(path); manifest.Completed = true; Save(manifestPath, manifest);
            return;
        }
        var expectedParts = transport == "internal-multi" ? (manifest.Size + ChunkSize - 1) / ChunkSize : 1;
        if (manifest.Parts.Count != expectedParts) throw new IOException("续传分片数量无效");
        long next = 0;
        foreach (var part in manifest.Parts)
        {
            if (part.From != next || part.To < part.From || part.To >= manifest.Size || part.SavedBytes < 0 || part.SavedBytes > part.To - part.From + 1)
                throw new IOException("续传分片边界无效");
            next = part.To + 1;
        }
        if (next != manifest.Size) throw new IOException("续传分片不完整");
        // Save successful assembly before publication. This also covers a crash between move and save.
        if (manifest.FinalHash.Length > 0)
        {
            if (!File.Exists(path))
            {
                VerifyFile(path + ".assembling", manifest.Size, manifest.FinalHash);
                File.Move(path + ".assembling", path, false);
            }
            VerifyFile(path, manifest.Size, manifest.FinalHash);
            manifest.Completed = true; Save(manifestPath, manifest);
            CleanupCompletedParts(path, manifest);
            return;
        }
        if (File.Exists(path)) throw new IOException("目标已被未经确认的文件占用");
        using var progress = new ProgressBar(config.RelatedTask);
        var bytes = new ConcurrentDictionary<int, long>();
        var saveGate = new object();
        for (var index = 0; index < manifest.Parts.Count; index++)
        {
            var temporary = PartPath(path, index);
            bytes[index] = File.Exists(temporary) ? new FileInfo(temporary).Length : 0;
        }
        Report();
        await Parallel.ForEachAsync(Enumerable.Range(0, manifest.Parts.Count),
            new ParallelOptions { MaxDegreeOfParallelism = config.MultiThread ? 8 : 1 }, async (index, token) =>
        {
            var part = manifest.Parts[index]; var temporary = PartPath(path, index);
            var exists = File.Exists(temporary);
            if (!exists && part.SavedBytes > 0) throw new IOException("已记录分片丢失");
            using var file = new FileStream(temporary, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous);
            if (file.Length < part.SavedBytes || file.Length > part.To - part.From + 1) throw new IOException("断点文件大小与分片边界不一致");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024]; long read = 0;
            while (read < part.SavedBytes)
            {
                var count = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, part.SavedBytes - read)), token);
                if (count == 0) throw new IOException("断点文件意外结束");
                hash.AppendData(buffer, 0, count); read += count;
            }
            if (part.SavedBytes > 0 && Convert.ToHexString(hash.GetCurrentHash()) != part.Hash) throw new IOException("断点文件校验失败");
            // A hard stop may leave one flushed block ahead of the manifest. Verify those bytes against the resource.
            if (file.Length > read)
            {
                using var tailResponse = await GetRangeAsync(url, part.From + read, part.From + file.Length - 1, manifest, token);
                using var remote = await tailResponse.Content.ReadAsStreamAsync(token);
                var remoteBuffer = new byte[buffer.Length];
                while (read < file.Length)
                {
                    var count = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, file.Length - read)), token);
                    await remote.ReadExactlyAsync(remoteBuffer.AsMemory(0, count), token);
                    if (!buffer.AsSpan(0, count).SequenceEqual(remoteBuffer.AsSpan(0, count))) throw new IOException("未落盘断点与远端资源不一致");
                    hash.AppendData(buffer, 0, count); read += count;
                }
                SavePart();
            }
            if (file.Length == part.To - part.From + 1) return;
            using var response = await GetRangeAsync(url, part.From + file.Length, part.To, manifest, token);
            using var stream = await response.Content.ReadAsStreamAsync(token);
            while (file.Length < part.To - part.From + 1)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, part.To - part.From + 1 - file.Length)), token);
                if (count == 0) throw new IOException("媒体传输提前结束，已保留断点");
                await file.WriteAsync(buffer.AsMemory(0, count), token);
                file.Flush(true); hash.AppendData(buffer, 0, count); read += count;
                SavePart(); bytes[index] = read; Report();
            }
            void SavePart()
            {
                lock (saveGate) { part.SavedBytes = read; part.Hash = Convert.ToHexString(hash.GetCurrentHash()); Save(manifestPath, manifest); }
            }
        });
        var assembled = path + ".assembling";
        if (File.Exists(assembled) && !manifest.Assembling) throw new IOException("临时封装路径已被占用");
        manifest.Assembling = true; Save(manifestPath, manifest);
        using (var output = new FileStream(assembled, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            for (var index = 0; index < manifest.Parts.Count; index++)
            {
                using var part = File.OpenRead(PartPath(path, index)); await part.CopyToAsync(output);
            }
            output.Flush(true);
        }
        if (new FileInfo(assembled).Length != manifest.Size) throw new IOException("合并后的传输大小不一致");
        manifest.FinalHash = HashFile(assembled); Save(manifestPath, manifest);
        File.Move(assembled, path, false); manifest.Completed = true; Save(manifestPath, manifest);
        CleanupCompletedParts(path, manifest);
        void Report() { lock (progress) { var sum = bytes.Values.Sum(); progress.Report((double)sum / manifest.Size, sum); } }
    }

    private static void CleanupCompletedParts(string path, Manifest manifest)
    {
        if (manifest.Transport == "aria2") return;
        if (!manifest.Completed || manifest.Transport is not ("internal-single" or "internal-multi"))
            throw new IOException("传输尚未确认完成，不能清理分片");
        var partSize = manifest.Transport == "internal-multi" ? ChunkSize : manifest.Size;
        if (partSize <= 0 || manifest.Parts.Count != (manifest.Size - 1) / partSize + 1)
            throw new IOException("续传分片数量无效，不能清理");
        var files = new List<string>();
        for (var index = 0; index < manifest.Parts.Count; index++)
        {
            var part = manifest.Parts[index];
            var from = index * partSize;
            var to = Math.Min(manifest.Size - 1, from + partSize - 1);
            if (part.From != from || part.To != to || part.SavedBytes != to - from + 1 || part.Hash.Length != 64)
                throw new IOException("续传分片记录未确认完整，不能清理");
            var temporary = PartPath(path, index);
            // Missing parts may already have been removed before an interrupted cleanup.
            if (!File.Exists(temporary)) continue;
            if ((File.GetAttributes(temporary) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("分片路径已被替换，不能清理");
            VerifyFile(temporary, part.SavedBytes, part.Hash);
            files.Add(temporary);
        }
        // Validate every remaining part before deleting any; never enumerate similar names.
        foreach (var temporary in files) File.Delete(temporary);
    }

    private static string PartPath(string path, int index) => path + $".part-{index:D5}";
    private static HttpRequestMessage Request(string url, long from, long to)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new(from, to);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        if (!url.Contains("platform=android")) request.Headers.TryAddWithoutValidation("Referer", "https://www.bilibili.com");
        request.Headers.TryAddWithoutValidation("Cookie", Core.Config.COOKIE);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        return request;
    }
    private static async Task<Manifest> ProbeAsync(string url)
    {
        using var request = Request(url, 0, 0);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        CheckRange(response, 0, 0, null);
        var manifest = new Manifest
        {
            Resource = new Uri(url).AbsolutePath,
            Size = response.Content.Headers.ContentRange!.Length!.Value,
            ETag = response.Headers.ETag?.ToString() ?? "",
            Modified = response.Content.Headers.LastModified?.ToString("O") ?? ""
        };
        using var sample = await GetRangeAsync(url, 0, Math.Min(manifest.Size - 1, 65535), manifest, CancellationToken.None);
        manifest.FirstBytesHash = Convert.ToHexString(SHA256.HashData(await sample.Content.ReadAsByteArrayAsync()));
        return manifest;
    }
    private static async Task<HttpResponseMessage> GetRangeAsync(string url, long from, long to, Manifest manifest, CancellationToken token)
    {
        using var request = Request(url, from, to);
        if (manifest.ETag.Length > 0 && !manifest.ETag.StartsWith("W/")) request.Headers.TryAddWithoutValidation("If-Range", manifest.ETag);
        else if (manifest.Modified.Length > 0) request.Headers.IfRange = new(DateTimeOffset.Parse(manifest.Modified));
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        try
        {
            CheckRange(response, from, to, manifest.Size);
            if ((response.Headers.ETag?.ToString() ?? "") != manifest.ETag || (response.Content.Headers.LastModified?.ToString("O") ?? "") != manifest.Modified)
                throw new IOException("远端验证信息已变化");
            return response;
        }
        catch { response.Dispose(); throw; }
    }
    private static void CheckRange(HttpResponseMessage response, long from, long to, long? size)
    {
        response.EnsureSuccessStatusCode();
        var range = response.Content.Headers.ContentRange;
        if (response.StatusCode != HttpStatusCode.PartialContent || range?.Unit != "bytes" || range.From != from || range.To != to ||
            range.Length is not > 0 || (size.HasValue && range.Length != size) || response.Content.Headers.ContentLength != to - from + 1)
            throw new IOException("服务器未返回所需的 206 / Content-Range，保留断点并停止");
    }
    private static void Save(string path, Manifest manifest)
    {
        var temporary = path + ".writing";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, manifest, ResumeJson.Default.Manifest); stream.Flush(true); }
        File.Move(temporary, path, true);
    }
    private static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void VerifyFile(string path, long size, string hash)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != size || hash.Length == 0 || HashFile(path) != hash)
            throw new IOException("已完成媒体文件缺失或校验失败");
    }
    internal static void MergeClips(string[] files, string destination)
    {
        var directory = destination + ".merge-work";
        var marker = directory + ".owner";
        var identity = string.Join("\n", files.Select(Path.GetFullPath));
        if (File.Exists(marker))
        {
            if (File.ReadAllText(marker) != identity) throw new IOException("旧式分段合并清单不匹配");
        }
        else
        {
            if (Directory.Exists(directory) || File.Exists(destination)) throw new IOException("旧式分段合并目标被占用");
            File.WriteAllText(marker, identity);
        }
        Directory.CreateDirectory(directory);
        var copies = files.Select((file, index) => Path.Combine(directory, $"{index:D5}.mp4")).ToArray();
        for (var index = 0; index < files.Length; index++) File.Copy(files[index], copies[index], true);
        var merged = Path.Combine(directory, "merged.output");
        if (File.Exists(merged)) File.Delete(merged);
        BBDownMuxer.MergeFLV(copies, merged);
        if (!File.Exists(merged) || new FileInfo(merged).Length == 0) throw new IOException("旧式分段合并失败");
        // The ownership marker precedes every attempt, so this is only our interrupted intermediate.
        if (File.Exists(destination)) File.Delete(destination);
        File.Move(merged, destination, false);
    }
}

[JsonSerializable(typeof(BeflowResumableTransfer.Manifest))]
internal partial class ResumeJson : JsonSerializerContext { }
