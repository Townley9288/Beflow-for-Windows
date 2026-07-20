using System.Text;
using System.Text.RegularExpressions;

namespace BBDownForWindows.Core;

internal sealed partial class StreamingDownloadParser
{
    private readonly DownloadParseMode _mode;
    private readonly IProgress<DownloadParseProgress>? _progress;
    private readonly int _expectedTotal;
    private readonly Dictionary<int, PageInfo> _pages = [];
    private readonly List<DownloadEpisodeInfo> _episodes = [];
    private readonly StringBuilder _currentOutput = new();
    private int? _currentPage;
    private bool _currentFailed;
    private string _currentError = string.Empty;
    private int _sourceTotal;
    private string _title = string.Empty;

    public StreamingDownloadParser(DownloadParseMode mode, IProgress<DownloadParseProgress>? progress, int expectedTotal = 0)
    {
        _mode = mode;
        _progress = progress;
        _expectedTotal = expectedTotal;
    }

    public IReadOnlyList<DownloadEpisodeInfo> Episodes => _episodes;
    public IReadOnlyList<PageInfo> Pages => _pages.Values.OrderBy(item => item.Number).ToList();
    public string Title => _title;

    public void Consume(string line)
    {
        var title = TitleRegex().Match(line);
        if (title.Success) _title = title.Groups[1].Value.Trim();

        var page = PageRegex().Match(line);
        if (page.Success)
        {
            var info = new PageInfo(int.Parse(page.Groups[1].Value), page.Groups[2].Value, page.Groups[3].Value, page.Groups[4].Value);
            _pages[info.Number] = info;
        }

        var total = TotalPagesRegex().Match(line);
        if (total.Success) _sourceTotal = int.Parse(total.Groups[1].Value);

        var parsing = ParsingPageRegex().Match(line);
        if (parsing.Success)
        {
            FinalizeCurrent();
            _currentPage = int.Parse(parsing.Groups[1].Value);
            _currentFailed = false;
            _currentError = string.Empty;
            _currentOutput.Clear();
            _currentOutput.Append(line);
            var pageTitle = _pages.TryGetValue(_currentPage.Value, out var current) ? current.Title : $"P{_currentPage}";
            _progress?.Report(new DownloadParseProgress(_episodes.Count, TargetTotal, _currentPage.Value, pageTitle, null, $"正在解析 P{_currentPage}：{pageTitle}"));
            return;
        }

        if (_currentPage is not null)
        {
            _currentOutput.Append(line);
            if (line.Contains("解析此分P失败", StringComparison.Ordinal))
            {
                _currentFailed = true;
                _currentError = "BBDown 解析此分P失败";
            }
            else
            {
                var jsonError = JsonErrorRegex().Match(line);
                if (_currentFailed && jsonError.Success) _currentError = jsonError.Groups[1].Value.Trim();
            }
        }

        if (line.Contains("任务完成", StringComparison.Ordinal)) FinalizeCurrent();
    }

    public void Complete() => FinalizeCurrent();

    private int TargetTotal => _expectedTotal > 0 ? _expectedTotal : _mode == DownloadParseMode.Current ? 1 : Math.Max(_sourceTotal, _pages.Count);

    private void FinalizeCurrent()
    {
        if (_currentPage is null) return;
        var number = _currentPage.Value;
        var page = _pages.TryGetValue(number, out var pageInfo) ? pageInfo : new PageInfo(number, string.Empty, $"P{number}", string.Empty);
        var output = _currentOutput.ToString();
        var video = BBDownParser.ParsePageVideoStreams(output).GetValueOrDefault(number) ?? [];
        var audio = BBDownParser.ParsePageAudioStreams(output).GetValueOrDefault(number) ?? [];
        var isMuxedStream = BBDownParser.ParseMuxedStreamPages(output).Contains(number);
        var failed = _currentFailed || video.Count == 0;
        var episode = new DownloadEpisodeInfo
        {
            Page = page,
            VideoStreams = video,
            AudioStreams = audio,
            IsMuxedStream = isMuxedStream,
            State = failed ? DownloadEpisodeParseState.Failed : DownloadEpisodeParseState.Ready,
            Error = failed ? (string.IsNullOrWhiteSpace(_currentError) ? "没有解析到可用视频流" : _currentError) : string.Empty
        };
        _episodes.Add(episode);
        _progress?.Report(new DownloadParseProgress(_episodes.Count, TargetTotal, number, page.Title, episode,
            failed ? $"P{number} 解析失败：{episode.Error}" : $"已解析 {_episodes.Count}/{TargetTotal}：P{number} {page.Title}"));
        _currentPage = null;
        _currentOutput.Clear();
    }

    [GeneratedRegex("视频标题:\\s*(.+)$")] private static partial Regex TitleRegex();
    [GeneratedRegex("\\bP(\\d+):\\s*\\[(\\d+)\\]\\s*\\[(.+?)\\]\\s*\\[([^\\]]+)\\]")] private static partial Regex PageRegex();
    [GeneratedRegex("开始解析P(\\d+):")] private static partial Regex ParsingPageRegex();
    [GeneratedRegex("共计\\s*(\\d+)\\s*个分P")] private static partial Regex TotalPagesRegex();
    [GeneratedRegex("\\{\\\"code\\\".*?\\\"message\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"")] private static partial Regex JsonErrorRegex();
}
