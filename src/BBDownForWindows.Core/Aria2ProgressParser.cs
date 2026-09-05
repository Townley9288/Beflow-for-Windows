using System.Globalization;
using System.Text.RegularExpressions;

namespace BBDownForWindows.Core;

public sealed record TransferProgress(double? Percent, string Speed, string Eta);

public sealed partial class Aria2ProgressParser(long videoBytes, long audioBytes, DownloadMode mode)
{
    private readonly TrackProgress _video = new(Math.Max(0, videoBytes));
    private readonly TrackProgress _audio = new(Math.Max(0, audioBytes));
    private TrackProgress? _current;
    private string _currentLabel = string.Empty;

    public bool TryConsume(string output, out TransferProgress progress)
    {
        progress = new TransferProgress(null, string.Empty, string.Empty);
        var consumed = false;
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryConsumeLine(line.Trim(), out var update))
            {
                consumed = true;
                progress = update;
            }
        }
        return consumed;
    }

    private bool TryConsumeLine(string line, out TransferProgress progress)
    {
        progress = new TransferProgress(null, string.Empty, string.Empty);
        var start = StartRegex().Match(line);
        if (start.Success)
        {
            var isVideo = start.Groups[1].Value == "视频";
            if (isVideo ? mode == DownloadMode.AudioOnly : mode == DownloadMode.VideoOnly) return false;
            var segment = start.Groups[2].Success ? int.Parse(start.Groups[2].Value, CultureInfo.InvariantCulture) : 1;
            var count = start.Groups[3].Success ? int.Parse(start.Groups[3].Value, CultureInfo.InvariantCulture) : 1;
            if (segment < 1 || count < segment) return false;
            _current = isVideo ? _video : _audio;
            _currentLabel = isVideo ? "当前视频" : "当前音频";
            if (isVideo && segment == 1) _audio.Reset();
            if (!isVideo && mode != DownloadMode.AudioOnly) _video.Complete();
            _current.Start(segment, count);
            progress = new TransferProgress(CurrentPercent(), string.Empty, string.Empty);
            return true;
        }

        if (FinishedRegex().IsMatch(line))
        {
            _video.Complete();
            _audio.Complete();
            _current = null;
            progress = new TransferProgress(100, string.Empty, string.Empty);
            return true;
        }

        if (_current is null) return false;
        if ((_current == _video && line.Contains("合并视频分片", StringComparison.Ordinal))
            || (_current == _audio && line.Contains("合并音频分片", StringComparison.Ordinal)))
        {
            _current.CompleteSegment();
            progress = new TransferProgress(CurrentPercent(), string.Empty, string.Empty);
            return true;
        }

        var match = ProgressRegex().Match(line);
        if (!match.Success) return false;
        var downloaded = StreamSelectionPolicy.ParseSizeBytes(match.Groups[1].Value);
        var total = StreamSelectionPolicy.ParseSizeBytes(match.Groups[2].Value);
        if (total <= 0) return false;
        _current.Report(Math.Clamp(downloaded, 0, total), total);
        var speed = match.Groups[3].Value;
        if (!speed.EndsWith("/s", StringComparison.OrdinalIgnoreCase)) speed += "/s";
        var eta = match.Groups[4].Success ? $"{match.Groups[4].Value}（{_currentLabel}）" : string.Empty;
        progress = new TransferProgress(CurrentPercent(), speed, eta);
        return true;
    }

    private double? CurrentPercent()
    {
        if (mode == DownloadMode.VideoOnly) return _video.Percent;
        if (mode == DownloadMode.AudioOnly) return _audio.Percent;
        if (_video.Percent is not { } video || _audio.Percent is not { } audio) return null;
        if (video == 100 && audio == 100) return 100;
        if (_video.EstimatedBytes <= 0 || _audio.EstimatedBytes <= 0) return null;
        return Math.Clamp((_video.EstimatedBytes * video + _audio.EstimatedBytes * audio)
            / (_video.EstimatedBytes + (double)_audio.EstimatedBytes), 0, 100);
    }

    private sealed class TrackProgress(long estimatedBytes)
    {
        private readonly Dictionary<int, (long Downloaded, long Total)> _segments = [];
        private int _index = 1;
        private int _count = 1;
        private bool _completed;
        public long EstimatedBytes { get; } = estimatedBytes;

        public double? Percent
        {
            get
            {
                if (_completed) return 100;
                if (_count == 1) return _segments.TryGetValue(1, out var item) ? item.Downloaded * 100d / item.Total : 0;
                // A completed segment may have produced no periodic output. Do not invent its size.
                if (EstimatedBytes <= 0 || Enumerable.Range(1, _index - 1).Any(index => !_segments.ContainsKey(index))) return null;
                return Math.Clamp(_segments.Values.Sum(item => (double)item.Downloaded) * 100 / EstimatedBytes, 0, 100);
            }
        }

        public void Reset()
        {
            _segments.Clear();
            _index = _count = 1;
            _completed = false;
        }

        public void Start(int index, int count)
        {
            if (index == 1) Reset();
            else CompleteSegment();
            _index = index;
            _count = count;
            _completed = false;
            _segments.Remove(index);
        }

        public void Report(long downloaded, long total) => _segments[_index] = (downloaded, total);
        public void Complete() => _completed = true;
        public void CompleteSegment()
        {
            if (_segments.TryGetValue(_index, out var item)) _segments[_index] = (item.Total, item.Total);
            if (_index == _count) Complete();
        }
    }

    [GeneratedRegex(@"(?:^|\] - )开始下载P\d+(视频|音频)(?:, 片段\((\d+)/(\d+)\))?")]
    private static partial Regex StartRegex();
    [GeneratedRegex(@"(?:^|\] - )下载P\d+完毕")]
    private static partial Regex FinishedRegex();
    [GeneratedRegex(@"\[#[0-9a-f]+\s+([^/\s]+)/([^\s(]+)\(\d+%\).*?DL:([^\s\]]+)(?:\s+ETA:([^\s\]]+))?", RegexOptions.IgnoreCase)]
    private static partial Regex ProgressRegex();
}
