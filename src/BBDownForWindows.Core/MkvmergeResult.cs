namespace BBDownForWindows.Core;

internal static class MkvmergeResult
{
    public static void EnsureOutput(ProcessResult result, string output, TaskExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Cancelled) throw new OperationCanceledException(cancellationToken);
        if (result.ExitCode is not (0 or 1) || !File.Exists(output))
            throw new IOException($"mkvmerge 封装失败，退出码 {result.ExitCode}，输出文件：{output}");
        if (result.ExitCode == 1) context.AppendLog("mkvmerge 封装完成，但报告了警告，请查看上方输出。\n");
    }
}
