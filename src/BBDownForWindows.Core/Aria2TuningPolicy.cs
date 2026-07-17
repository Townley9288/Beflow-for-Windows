namespace BBDownForWindows.Core;

public sealed record Aria2TuningResult(
    bool Applied,
    long EstimatedStreamBytes,
    int MaxConnection,
    int Split,
    int MaxConcurrentDownloads,
    int MinSplitSizeMb)
{
    public string Description =>
        $"aria2c 自动调优：{MediaEstimateFormatter.FormatBytes(EstimatedStreamBytes)}，连接 {MaxConnection}，分片 {Split}，同时任务 {MaxConcurrentDownloads}，最小分片 {MinSplitSizeMb} MB";
}

public static class Aria2TuningPolicy
{
    private const long SmallStreamLimit = 128L * 1024 * 1024;
    private const long MediumStreamLimit = 1024L * 1024 * 1024;

    public static Aria2TuningResult Apply(DownloadRequest request, long estimatedStreamBytes)
    {
        var maxConnection = Math.Clamp(request.Aria2MaxConnection, 1, 32);
        var split = Math.Clamp(request.Aria2Split, 1, 32);
        var concurrent = Math.Clamp(request.Aria2MaxConcurrentDownloads, 1, 32);
        var minSplitSize = Math.Clamp(request.Aria2MinSplitSize, 1, 64);
        if (!request.Aria2AutoTune || estimatedStreamBytes <= 0)
            return new Aria2TuningResult(false, estimatedStreamBytes, maxConnection, split, concurrent, minSplitSize);

        var recommended = estimatedStreamBytes < SmallStreamLimit
            ? 4
            : estimatedStreamBytes < MediumStreamLimit
                ? 8
                : 16;
        request.Aria2MaxConnection = Math.Min(maxConnection, recommended);
        request.Aria2Split = Math.Min(split, recommended);
        request.Aria2MaxConcurrentDownloads = Math.Min(concurrent, 4);
        request.Aria2MinSplitSize = minSplitSize;
        return new Aria2TuningResult(true, estimatedStreamBytes, request.Aria2MaxConnection, request.Aria2Split,
            request.Aria2MaxConcurrentDownloads, request.Aria2MinSplitSize);
    }
}
