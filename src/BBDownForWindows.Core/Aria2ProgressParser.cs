using System.Text.RegularExpressions;

namespace BBDownForWindows.Core;

public sealed record TransferProgress(double Percent, string Speed, string Eta);

public sealed partial class Aria2ProgressParser
{
    private readonly Dictionary<string, (long Downloaded, long Total)> _transfers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _transferWeights = new(StringComparer.OrdinalIgnoreCase);
    private readonly long[] _plannedTransferBytes = [];
    private readonly long _plannedTotalBytes;
    private int _nextPlannedTransfer;

    public Aria2ProgressParser() { }

    public Aria2ProgressParser(long videoBytes, long audioBytes, DownloadMode mode)
    {
        var planned = new List<long>();
        if (mode != DownloadMode.AudioOnly) planned.Add(videoBytes);
        if (mode != DownloadMode.VideoOnly) planned.Add(audioBytes);
        if (planned.Count > 0 && planned.All(value => value > 0))
        {
            _plannedTransferBytes = planned.ToArray();
            _plannedTotalBytes = planned.Sum();
        }
    }

    public bool TryConsume(string line, out TransferProgress progress)
    {
        progress = new TransferProgress(0, string.Empty, string.Empty);
        var match = ProgressRegex().Match(line);
        if (!match.Success) return false;
        var id = match.Groups[1].Value;
        var downloaded = StreamSelectionPolicy.ParseSizeBytes(match.Groups[2].Value);
        var total = StreamSelectionPolicy.ParseSizeBytes(match.Groups[3].Value);
        if (total <= 0) return false;
        if (!_transfers.ContainsKey(id) && _transfers.Count > 0)
        {
            foreach (var previous in _transfers.Keys.ToList())
            {
                var item = _transfers[previous];
                _transfers[previous] = (item.Total, item.Total);
            }
        }
        if (!_transferWeights.ContainsKey(id))
        {
            _transferWeights[id] = _nextPlannedTransfer < _plannedTransferBytes.Length
                ? _plannedTransferBytes[_nextPlannedTransfer++]
                : total;
        }
        _transfers[id] = (Math.Clamp(downloaded, 0, total), total);
        double percent;
        if (_plannedTotalBytes > 0)
        {
            var weightedDownloaded = _transfers.Sum(item =>
                _transferWeights[item.Key] * (item.Value.Downloaded / (double)item.Value.Total));
            percent = Math.Clamp(weightedDownloaded * 100d / _plannedTotalBytes, 0, 100);
        }
        else
        {
            var sumTotal = _transfers.Values.Sum(item => item.Total);
            var sumDownloaded = _transfers.Values.Sum(item => item.Downloaded);
            percent = sumTotal > 0 ? Math.Clamp(sumDownloaded * 100d / sumTotal, 0, 100) : 0;
        }
        progress = new TransferProgress(percent, match.Groups[5].Value, match.Groups[6].Value);
        return true;
    }

    [GeneratedRegex("\\[#([0-9a-f]+)\\s+([^/\\s]+)/([^\\s(]+)\\((\\d+)%\\).*?DL:([^\\s\\]]+)(?:\\s+ETA:([^\\s\\]]+))?", RegexOptions.IgnoreCase)]
    private static partial Regex ProgressRegex();
}
