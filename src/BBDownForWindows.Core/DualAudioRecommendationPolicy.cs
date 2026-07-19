using System.Text.RegularExpressions;

namespace BBDownForWindows.Core;

public static partial class DualAudioRecommendationPolicy
{
    public static DualAudioRecommendation Recommend(VideoStreamSelection sourceA, VideoStreamSelection sourceB, string preferredEncoding)
    {
        var comparison = Compare(sourceA, sourceB, preferredEncoding, out var reason);
        return comparison >= 0
            ? new DualAudioRecommendation(DualAudioSource.A, $"推荐来源 A：{reason}")
            : new DualAudioRecommendation(DualAudioSource.B, $"推荐来源 B：{reason}");
    }

    private static int Compare(VideoStreamSelection sourceA, VideoStreamSelection sourceB, string preferredEncoding, out string reason)
    {
        var pixelsA = ParsePixels(sourceA.Resolution);
        var pixelsB = ParsePixels(sourceB.Resolution);
        if (pixelsA != pixelsB)
        {
            reason = $"{sourceA.Resolution} 与 {sourceB.Resolution}，真实分辨率更高";
            return pixelsA.CompareTo(pixelsB);
        }

        var rangeA = DynamicRangeRank(sourceA.Quality);
        var rangeB = DynamicRangeRank(sourceB.Quality);
        if (rangeA != rangeB)
        {
            reason = $"同分辨率下 {sourceA.Quality} 与 {sourceB.Quality}，动态范围规格更高";
            return rangeA.CompareTo(rangeB);
        }

        if (sourceA.BitrateKbps != sourceB.BitrateKbps)
        {
            reason = $"同画质下视频码率 {sourceA.BitrateKbps} 与 {sourceB.BitrateKbps} kbps，码率更高";
            return sourceA.BitrateKbps.CompareTo(sourceB.BitrateKbps);
        }

        var preferredA = sourceA.Codec.Equals(preferredEncoding, StringComparison.OrdinalIgnoreCase);
        var preferredB = sourceB.Codec.Equals(preferredEncoding, StringComparison.OrdinalIgnoreCase);
        if (preferredA != preferredB)
        {
            reason = $"同规格下优先 {preferredEncoding} 编码";
            return preferredA ? 1 : -1;
        }

        reason = "双方规格相同，按稳定规则选择来源 A";
        return 0;
    }

    private static long ParsePixels(string resolution)
    {
        var match = ResolutionRegex().Match(resolution ?? string.Empty);
        return match.Success && long.TryParse(match.Groups[1].Value, out var width) && long.TryParse(match.Groups[2].Value, out var height)
            ? width * height
            : 0;
    }

    private static int DynamicRangeRank(string quality)
    {
        if (quality.Contains("杜比", StringComparison.OrdinalIgnoreCase) || quality.Contains("Dolby", StringComparison.OrdinalIgnoreCase)) return 2;
        if (quality.Contains("HDR", StringComparison.OrdinalIgnoreCase)) return 1;
        return 0;
    }

    [GeneratedRegex("(\\d+)\\s*[x×]\\s*(\\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ResolutionRegex();
}
