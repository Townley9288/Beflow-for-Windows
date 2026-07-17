using System.Globalization;
using System.Text.RegularExpressions;

namespace BBDownForWindows.Core;

public sealed partial class BBDownInternalProgressParser(long videoBytes, long audioBytes, DownloadMode mode)
{
    private enum TransferKind { Video, Audio }

    private readonly long _videoBytes = mode == DownloadMode.AudioOnly ? 0 : Math.Max(0, videoBytes);
    private readonly long _audioBytes = mode == DownloadMode.VideoOnly ? 0 : Math.Max(0, audioBytes);
    private TransferKind _current = mode == DownloadMode.AudioOnly ? TransferKind.Audio : TransferKind.Video;
    private double _videoPercent;
    private double _audioPercent;
    private string _lastSpeed = string.Empty;

    public bool TryConsume(string output, out TransferProgress progress)
    {
        progress = new TransferProgress(CurrentPercent(), string.Empty, string.Empty);
        if (output.Contains("开始下载", StringComparison.Ordinal) && output.Contains("音频", StringComparison.Ordinal))
        {
            _videoPercent = _videoBytes > 0 ? 100 : _videoPercent;
            _current = TransferKind.Audio;
            _lastSpeed = string.Empty;
        }
        else if (output.Contains("开始下载", StringComparison.Ordinal) && output.Contains("视频", StringComparison.Ordinal))
        {
            _current = TransferKind.Video;
            _lastSpeed = string.Empty;
        }
        else if (output.Contains("合并视频分片", StringComparison.Ordinal))
        {
            _videoPercent = _videoBytes > 0 ? 100 : _videoPercent;
        }
        else if (output.Contains("合并音频分片", StringComparison.Ordinal))
        {
            _audioPercent = _audioBytes > 0 ? 100 : _audioPercent;
        }

        var percentMatch = PercentRegex().Match(output);
        if (!percentMatch.Success) return false;
        var percent = Math.Clamp(double.Parse(percentMatch.Groups[1].Value, CultureInfo.InvariantCulture), 0, 100);
        if (_current == TransferKind.Video) _videoPercent = Math.Max(_videoPercent, percent);
        else _audioPercent = Math.Max(_audioPercent, percent);

        var speedMatch = SpeedRegex().Match(output);
        if (speedMatch.Success) _lastSpeed = speedMatch.Value.Replace(" ", " ").Trim();
        var overallPercent = CurrentPercent();
        progress = new TransferProgress(overallPercent, _lastSpeed, BuildEta(overallPercent, _lastSpeed));
        return true;
    }

    public static bool IsProgressOutput(string output) => PercentRegex().IsMatch(output);

    private double CurrentPercent()
    {
        var total = _videoBytes + _audioBytes;
        if (total <= 0) return _current == TransferKind.Video ? _videoPercent : _audioPercent;
        var completed = _videoBytes * _videoPercent / 100d + _audioBytes * _audioPercent / 100d;
        return Math.Clamp(completed * 100d / total, 0, 100);
    }

    private string BuildEta(double overallPercent, string speed)
    {
        var total = _videoBytes + _audioBytes;
        var bytesPerSecond = ParseSpeed(speed);
        if (total <= 0 || bytesPerSecond <= 0 || overallPercent >= 100) return string.Empty;
        var remaining = total * (100d - overallPercent) / 100d;
        var seconds = Math.Clamp((long)Math.Ceiling(remaining / bytesPerSecond), 0, 24 * 60 * 60);
        return FormatDuration(TimeSpan.FromSeconds(seconds));
    }

    private static double ParseSpeed(string speed)
    {
        var match = SpeedRegex().Match(speed);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return 0;
        var multiplier = match.Groups[2].Value.ToUpperInvariant() switch
        {
            "KB" => 1024d,
            "MB" => 1024d * 1024,
            "GB" => 1024d * 1024 * 1024,
            "TB" => 1024d * 1024 * 1024 * 1024,
            _ => 1d
        };
        return value * multiplier;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1) return $"约 {(int)duration.TotalHours}时{duration.Minutes:00}分";
        if (duration.TotalMinutes >= 1) return $"约 {(int)duration.TotalMinutes}分{duration.Seconds:00}秒";
        return $"约 {Math.Max(1, duration.Seconds)}秒";
    }

    [GeneratedRegex("(?<!\\d)(\\d{1,3})%")]
    private static partial Regex PercentRegex();

    [GeneratedRegex("(\\d+(?:\\.\\d+)?)\\s*(B|KB|MB|GB|TB)/s", RegexOptions.IgnoreCase)]
    private static partial Regex SpeedRegex();
}
