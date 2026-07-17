using System.Text.RegularExpressions;

namespace BBDownForWindows.Core;

public sealed record TransferProgress(double Percent, string Speed, string Eta);

public sealed partial class Aria2ProgressParser
{
    private readonly Dictionary<string, (long Downloaded, long Total)> _transfers = new(StringComparer.OrdinalIgnoreCase);

    public bool TryConsume(string line, out TransferProgress progress)
    {
        progress = new TransferProgress(0, string.Empty, string.Empty);
        var match = ProgressRegex().Match(line);
        if (!match.Success) return false;
        var id = match.Groups[1].Value;
        if (!_transfers.ContainsKey(id) && _transfers.Count > 0)
        {
            foreach (var previous in _transfers.Keys.ToList())
            {
                var item = _transfers[previous];
                _transfers[previous] = (item.Total, item.Total);
            }
        }
        var downloaded = StreamSelectionPolicy.ParseSizeBytes(match.Groups[2].Value);
        var total = StreamSelectionPolicy.ParseSizeBytes(match.Groups[3].Value);
        if (total <= 0) return false;
        _transfers[id] = (Math.Clamp(downloaded, 0, total), total);
        var sumTotal = _transfers.Values.Sum(item => item.Total);
        var sumDownloaded = _transfers.Values.Sum(item => item.Downloaded);
        var percent = sumTotal > 0 ? Math.Clamp(sumDownloaded * 100d / sumTotal, 0, 100) : 0;
        progress = new TransferProgress(percent, match.Groups[5].Value, match.Groups[6].Value);
        return true;
    }

    [GeneratedRegex("\\[#([0-9a-f]+)\\s+([^/\\s]+)/([^\\s(]+)\\((\\d+)%\\).*?DL:([^\\s\\]]+)(?:\\s+ETA:([^\\s\\]]+))?", RegexOptions.IgnoreCase)]
    private static partial Regex ProgressRegex();
}
