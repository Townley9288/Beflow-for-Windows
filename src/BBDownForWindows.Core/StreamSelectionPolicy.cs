using System.Globalization;
using System.Text.RegularExpressions;

namespace BBDownForWindows.Core;

public sealed record StreamSelectionDecision(
    VideoStreamInfo? Video,
    AudioStreamInfo? Audio,
    string FallbackReason);

public static partial class StreamSelectionPolicy
{
    private static readonly HashSet<string> KnownQualityRules = new(StringComparer.OrdinalIgnoreCase)
    { "杜比视界", "HDR 真彩", "4K 超清", "1080P 高码率", "1080P 高清", "720P 高清", "480P 清晰", "360P 流畅" };

    public static StreamSelectionDecision Select(
        DownloadEpisodeInfo episode,
        StreamSelectionRule rule,
        DownloadMode mode)
    {
        var reasons = new List<string>();
        VideoStreamInfo? video = null;
        AudioStreamInfo? audio = null;

        if (mode != DownloadMode.AudioOnly)
            video = SelectVideo(episode.VideoStreams, rule.QualityRule, rule.PreferredEncoding, reasons);
        if (mode != DownloadMode.VideoOnly)
            audio = SelectAudio(episode.AudioStreams, rule.PreferredAudioCodec, rule.AudioBitratePriority, reasons);

        return new StreamSelectionDecision(video, audio, string.Join("；", reasons.Distinct()));
    }

    public static StreamSelectionDecision Resolve(
        DownloadEpisodeInfo episode,
        EpisodeStreamSelection desired,
        DownloadRequest options)
    {
        var reasons = new List<string>();
        VideoStreamInfo? video = null;
        AudioStreamInfo? audio = null;

        if (options.DownloadMode != DownloadMode.AudioOnly)
        {
            if (desired.Video is null) throw new InvalidOperationException($"P{desired.PageNumber} 没有选择视频流");
            video = FindVideo(episode.VideoStreams, desired.Video, desired.Video.IsManual);
            if (video is null)
            {
                if (desired.Video.IsManual) throw new InvalidOperationException($"P{desired.PageNumber} 手动选择的视频流已不可用");
                video = SelectVideo(episode.VideoStreams, desired.Video.Quality, desired.Video.Codec, reasons);
                reasons.Add("原视频规格已变化，已重新匹配");
            }
        }

        if (options.DownloadMode != DownloadMode.VideoOnly)
        {
            if (desired.Audio is null) throw new InvalidOperationException($"P{desired.PageNumber} 没有选择音频流");
            audio = FindAudio(episode.AudioStreams, desired.Audio, desired.Audio.IsManual);
            if (audio is null)
            {
                if (desired.Audio.IsManual) throw new InvalidOperationException($"P{desired.PageNumber} 手动选择的音频流已不可用");
                audio = SelectAudio(episode.AudioStreams, desired.Audio.Codec, options.AudioBitratePriority, reasons);
                reasons.Add("原音频规格已变化，已重新匹配");
            }
        }

        if (!string.IsNullOrWhiteSpace(desired.FallbackReason)) reasons.Insert(0, desired.FallbackReason);
        return new StreamSelectionDecision(video, audio, string.Join("；", reasons.Distinct()));
    }

    public static long ParseSizeBytes(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var match = SizeRegex().Match(value);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)) return 0;
        var multiplier = match.Groups[2].Value.ToUpperInvariant() switch
        {
            "KB" or "KIB" => 1024d,
            "MB" or "MIB" => 1024d * 1024,
            "GB" or "GIB" => 1024d * 1024 * 1024,
            "TB" or "TIB" => 1024d * 1024 * 1024 * 1024,
            _ => 1d
        };
        return (long)Math.Max(0, amount * multiplier);
    }

    public static string NormalizeQualityRule(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "4K 超清";
        return value.Trim() switch
        {
            "highest" or "自动" or "最高" or "最高可用" => "4K 超清",
            "4K" => "4K 超清",
            "1080P" => "1080P 高清",
            "720P" => "720P 高清",
            "480P" => "480P 清晰",
            "360P" => "360P 流畅",
            var normalized => normalized
        };
    }

    public static bool IsKnownQualityRule(string? value) => !string.IsNullOrWhiteSpace(value) && KnownQualityRules.Contains(NormalizeQualityRule(value));

    private static VideoStreamInfo SelectVideo(
        IReadOnlyList<VideoStreamInfo> streams,
        string qualityRule,
        string preferredEncoding,
        List<string> reasons)
    {
        if (streams.Count == 0) throw new InvalidOperationException("没有可用视频流");
        var normalizedRule = NormalizeQualityRule(qualityRule);
        var exact = streams.Where(item => QualityMatches(item.Quality, normalizedRule)).ToList();

        List<VideoStreamInfo> qualityCandidates;
        if (exact.Count > 0)
        {
            qualityCandidates = exact;
        }
        else
        {
            var allowed = streams.Where(item => !IsSpecialQuality(item.Quality)).ToList();
            if (allowed.Count == 0) allowed = streams.ToList();
            var first = allowed[0];
            qualityCandidates = allowed.Where(item => item.Quality.Equals(first.Quality, StringComparison.OrdinalIgnoreCase) && item.Resolution.Equals(first.Resolution, StringComparison.OrdinalIgnoreCase)).ToList();
            reasons.Add($"无 {qualityRule}，已回退到 {first.Quality} · {first.Resolution}");
        }

        var encoded = qualityCandidates.FirstOrDefault(item => item.Codec.Equals(preferredEncoding, StringComparison.OrdinalIgnoreCase));
        if (encoded is not null) return encoded;
        var fallback = qualityCandidates[0];
        if (!string.IsNullOrWhiteSpace(preferredEncoding) && !preferredEncoding.Equals("auto", StringComparison.OrdinalIgnoreCase))
            reasons.Add($"无 {preferredEncoding}，已回退到 {fallback.Codec}");
        return fallback;
    }

    private static AudioStreamInfo SelectAudio(
        IReadOnlyList<AudioStreamInfo> streams,
        string preferredCodec,
        AudioBitratePriority priority,
        List<string> reasons)
    {
        if (streams.Count == 0) throw new InvalidOperationException("没有可用音频流");
        var candidates = preferredCodec.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? streams.ToList()
            : streams.Where(item => item.Codec.Equals(preferredCodec, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0)
        {
            candidates = streams.ToList();
            reasons.Add($"无 {preferredCodec}，已回退到 {candidates[0].Codec}");
        }
        return priority == AudioBitratePriority.Lowest
            ? candidates.OrderBy(item => item.BitrateKbps).ThenBy(item => item.Index).First()
            : candidates.OrderByDescending(item => item.BitrateKbps).ThenBy(item => item.Index).First();
    }

    private static VideoStreamInfo? FindVideo(IEnumerable<VideoStreamInfo> streams, VideoStreamSelection desired, bool requireExactBitrate) =>
        streams.Where(item => item.Quality.Equals(desired.Quality, StringComparison.OrdinalIgnoreCase)
                              && item.Resolution.Equals(desired.Resolution, StringComparison.OrdinalIgnoreCase)
                              && item.Codec.Equals(desired.Codec, StringComparison.OrdinalIgnoreCase)
                              && (!requireExactBitrate || item.BitrateKbps == desired.BitrateKbps))
            .OrderBy(item => Math.Abs(item.BitrateKbps - desired.BitrateKbps))
            .FirstOrDefault();

    private static AudioStreamInfo? FindAudio(IEnumerable<AudioStreamInfo> streams, AudioStreamSelection desired, bool requireExactBitrate) =>
        streams.Where(item => item.Codec.Equals(desired.Codec, StringComparison.OrdinalIgnoreCase)
                              && (!requireExactBitrate || item.BitrateKbps == desired.BitrateKbps))
            .OrderBy(item => Math.Abs(item.BitrateKbps - desired.BitrateKbps))
            .FirstOrDefault();

    private static bool IsSpecialQuality(string quality) =>
        quality.Contains("杜比", StringComparison.OrdinalIgnoreCase) || quality.Contains("HDR", StringComparison.OrdinalIgnoreCase);

    private static bool QualityMatches(string actual, string requested) =>
        actual.Equals(requested, StringComparison.OrdinalIgnoreCase)
        || actual.StartsWith(requested, StringComparison.OrdinalIgnoreCase)
        || requested.StartsWith(actual, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("([0-9]+(?:\\.[0-9]+)?)\\s*(KB|KiB|MB|MiB|GB|GiB|TB|TiB|B)", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();
}
