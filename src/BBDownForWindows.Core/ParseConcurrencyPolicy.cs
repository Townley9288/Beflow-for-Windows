namespace BBDownForWindows.Core;

public static class ParseConcurrencyPolicy
{
    public const int Default = 4;
    public static IReadOnlyList<int> Options { get; } = Array.AsReadOnly(new[] { 4, 6, 8 });

    public static void Validate(int concurrency)
    {
        if (!Options.Contains(concurrency))
            throw new InvalidOperationException("解析并发数请选择 4、6 或 8 路。");
    }
}
