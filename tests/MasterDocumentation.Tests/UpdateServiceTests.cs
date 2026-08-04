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

    [Fact]
    public void ParseRelease_ReadsPageAndPreReleaseFlag()
    {
        var release = UpdateService.ParseRelease(ReleaseJson.Replace("\"draft\": false", "\"draft\": false, \"prerelease\": true, \"html_url\": \"https://example.invalid/releases/v1.4.2\""));

        Assert.True(release!.IsPreRelease);
        Assert.Equal("https://example.invalid/releases/v1.4.2", release.PageUrl);
    }

    /// <summary>
    /// Стабильной копии предлагается только стабильный выпуск, бете — самый свежий,
    /// включая предварительный.
    /// </summary>
    [Theory]
    [InlineData(false, "1.3.0")]
    [InlineData(true, "1.4.0")]
    public void SelectRelease_RespectsPreReleaseChoice(bool includePreRelease, string expected)
    {
        var list = $"""
        [
          { Release("v1.4.0", true) },
          { Release("v1.3.0", false) },
          { Release("v1.2.0", false) }
        ]
        """;

        var release = UpdateService.SelectRelease(list, includePreRelease);

        Assert.Equal(Version.Parse(expected), release!.Version);
    }

    /// <summary>
    /// По семантическому версионированию 1.2.0-beta старше 1.2.0, поэтому бета должна
    /// обновляться до одноимённого стабильного выпуска, а не считать версии равными.
    /// </summary>
    [Fact]
    public void Compare_TreatsPreReleaseAsOlderThanSameNumbers()
    {
        var numbers = new Version(1, 2, 0);

        Assert.True(UpdateService.Compare(numbers, null, numbers, "beta") > 0);
        Assert.True(UpdateService.Compare(numbers, "beta", numbers, null) < 0);
        Assert.Equal(0, UpdateService.Compare(numbers, "beta", numbers, "beta"));
        Assert.True(UpdateService.Compare(new Version(1, 2, 1), "beta", numbers, null) > 0);
    }

    [Theory]
    [InlineData("v1.2.0", null)]
    [InlineData("v1.2.0-beta", "beta")]
    [InlineData("1.3.0-rc.2", "rc.2")]
    [InlineData("v1.2.0+build7", null)]
    public void ParseSuffix_ReadsPreReleasePart(string tag, string? expected) =>
        Assert.Equal(expected, UpdateService.ParseSuffix(tag));

    /// <summary>Из беты и одноимённого выпуска новее считается стабильный.</summary>
    [Fact]
    public void SelectRelease_PrefersStableOverSamePreRelease()
    {
        var list = $"[ {Release("v1.2.0-beta", true)}, {Release("v1.2.0", false)} ]";

        var release = UpdateService.SelectRelease(list, includePreRelease: true);

        Assert.Equal("v1.2.0", release!.Tag);
        Assert.Equal("1.2.0", release.Display);
    }

    [Fact]
    public void SelectRelease_ReturnsNothingWhenOnlyPreReleasesAvailable() =>
        Assert.Null(UpdateService.SelectRelease($"[ {Release("v1.4.0", true)} ]", includePreRelease: false));

    private static string Release(string tag, bool preRelease) => $$"""
    {
      "tag_name": "{{tag}}",
      "prerelease": {{(preRelease ? "true" : "false")}},
      "html_url": "https://example.invalid/releases/{{tag}}",
      "assets": [
        { "name": "MasterDocumentation-Setup-{{tag}}.exe", "browser_download_url": "https://example.invalid/{{tag}}/setup.exe", "size": 1 }
      ]
    }
    """;
}
