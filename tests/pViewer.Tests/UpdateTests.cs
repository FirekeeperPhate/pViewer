using System.Text.Json;
using pViewer.Services;

namespace pViewer.Tests;

public class UpdateTests
{
    private static JsonElement Release(string tag, bool draft = false, bool prerelease = false, string host = "github.com", string? digest = null)
    {
        string digestJson = digest is null ? "" : $", \"digest\": \"{digest}\"";
        string json = $$"""
            {
              "tag_name": "{{tag}}", "draft": {{(draft ? "true" : "false")}}, "prerelease": {{(prerelease ? "true" : "false")}},
              "html_url": "https://github.com/MarcoTrombetta/pViewer/releases/tag/{{tag}}",
              "assets": [
                { "name": "pViewer-Setup-{{tag.TrimStart('v')}}-Full.exe", "size": 46000000,
                  "browser_download_url": "https://{{host}}/MarcoTrombetta/pViewer/releases/download/{{tag}}/pViewer-Setup-{{tag.TrimStart('v')}}-Full.exe"{{digestJson}} },
                { "name": "pViewer-Setup-{{tag.TrimStart('v')}}-Light.exe", "size": 4200000,
                  "browser_download_url": "https://{{host}}/MarcoTrombetta/pViewer/releases/download/{{tag}}/pViewer-Setup-{{tag.TrimStart('v')}}-Light.exe"{{digestJson}} }
              ]
            }
            """;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static readonly Version Current = new(2, 0, 6);

    [Fact]
    public void NewerReleaseOffersTheInstallerOfTheSameEdition()
    {
        var light = UpdateService.ParseRelease(Release("v2.0.7"), Current, fullEdition: false)!;
        Assert.Equal(new Version(2, 0, 7), light.Version);
        Assert.Equal("pViewer-Setup-2.0.7-Light.exe", light.InstallerName);
        Assert.Equal(4200000, light.InstallerSize);
        var full = UpdateService.ParseRelease(Release("v2.1.0"), Current, fullEdition: true)!;
        Assert.Equal("pViewer-Setup-2.1.0-Full.exe", full.InstallerName);
        Assert.EndsWith("/pViewer-Setup-2.1.0-Full.exe", full.InstallerUrl);
    }

    [Theory]
    [InlineData("v2.0.6")]   // the same version
    [InlineData("v2.0.5")]   // older
    [InlineData("v1.9")]
    [InlineData("latest")]   // not a version
    public void NothingToOfferForTheSameOrAnOlderVersion(string tag) =>
        Assert.Null(UpdateService.ParseRelease(Release(tag), Current, fullEdition: false));

    [Fact]
    public void DraftsPrereleasesAndForeignDownloadsAreIgnored()
    {
        Assert.Null(UpdateService.ParseRelease(Release("v2.0.7", draft: true), Current, false));
        Assert.Null(UpdateService.ParseRelease(Release("v2.0.7", prerelease: true), Current, false));
        Assert.Null(UpdateService.ParseRelease(Release("v2.0.7", host: "example.com"), Current, false));
        Assert.NotNull(UpdateService.ParseRelease(Release("v2.0.7", host: "objects.githubusercontent.com"), Current, false));
    }

    [Fact]
    public void ChecksumIsTakenWhenGitHubGivesIt()
    {
        var update = UpdateService.ParseRelease(Release("v2.0.7", digest: "sha256:ABCDEF0123"), Current, false)!;
        Assert.Equal("ABCDEF0123", update.Sha256);
        Assert.Null(UpdateService.ParseRelease(Release("v2.0.7"), Current, false)!.Sha256);
    }

    [Fact]
    public void ReleaseWithoutTheEditionsInstallerOffersNothing()
    {
        var json = JsonDocument.Parse("""{ "tag_name": "v3.0.0", "assets": [ { "name": "notes.txt", "size": 1, "browser_download_url": "https://github.com/x" } ] }""");
        Assert.Null(UpdateService.ParseRelease(json.RootElement, Current, false));
    }
}
