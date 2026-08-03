using SteamFinish.Core.Updates;

namespace SteamFinish.Tests;

public class VersionComparisonTests
{
    [Theory]
    [InlineData("1.0.1", "1.0.0")]
    [InlineData("1.1.0", "1.0.9")]
    [InlineData("2.0.0", "1.99.99")]
    [InlineData("1.10.0", "1.9.0")]   // a string comparison gets this one wrong
    [InlineData("1.0.10", "1.0.9")]
    [InlineData("v1.2.0", "1.1.0")]
    [InlineData("1.2", "1.1.9")]
    public void NewerVersionsAreRecognised(string candidate, string current)
    {
        Assert.True(ReleaseReader.IsNewer(candidate, current), $"{candidate} should beat {current}");
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("1.9.0", "1.10.0")]
    [InlineData("1.0.0", "1.0.0.0")]
    [InlineData("v1.0.0", "1.0.0")]
    public void SameOrOlderVersionsAreNot(string candidate, string current)
    {
        Assert.False(ReleaseReader.IsNewer(candidate, current), $"{candidate} should not beat {current}");
    }

    [Theory]
    [InlineData("not-a-version", "1.0.0")]
    [InlineData("", "1.0.0")]
    [InlineData("1.0.0", "rubbish")]
    public void UnparseableVersionsNeverTriggerAnUpdate(string candidate, string current)
    {
        Assert.False(ReleaseReader.IsNewer(candidate, current));
    }

    [Fact]
    public void BuildMetadataFromTheSdkIsIgnored()
    {
        // dotnet stamps "1.2.3+<commit>"; only the numbers matter.
        Assert.Equal("1.2.3", ReleaseReader.Normalize("1.2.3+588dc9c876f7cc98"));
        Assert.Equal("1.2.3", ReleaseReader.Normalize("v1.2.3"));
        Assert.False(ReleaseReader.IsNewer("1.2.3+abc", "1.2.3"));
    }
}

public class ReleasePayloadTests
{
    private const string Payload = """
        {"tag_name":"v1.4.0","html_url":"https://github.com/hmh6a/SteamFinish/releases/tag/v1.4.0",
         "draft":false,"prerelease":false,
         "assets":[
           {"name":"SteamFinish-1.4.0-win-x64.zip","size":155189248,
            "browser_download_url":"https://github.com/hmh6a/SteamFinish/releases/download/v1.4.0/SteamFinish-1.4.0-win-x64.zip"},
           {"name":"SteamFinish-1.4.0-win-x64.zip.sha256","size":80,
            "browser_download_url":"https://github.com/hmh6a/SteamFinish/releases/download/v1.4.0/SteamFinish-1.4.0-win-x64.zip.sha256"}]}
        """;

    [Fact]
    public void ReadsTheZipAndItsChecksum()
    {
        var release = ReleaseReader.Read(Payload)!;

        Assert.Equal("1.4.0", release.Version);
        Assert.Equal("v1.4.0", release.Tag);
        Assert.EndsWith("SteamFinish-1.4.0-win-x64.zip", release.DownloadUrl, StringComparison.Ordinal);
        Assert.EndsWith(".sha256", release.ChecksumUrl!, StringComparison.Ordinal);
        Assert.Equal(155_189_248, release.SizeBytes);
    }

    [Fact]
    public void ADraftIsNotOffered()
    {
        var draft = Payload.Replace("\"draft\":false", "\"draft\":true", StringComparison.Ordinal);

        Assert.Null(ReleaseReader.Read(draft));
    }

    [Fact]
    public void AReleaseWithNoBuildAttachedIsSkipped()
    {
        Assert.Null(ReleaseReader.Read("""{"tag_name":"v1.4.0","assets":[]}"""));
        Assert.Null(ReleaseReader.Read("""{"tag_name":"v1.4.0"}"""));
    }

    [Fact]
    public void AChecksumIsOptional()
    {
        var noChecksum = """
            {"tag_name":"v2.0.0","assets":[{"name":"SteamFinish-2.0.0-win-x64.zip","size":1,
             "browser_download_url":"https://example.invalid/a.zip"}]}
            """;

        var release = ReleaseReader.Read(noChecksum)!;

        Assert.Equal("2.0.0", release.Version);
        Assert.Null(release.ChecksumUrl);
    }

    [Fact]
    public void GarbageIsRejectedRatherThanThrown()
    {
        Assert.Null(ReleaseReader.Read("<html>rate limited</html>"));
        Assert.Null(ReleaseReader.Read("[]"));
    }

    [Fact]
    public void TheRunningVersionIsReadable()
    {
        // Whatever the build stamped, it must parse as a version so comparisons work at all.
        Assert.Matches(@"^\d+(\.\d+)*$", UpdateService.CurrentVersion);
    }
}
