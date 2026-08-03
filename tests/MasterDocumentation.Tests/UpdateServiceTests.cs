using MasterDocumentation.Services;

namespace MasterDocumentation.Tests;

/// <summary>
/// Разбор ответа GitHub о последнем релизе: по нему приложение решает, предлагать обновление
/// или нет, и какой файл установщика скачивать.
/// </summary>
public sealed class UpdateServiceTests
{
    private const string ReleaseJson = """
    {
      "tag_name": "v1.4.2",
      "draft": false,
      "body": "Что нового",
      "assets": [
        { "name": "MasterDocumentation-v1.4.2-win-x64.zip", "browser_download_url": "https://example.invalid/portable.zip", "size": 10 },
        { "name": "MasterDocumentation-Setup-v1.4.2.exe", "browser_download_url": "https://example.invalid/setup.exe", "size": 403984031 },
        { "name": "MasterDocumentation-Setup-v1.4.2.exe.sha256", "browser_download_url": "https://example.invalid/setup.exe.sha256", "size": 80 }
      ]
    }
    """;

    [Theory]
    [InlineData("v1.4.2", "1.4.2")]
    [InlineData("1.4.2", "1.4.2")]
    [InlineData("v2.0.0-beta.1", "2.0.0")]
    [InlineData("v1.5", "1.5.0")]
    public void ParseVersion_ReadsTag(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), UpdateService.ParseVersion(tag));

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v1")]
    public void ParseVersion_RejectsUnusableTag(string tag) => Assert.Null(UpdateService.ParseVersion(tag));

    [Fact]
    public void ParseRelease_PicksInstallerAndChecksum()
    {
        var release = UpdateService.ParseRelease(ReleaseJson);

        Assert.NotNull(release);
        Assert.Equal(new Version(1, 4, 2), release!.Version);
        Assert.Equal("MasterDocumentation-Setup-v1.4.2.exe", release.InstallerName);
        Assert.Equal("https://example.invalid/setup.exe", release.InstallerUrl);
        Assert.Equal("https://example.invalid/setup.exe.sha256", release.ChecksumUrl);
        Assert.Equal(403984031, release.InstallerSize);
    }

    [Fact]
    public void ParseRelease_IgnoresDraft() =>
        Assert.Null(UpdateService.ParseRelease(ReleaseJson.Replace("\"draft\": false", "\"draft\": true")));

    /// <summary>Релиз без установщика обновлением не считается: обновлять было бы нечем.</summary>
    [Fact]
    public void ParseRelease_RequiresInstaller() =>
        Assert.Null(UpdateService.ParseRelease("""{ "tag_name": "v1.4.2", "assets": [ { "name": "notes.txt", "browser_download_url": "https://example.invalid/notes.txt" } ] }"""));

    [Fact]
    public void ParseRelease_RequiresVersionTag() =>
        Assert.Null(UpdateService.ParseRelease("""{ "tag_name": "nightly", "assets": [] }"""));
}
