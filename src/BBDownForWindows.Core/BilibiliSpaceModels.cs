namespace BBDownForWindows.Core;

public sealed record BilibiliSpaceProfile(string Uid, string Name, string AvatarUrl, string Description);
public sealed record BilibiliSpaceUser(string Uid, string Name, string AvatarUrl, string Description,
    long Fans, int Videos, string Verification);
public sealed record BilibiliUserSearchPage(IReadOnlyList<BilibiliSpaceUser> Items, int PageNumber,
    int PageSize, int Total, int TotalPages)
{
    public bool HasMore => PageNumber < TotalPages;
}
public sealed record BilibiliSpaceCategory(int Id, string Name, int Count);
public sealed record BilibiliSpaceVideo(string Aid, string Bvid, string Title, string CoverUrl,
    int DurationSeconds, DateTimeOffset PublishedAt, int CategoryId)
{
    public string Url => $"https://www.bilibili.com/video/{Bvid}";
}
public enum BilibiliSpaceCollectionKind { Season, Series }
public sealed record BilibiliSpaceCollection(string Id, string Uid, BilibiliSpaceCollectionKind Kind,
    string Title, string CoverUrl, string Description, int Count);
public sealed record BilibiliSpacePage<T>(IReadOnlyList<T> Items, int PageNumber, int PageSize, int Total,
    IReadOnlyList<BilibiliSpaceCategory>? Categories = null)
{
    public bool HasMore => (long)PageNumber * PageSize < Total;
}

public interface IBilibiliSpaceService
{
    Task<BilibiliUserSearchPage> SearchUsersAsync(string keyword, int page, CancellationToken cancellationToken = default);
    Task<BilibiliSpaceProfile> GetProfileAsync(string uid, CancellationToken cancellationToken = default);
    Task<BilibiliSpacePage<BilibiliSpaceVideo>> GetUploadsAsync(string uid, int page, CancellationToken cancellationToken = default);
    Task<BilibiliSpacePage<BilibiliSpaceCollection>> GetCollectionsAsync(string uid, int page, CancellationToken cancellationToken = default);
    Task<BilibiliSpacePage<BilibiliSpaceVideo>> GetCollectionVideosAsync(BilibiliSpaceCollection collection, int page, CancellationToken cancellationToken = default);
}

// A page is committed only after validation; cancellation can resume from the next unread page.
public sealed class BilibiliSpaceDirectory<T>(Func<T, string> identity)
{
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    public List<T> Items { get; } = [];
    public int NextPage { get; private set; } = 1;
    public int Total { get; private set; }
    public bool Complete { get; private set; }
    public IReadOnlyList<BilibiliSpaceCategory> Categories { get; private set; } = [];

    public void Append(BilibiliSpacePage<T> page)
    {
        if (Complete || page.PageNumber != NextPage || page.PageSize <= 0 || page.Total < 0
            || (page.Items.Count == 0 && (page.HasMore || page.Total > 0)))
            throw new InvalidDataException("目录分页数据不完整，请刷新后重试。");
        foreach (var item in page.Items)
            if (seen.Add(identity(item))) Items.Add(item);
        Total = page.Total;
        if (page.Categories is not null) Categories = page.Categories;
        NextPage++;
        Complete = !page.HasMore;
    }
}
