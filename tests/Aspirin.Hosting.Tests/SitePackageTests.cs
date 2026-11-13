using System.Text.Json;

namespace Aspirin.Hosting.Tests;

public class SitePackageTests(PublishedSitePackage package)
{
    [Fact]
    public void Publish_ShouldDescribeTheSiteInTheManifest_WhenGivenSiteAppHost()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(package.Root, "package.json")));
        var resource = Assert.Single(manifest.RootElement.GetProperty("resources").EnumerateArray());
        var site = resource.GetProperty("iisSite");
        var bindings = site.GetProperty("bindings").EnumerateArray().ToList();

        Assert.Equal("iisSite", resource.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, resource.GetProperty("windowsService").ValueKind);
        Assert.Equal("Aspirin Sample Web", site.GetProperty("siteName").GetString());
        Assert.Equal("Aspirin Sample Web", site.GetProperty("appPoolName").GetString());
        Assert.Equal(@"C:\inetpub\AspirinSampleWeb", resource.GetProperty("installPath").GetString());
        Assert.Equal(["http", "https"], bindings.Select(binding => binding.GetProperty("protocol").GetString()));
        Assert.False(bindings[0].GetProperty("sni").GetBoolean());
        Assert.True(bindings[1].GetProperty("sni").GetBoolean());
        Assert.Equal("My", bindings[1].GetProperty("certificateStore").GetString());
        Assert.True(File.Exists(Path.Combine(package.Root, resource.GetProperty("source").GetString()!, "web.config")));
    }

    [Fact]
    public void Publish_ShouldLeaveOutTheListeningAddress_WhenProjectHasLaunchSettingsEndpoints()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(package.Root, "package.json")));
        var settings = manifest.RootElement.GetProperty("resources")[0].GetProperty("settings").EnumerateArray()
            .Select(setting => setting.GetProperty("name").GetString()).ToList();

        Assert.Contains("Sample__Greeting", settings);
        Assert.DoesNotContain("HTTP_PORTS", settings);
    }

    [Fact]
    public void Publish_ShouldIncludeTheIisScript_WhenGivenSiteAppHost()
    {
        var checksums = File.ReadAllLines(Path.Combine(package.Root, "checksums.sha256")).Select(line => line[66..]);

        Assert.Contains("iis.ps1", checksums);
    }
}
