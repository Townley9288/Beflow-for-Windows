using BBDownForWindows.Core;
using Xunit;

namespace BBDownForWindows.Tests;

public sealed class BilibiliInputParserTests
{
    [Theory]
    [InlineData("https://www.bilibili.com/video/av115110811074987?t=1.0")]
    [InlineData("https://www.bilibili.com/bangumi/play/ep2104833?from_spmid=666.25.episode.0")]
    public void RecognizesSupportedVideoAndBangumiUrls(string input)
    {
        Assert.True(BilibiliInputParser.TryExtract(input, out var found));
        Assert.Equal(input, found);
    }

    [Fact]
    public void ExtractsShareUrlWithoutSurroundingPunctuation()
    {
        var found = BilibiliInputParser.ExtractAll("分享视频：https://b23.tv/AbCd123。");

        Assert.Equal(["https://b23.tv/AbCd123"], found);
    }

    [Fact]
    public void ExtractsDistinctUrlAndIdentifiersInSourceOrder()
    {
        var found = BilibiliInputParser.ExtractAll(
            "https://www.bilibili.com/video/BV1xx411c7mD?p=1 BV1xx411c7mD av170001 ep123 ss456");

        Assert.Equal(
        [
            "https://www.bilibili.com/video/BV1xx411c7mD?p=1",
            "BV1xx411c7mD",
            "av170001",
            "ep123",
            "ss456"
        ], found);
    }

    [Theory]
    [InlineData("https://bilibili.com.evil.example/video/BV1xx411c7mD")]
    [InlineData("https://example.com/watch/BV1xx411c7mD")]
    [InlineData("https://www.bilibili.com/")]
    [InlineData("https://space.bilibili.com/12345")]
    [InlineData("https://b23.tv/")]
    [InlineData("普通剪贴板文字")]
    public void RejectsUnsupportedClipboardContent(string text)
    {
        Assert.False(BilibiliInputParser.TryExtract(text, out _));
    }

    [Fact]
    public void NormalizesIdentifierPrefixWithoutChangingPayload()
    {
        Assert.True(BilibiliInputParser.TryExtract("bv1XX411C7Md", out var found));
        Assert.Equal("BV1XX411C7Md", found);
    }
}
