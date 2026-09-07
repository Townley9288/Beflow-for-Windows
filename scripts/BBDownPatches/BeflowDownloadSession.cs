using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using static BBDown.Core.Entity.Entity;
using static BBDown.BBDownUtil;

namespace BBDown;

// This source is copied into the pinned BBDown build by AcquireTools.ps1.
internal static class BeflowDownloadSession
{
    internal static bool Queued { get; private set; }
    private static NamedPipeClientStream? channel;
    private static StreamWriter? channelWriter;
    private static StreamReader? channelReader;
    internal sealed record Selection(int VideoIndex, int AudioIndex, string RelativeOutputPath, string Aria2Arguments);

    internal static Selection Prepare(string pipeName, Page page, BBDown.Core.Entity.ParsedResult tracks, bool muxed, bool queued = false)
    {
        Queued = queued;
        var output = new StringBuilder();
        output.AppendLine($"P{page.index}: [{page.cid}] [{page.title}] [{page.dur}s]");
        output.AppendLine($"开始解析P{page.index}:");
        output.AppendLine(muxed ? $"共计{tracks.VideoTracks.Count}条流(共有{tracks.Clips.Count}个分段)." : $"共计{tracks.VideoTracks.Count}条视频流.");
        for (var index = 0; index < tracks.VideoTracks.Count; index++)
        {
            var video = tracks.VideoTracks[index];
            var duration = page.dur == 0 ? video.dur : page.dur;
            var size = video.size > 0 ? video.size : duration * video.bandwith * 1024 / 8;
            output.AppendLine(muxed
                ? FormattableString.Invariant($"{index}. [{video.dfn}] [{video.codecs}] [~{video.size / 1024 / video.dur * 8:00} kbps] [{FormatFileSize(video.size)}]")
                : FormattableString.Invariant($"{index}. [{video.dfn}] [{video.res}] [{video.codecs}] [{video.fps}] [{video.bandwith} kbps] [~{FormatFileSize(size)}]"));
        }
        output.AppendLine($"共计{tracks.AudioTracks.Count}条音频流.");
        for (var index = 0; index < tracks.AudioTracks.Count; index++)
        {
            var audio = tracks.AudioTracks[index];
            var duration = page.dur == 0 ? audio.dur : page.dur;
            output.AppendLine(FormattableString.Invariant($"{index}. [{audio.codecs}] [{audio.bandwith} kbps] [~{FormatFileSize(duration * audio.bandwith * 1024 / 8)}]"));
        }
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        pipe.Connect(30000);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        channel = pipe; channelWriter = writer; channelReader = reader;
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("Protocol", queued ? 2 : 1);
            json.WriteNumber("Page", page.index);
            json.WriteString("Output", output.ToString());
            json.WriteEndObject();
        }
        writer.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
        using var response = JsonDocument.Parse(reader.ReadLine() ?? throw new IOException("Beflow 下载准备连接已关闭"));
        var root = response.RootElement;
        if (root.GetProperty("Protocol").GetInt32() != (queued ? 2 : 1)) throw new InvalidOperationException("Beflow 下载准备协议版本不匹配");
        var selection = new Selection(root.GetProperty("VideoIndex").GetInt32(), root.GetProperty("AudioIndex").GetInt32(),
            root.GetProperty("RelativeOutputPath").GetString()!, root.GetProperty("Aria2Arguments").GetString()!);
        if (selection.VideoIndex < -1 || selection.VideoIndex >= tracks.VideoTracks.Count ||
            selection.AudioIndex < -1 || selection.AudioIndex >= tracks.AudioTracks.Count ||
            string.IsNullOrWhiteSpace(selection.RelativeOutputPath))
            throw new InvalidOperationException("Beflow 返回了无效的下载选择");
        if (!queued) { reader.Dispose(); writer.Dispose(); pipe.Dispose(); }
        return selection;
    }

    internal static void Stage(string stage, string path = "")
    {
        if (!Queued) return;
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject(); json.WriteNumber("Protocol", 2);
            json.WriteString("Stage", stage); json.WriteString("Path", path); json.WriteEndObject();
        }
        channelWriter!.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
        using var response = JsonDocument.Parse(channelReader!.ReadLine() ?? throw new IOException("队列阶段确认连接已关闭"));
        if (response.RootElement.GetProperty("Protocol").GetInt32() != 2 || !response.RootElement.GetProperty("Ack").GetBoolean())
            throw new IOException("队列阶段未确认保存");
        if (stage == "muxed") { channelReader.Dispose(); channelWriter.Dispose(); channel!.Dispose(); }
    }
}
