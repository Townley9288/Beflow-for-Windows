using System.Text.RegularExpressions;

namespace BBDownForWindows.Core;

public static partial class BilibiliInputParser
{
    private static readonly char[] TrailingUrlPunctuation =
    [
        '.', ',', ';', ':', '!', '?', ')', ']', '}',
        '。', '，', '；', '：', '！', '？', '）', '】', '》', '」', '』', '、'
    ];

    public static bool TryExtract(string? text, out string input)
    {
        input = ExtractAll(text).FirstOrDefault() ?? string.Empty;
        return input.Length > 0;
    }

    public static IReadOnlyList<string> ExtractAll(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var urlMatches = UrlRegex().Matches(text).Cast<Match>().ToList();
        foreach (var match in urlMatches)
        {
            var candidate = match.Value.TrimEnd(TrailingUrlPunctuation);
            if (!IsSupportedUrl(candidate) || !seen.Add(candidate)) continue;
            result.Add(candidate);
        }

        foreach (Match match in IdentifierRegex().Matches(text))
        {
            if (urlMatches.Any(url => match.Index >= url.Index && match.Index < url.Index + url.Length)) continue;
            var candidate = NormalizeIdentifier(match.Groups["id"].Value);
            if (!seen.Add(candidate)) continue;
            result.Add(candidate);
        }

        return result;
    }

    public static bool IsSupportedUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;
        if (IsHostOrSubdomain(uri.Host, "b23.tv")) return uri.AbsolutePath.Length > 1;
        if (!IsHostOrSubdomain(uri.Host, "bilibili.com")) return false;
        return IdentifierRegex().IsMatch(Uri.UnescapeDataString(uri.PathAndQuery));
    }

    private static bool IsHostOrSubdomain(string host, string expected) =>
        host.Equals(expected, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith($".{expected}", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeIdentifier(string value)
    {
        if (value.StartsWith("bv", StringComparison.OrdinalIgnoreCase)) return $"BV{value[2..]}";
        if (value.StartsWith("av", StringComparison.OrdinalIgnoreCase)) return $"av{value[2..]}";
        if (value.StartsWith("ep", StringComparison.OrdinalIgnoreCase)) return $"ep{value[2..]}";
        return $"ss{value[2..]}";
    }

    [GeneratedRegex(@"https?://[^\s<>\""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?<id>BV[0-9A-Za-z]{10}|av\d+|ep\d+|ss\d+)(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();
}
