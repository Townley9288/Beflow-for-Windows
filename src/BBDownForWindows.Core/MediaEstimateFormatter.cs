namespace BBDownForWindows.Core;

public static class MediaEstimateFormatter
{
    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "大小未知";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }
}
