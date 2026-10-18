namespace BBDownForWindows.Core;

/// <summary>Chooses one complete session for an episode; never mixes TV video with WEB audio.</summary>
public static class DownloadSessionPolicy
{
    public static void Remember(DownloadEpisodeInfo episode, string apiMode)
    {
        episode.ApiMode = apiMode;
        if (episode.State == DownloadEpisodeParseState.Ready && !episode.Sources.Any(s => s.ApiMode == apiMode))
            episode.Sources.Add(new(apiMode, episode.VideoStreams, episode.AudioStreams, episode.IsMuxedStream));
    }

    public static bool Matches(DownloadEpisodeSource source, StreamSelectionRule rule, DownloadMode mode)
    {
        var video = mode == DownloadMode.AudioOnly && !source.IsMuxedStream
            || source.VideoStreams.Any(v => StreamSelectionPolicy.AreEquivalentQualities(v.Quality, rule.QualityRule)
                && (string.IsNullOrWhiteSpace(rule.PreferredEncoding) || rule.PreferredEncoding.Equals("auto", StringComparison.OrdinalIgnoreCase)
                    || v.Codec.Equals(rule.PreferredEncoding, StringComparison.OrdinalIgnoreCase)));
        var audio = mode == DownloadMode.VideoOnly || source.IsMuxedStream
            || source.AudioStreams.Any(a => rule.PreferredAudioCodec.Equals("auto", StringComparison.OrdinalIgnoreCase)
                || a.Codec.Equals(rule.PreferredAudioCodec, StringComparison.OrdinalIgnoreCase));
        return video && audio;
    }

    public static void Select(DownloadEpisodeInfo episode, StreamSelectionRule rule, DownloadMode mode)
    {
        if (episode.Sources.Count == 0) return; // Existing saved tasks have only their original stream catalog.
        var web = episode.Sources.FirstOrDefault(s => s.ApiMode == "WEB");
        if (web is not null) Apply(episode, web);
        episode.SessionReason = string.Empty;
    }

    public static DownloadEpisodeInfo ForSelection(DownloadEpisodeInfo episode, string apiMode)
    {
        if (string.IsNullOrWhiteSpace(apiMode) || episode.Sources.Count == 0 || episode.ApiMode == apiMode) return episode;
        var source = episode.Sources.SingleOrDefault(s => s.ApiMode == apiMode)
            ?? throw new InvalidOperationException($"P{episode.Page.Number} 缺少已确认的 {apiMode} 选流，请重新解析。");
        return new DownloadEpisodeInfo
        {
            Page = episode.Page, ApiMode = apiMode, VideoStreams = source.VideoStreams,
            AudioStreams = source.AudioStreams, IsMuxedStream = source.IsMuxedStream,
            State = episode.State, Error = episode.Error
        };
    }

    public static void Apply(DownloadEpisodeInfo episode, DownloadEpisodeSource source)
    {
        episode.ApiMode = source.ApiMode;
        episode.VideoStreams = source.VideoStreams;
        episode.AudioStreams = source.AudioStreams;
        episode.IsMuxedStream = source.IsMuxedStream;
    }
}
