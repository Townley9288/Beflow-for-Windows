using System.Diagnostics;
using Beflow.Updater;

try
{
    var values = ParseArguments(args);
    var pid = int.Parse(Required(values, "wait-pid"));
    var package = Required(values, "package");
    var target = Required(values, "target");
    var restart = Required(values, "restart");
    var log = Required(values, "log");
    var working = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(package))!, "apply");

    try
    {
        using var process = Process.GetProcessById(pid);
        if (!process.WaitForExit((int)TimeSpan.FromMinutes(2).TotalMilliseconds))
            throw new TimeoutException("等待 Beflow 退出超时。");
    }
    catch (ArgumentException) { }

    new PortableUpdateEngine().Apply(new PortableUpdateOptions(package, target, working, log));
    Process.Start(new ProcessStartInfo
    {
        FileName = Path.GetFullPath(restart),
        WorkingDirectory = Path.GetFullPath(target),
        UseShellExecute = true
    });
    return 0;
}
catch (Exception exception)
{
    try
    {
        var values = ParseArguments(args);
        if (values.TryGetValue("log", out var log))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(log))!);
            File.AppendAllText(log, $"{DateTimeOffset.Now:O} 更新失败：{exception}{Environment.NewLine}");
        }
    }
    catch { }
    return 1;
}

static Dictionary<string, string> ParseArguments(string[] arguments)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < arguments.Length; index += 2)
    {
        if (!arguments[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= arguments.Length)
            throw new ArgumentException("更新助手参数无效。");
        result[arguments[index][2..]] = arguments[index + 1];
    }
    return result;
}

static string Required(IReadOnlyDictionary<string, string> values, string key) =>
    values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"缺少参数 --{key}。");
