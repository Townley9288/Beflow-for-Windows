using System.Text.RegularExpressions;

namespace BBDownForWindows.Core;

internal static partial class BilibiliRequestFailure
{
    public static string RiskControlMessage(string output)
    {
        if (Http412Regex().IsMatch(output))
            return "接口返回 HTTP 412（风控），本轮解析已停止。已保留取得的结果，请稍后手动重试。";
        if (RiskCodeRegex().IsMatch(output))
            return "接口返回 -352（风控校验失败），本轮解析已停止。已保留取得的结果，请确认登录状态后手动重试。";
        return string.Empty;
    }

    [GeneratedRegex(@"(?:HTTP(?:/\d(?:\.\d)?)?\s+412\b|status\s+code[^\r\n]*\b412\b|not_success_statuscode_reason\s*,\s*412\b|\b412\s*[,:(]\s*Precondition Failed)", RegexOptions.IgnoreCase)]
    private static partial Regex Http412Regex();
    [GeneratedRegex("(?:[\"']?code[\"']?\\s*[:=]\\s*-352\\b|接口错误\\s*-352\\b)", RegexOptions.IgnoreCase)]
    private static partial Regex RiskCodeRegex();
}
