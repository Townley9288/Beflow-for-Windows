using BBDownForWindows.Core;
using Windows.ApplicationModel.DataTransfer;

namespace BBDownForWindows.App;

internal static class BilibiliDataTransfer
{
    public static bool MayContainInput(DataPackageView data) =>
        data.Contains(StandardDataFormats.WebLink) || data.Contains(StandardDataFormats.Text);

    public static async Task<IReadOnlyList<string>> ExtractInputsAsync(DataPackageView data)
    {
        var sourceValues = new List<string>();
        if (data.Contains(StandardDataFormats.WebLink))
        {
            var link = await data.GetWebLinkAsync();
            if (link is not null) sourceValues.Add(link.AbsoluteUri);
        }
        if (data.Contains(StandardDataFormats.Text))
        {
            var text = await data.GetTextAsync();
            if (!string.IsNullOrWhiteSpace(text)) sourceValues.Add(text);
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in sourceValues)
        foreach (var input in BilibiliInputParser.ExtractAll(value))
            if (seen.Add(input)) result.Add(input);
        return result;
    }
}
