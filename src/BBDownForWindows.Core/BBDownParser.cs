using System.Text.RegularExpressions;

namespace BBDownForWindows.Core;

public static partial class BBDownParser
{
    public static VideoInfo ParseInfo(string output)
    {
        var info = new VideoInfo { RawOutput = output };
        var section = string.Empty;
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var title = TitleRegex().Match(line);
            if (title.Success) { info.Title = title.Groups[1].Value.Trim(); continue; }
            var page = PageRegex().Match(line);
            if (page.Success)
            {
                info.Pages.Add(new PageInfo(int.Parse(page.Groups[1].Value), page.Groups[2].Value, page.Groups[3].Value, page.Groups[4].Value));
                continue;
            }
            if (MuxedStreamHeadingRegex().IsMatch(line)) { section = "muxed"; continue; }
            if (line.Contains("视频流", StringComparison.Ordinal)) { section = "video"; continue; }
            if (line.Contains("音频流", StringComparison.Ordinal)) { section = "audio"; continue; }
            var stream = StreamRegex().Match(line);
            if (!stream.Success) continue;
            var parts = BracketRegex().Matches(stream.Groups[2].Value).Select(match => match.Groups[1].Value).ToArray();
            if (section == "video" && parts.Length >= 6 && TryResolution(parts[1], out var width, out var height))
                info.VideoStreams.Add(new VideoStreamInfo(int.Parse(stream.Groups[1].Value), parts[0], parts[1], width, height, parts[2].ToUpperInvariant(), parts[3], parts[4], ParseBitrate(parts[4]), parts[5]));
            else if (section == "muxed" && ParseMuxedStream(int.Parse(stream.Groups[1].Value), parts) is { } muxed)
                info.VideoStreams.Add(muxed);
            else if (section == "audio" && parts.Length >= 3)
                info.AudioStreams.Add(new AudioStreamInfo(int.Parse(stream.Groups[1].Value), parts[0], parts[1], ParseBitrate(parts[1]), parts[2]));
        }
        return info;
    }

    public static Dictionary<int, List<VideoStreamInfo>> ParsePageVideoStreams(string output)
    {
        var result = new Dictionary<int, List<VideoStreamInfo>>();
        int? currentPage = null;
        var section = string.Empty;
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var page = ParsingPageRegex().Match(line);
            if (page.Success)
            {
                currentPage = int.Parse(page.Groups[1].Value);
                result.TryAdd(currentPage.Value, []);
                section = string.Empty;
                continue;
            }
            if (currentPage is null) continue;
            if (MuxedStreamHeadingRegex().IsMatch(line)) { section = "muxed"; continue; }
            if (line.Contains("视频流", StringComparison.Ordinal)) { section = "video"; continue; }
            if (line.Contains("音频流", StringComparison.Ordinal)) { section = "audio"; continue; }
            if (section is not ("video" or "muxed")) continue;
            var stream = StreamRegex().Match(line);
            if (!stream.Success) continue;
            var parts = BracketRegex().Matches(stream.Groups[2].Value).Select(match => match.Groups[1].Value).ToArray();
            if (section == "muxed")
            {
                if (ParseMuxedStream(int.Parse(stream.Groups[1].Value), parts) is { } muxed) result[currentPage.Value].Add(muxed);
                continue;
            }
            if (parts.Length >= 6 && TryResolution(parts[1], out var width, out var height))
                result[currentPage.Value].Add(new VideoStreamInfo(int.Parse(stream.Groups[1].Value), parts[0], parts[1], width, height, parts[2].ToUpperInvariant(), parts[3], parts[4], ParseBitrate(parts[4]), parts[5]));
        }
        return result;
    }

    public static HashSet<int> ParseMuxedStreamPages(string output)
    {
        var result = new HashSet<int>();
        int? currentPage = null;
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var page = ParsingPageRegex().Match(line);
            if (page.Success) currentPage = int.Parse(page.Groups[1].Value);
            else if (currentPage is not null && MuxedStreamHeadingRegex().IsMatch(line)) result.Add(currentPage.Value);
        }
        return result;
    }

    public static Dictionary<int, List<AudioStreamInfo>> ParsePageAudioStreams(string output)
    {
        var result = new Dictionary<int, List<AudioStreamInfo>>();
        int? currentPage = null;
        var section = string.Empty;
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var page = ParsingPageRegex().Match(line);
            if (page.Success)
            {
                currentPage = int.Parse(page.Groups[1].Value);
                result.TryAdd(currentPage.Value, []);
                section = string.Empty;
                continue;
            }
            if (currentPage is null) continue;
            if (line.Contains("视频流", StringComparison.Ordinal)) { section = "video"; continue; }
            if (line.Contains("音频流", StringComparison.Ordinal)) { section = "audio"; continue; }
            if (section != "audio") continue;
            var stream = StreamRegex().Match(line);
            if (!stream.Success) continue;
            var parts = BracketRegex().Matches(stream.Groups[2].Value).Select(match => match.Groups[1].Value).ToArray();
            if (parts.Length >= 3)
                result[currentPage.Value].Add(new AudioStreamInfo(int.Parse(stream.Groups[1].Value), parts[0], parts[1], ParseBitrate(parts[1]), parts[2]));
        }
        return result;
    }

    public static List<int> ParseSelectedPages(string output)
    {
        var parsed = ParsingPageRegex().Matches(output).Select(match => int.Parse(match.Groups[1].Value)).Distinct().ToList();
        if (parsed.Count > 0) return parsed;
        var selected = SelectedRegex().Match(output);
        if (!selected.Success || selected.Groups[1].Value.Trim().Equals("ALL", StringComparison.OrdinalIgnoreCase)) return [];
        return ExpandPageExpression(selected.Groups[1].Value);
    }

    public static List<int> ParseExpectedSeasonPages(string output)
    {
        var total = TotalPagesRegex().Match(output);
        if (total.Success) return Enumerable.Range(1, int.Parse(total.Groups[1].Value)).ToList();
        return PageRegex().Matches(output).Select(match => int.Parse(match.Groups[1].Value)).Distinct().Order().ToList();
    }

    public static (Dictionary<int, int> Indices, List<int> FallbackPages) SelectPreferredAudioIndices(Dictionary<int, List<AudioStreamInfo>> streams, IEnumerable<int> pages, string codec)
    {
        var indices = new Dictionary<int, int>();
        var fallback = new List<int>();
        foreach (var page in pages)
        {
            if (!streams.TryGetValue(page, out var candidates) || candidates.Count == 0) throw new InvalidOperationException($"P{page} 没有可用音频流");
            var selected = candidates.FirstOrDefault(item => item.Codec.Equals(codec, StringComparison.OrdinalIgnoreCase));
            if (selected is null) { selected = candidates[0]; fallback.Add(page); }
            indices[page] = selected.Index;
        }
        return (indices, fallback);
    }

    public static VideoStreamInfo? SelectResolutionFirst(IEnumerable<VideoStreamInfo> streams, string preferredEncoding)
    {
        return streams.Where(stream => stream.Width <= 3840 && stream.Height <= 2160)
            .OrderByDescending(stream => stream.Pixels)
            .ThenBy(stream => stream.Codec.Equals(preferredEncoding, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(stream => stream.BitrateKbps)
            .ThenBy(stream => stream.Index)
            .FirstOrDefault();
    }

    public static List<ResolutionGroup> BuildResolutionGroups(Dictionary<int, List<VideoStreamInfo>> pageStreams, string preferredEncoding)
    {
        var groups = new Dictionary<(string Quality, string Codec, string Resolution), ResolutionGroup>();
        foreach (var (page, streams) in pageStreams.OrderBy(pair => pair.Key))
        {
            var selected = SelectResolutionFirst(streams, preferredEncoding);
            if (selected is null) continue;
            var key = (selected.Quality, selected.Codec, selected.Resolution);
            if (!groups.TryGetValue(key, out var group)) groups[key] = group = new ResolutionGroup(key.Quality, key.Codec, key.Resolution, []);
            group.Pages.Add(page);
        }
        return groups.Values.ToList();
    }

    public static string BuildInteractiveInput(IEnumerable<int> pages, IReadOnlyDictionary<int, int> audioIndices, bool audioOnly)
    {
        var answers = new List<string>();
        foreach (var page in pages)
        {
            if (!audioOnly) answers.Add("0");
            answers.Add(audioIndices[page].ToString());
        }
        return answers.Count == 0 ? string.Empty : string.Join('\n', answers) + "\n";
    }

    public static List<int> ExpandPageExpression(string expression)
    {
        var result = new List<int>();
        foreach (var token in Regex.Split(expression.Trim().TrimEnd('。', '.'), "[,，\\s]+"))
        {
            var range = Regex.Match(token, "^(\\d+)\\s*[-~]\\s*(\\d+)$");
            if (range.Success)
            {
                var start = int.Parse(range.Groups[1].Value);
                var end = int.Parse(range.Groups[2].Value);
                var step = end >= start ? 1 : -1;
                for (var value = start; value != end + step; value += step) result.Add(value);
            }
            else if (int.TryParse(token, out var page)) result.Add(page);
        }
        return result;
    }

    private static int ParseBitrate(string value)
    {
        var match = BitrateRegex().Match(value);
        return match.Success ? (int)double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
    }

    private static bool TryResolution(string value, out int width, out int height)
    {
        var match = ResolutionRegex().Match(value);
        width = match.Success ? int.Parse(match.Groups[1].Value) : 0;
        height = match.Success ? int.Parse(match.Groups[2].Value) : 0;
        return match.Success;
    }

    private static VideoStreamInfo? ParseMuxedStream(int index, string[] parts)
    {
        if (parts.Length < 4 || !TryInferResolution(parts[0], out var width, out var height)) return null;
        return new VideoStreamInfo(index, parts[0], $"{width}x{height}", width, height,
            parts[1].ToUpperInvariant(), string.Empty, parts[2], ParseBitrate(parts[2]), parts[3]);
    }

    private static bool TryInferResolution(string quality, out int width, out int height)
    {
        var normalized = quality.ToUpperInvariant();
        (width, height) = normalized switch
        {
            var value when value.Contains("8K", StringComparison.Ordinal) => (7680, 4320),
            var value when value.Contains("4K", StringComparison.Ordinal) => (3840, 2160),
            var value when value.Contains("1080", StringComparison.Ordinal) => (1920, 1080),
            var value when value.Contains("720", StringComparison.Ordinal) => (1280, 720),
            var value when value.Contains("480", StringComparison.Ordinal) => (852, 480),
            var value when value.Contains("360", StringComparison.Ordinal) => (640, 360),
            _ => (0, 0)
        };
        return width > 0;
    }

    [GeneratedRegex("视频标题:\\s*(.+)$")] private static partial Regex TitleRegex();
    [GeneratedRegex("\\bP(\\d+):\\s*\\[(\\d+)\\]\\s*\\[(.+?)\\]\\s*\\[([^\\]]+)\\]")] private static partial Regex PageRegex();
    [GeneratedRegex("^\\s*(\\d+)\\.\\s*(.+)$")] private static partial Regex StreamRegex();
    [GeneratedRegex("\\[([^\\]]+)\\]")] private static partial Regex BracketRegex();
    [GeneratedRegex("开始解析P(\\d+):")] private static partial Regex ParsingPageRegex();
    [GeneratedRegex("已选择\\s*[：:]\\s*([^\\r\\n]+)", RegexOptions.IgnoreCase)] private static partial Regex SelectedRegex();
    [GeneratedRegex("共计\\s*(\\d+)\\s*个分P")] private static partial Regex TotalPagesRegex();
    [GeneratedRegex("共计\\s*\\d+\\s*条流\\s*[（(]\\s*共有\\s*\\d+\\s*个分段\\s*[）)]")] private static partial Regex MuxedStreamHeadingRegex();
    [GeneratedRegex("(\\d+(?:\\.\\d+)?)\\s*kbps")] private static partial Regex BitrateRegex();
    [GeneratedRegex("^(\\d+)[x×](\\d+)$")] private static partial Regex ResolutionRegex();
}
