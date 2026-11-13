using System.Security.Cryptography;
using System.Text.Json;

namespace Handoff.Aspire.Hosting.Tests;

public class PackageTests(PublishedPackage package)
{
    [Fact]
    public void Publish_ShouldDescribeTheServiceInTheManifest_WhenGivenSampleAppHost()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(package.Root, "package.json")));
        var resource = Assert.Single(manifest.RootElement.GetProperty("resources").EnumerateArray());
        var service = resource.GetProperty("windowsService");

        Assert.Equal(1, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("worker", resource.GetProperty("name").GetString());
        Assert.Equal("1.2.3", resource.GetProperty("version").GetString());
        Assert.Equal("HandoffSampleWorker", service.GetProperty("serviceName").GetString());
        Assert.Equal("Sample.Worker.exe", service.GetProperty("executable").GetString());
        Assert.Equal(@"C:\Program Files\HandoffSampleWorker", service.GetProperty("installPath").GetString());
        Assert.Equal("delayedAutomatic", service.GetProperty("startMode").GetString());
        Assert.True(File.Exists(Path.Combine(package.Root, resource.GetProperty("source").GetString()!, "Sample.Worker.exe")));
    }

    [Fact]
    public void Publish_ShouldCarryParameterValuesButNotSecrets_WhenGivenSampleAppHost()
    {
        var manifestText = File.ReadAllText(Path.Combine(package.Root, "package.json"));
        using var manifest = JsonDocument.Parse(manifestText);
        var settings = manifest.RootElement.GetProperty("resources")[0].GetProperty("settings").EnumerateArray()
            .ToDictionary(setting => setting.GetProperty("name").GetString()!);

        Assert.Equal(PublishedPackage.Greeting, settings["Sample__Greeting"].GetProperty("value").GetString());
        Assert.True(settings["ConnectionStrings__sampledb"].GetProperty("secret").GetBoolean());
        Assert.DoesNotContain("not-in-the-package", manifestText);
    }

    [Fact]
    public async Task Publish_ShouldExitWithFailureAndNoManifest_WhenParameterHasNoValue()
    {
        var outputPath = Directory.CreateTempSubdirectory("handoff-failed-publish-").FullName;
        try
        {
            var result = await PublishedPackage.PublishSampleAsync(outputPath, new() { ["ConnectionStrings__sampledb"] = "x" });

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("'Parameters:greeting' is missing", result.Output);
            Assert.False(File.Exists(Path.Combine(outputPath, "handoff", "package.json")));
        }
        finally
        {
            Directory.Delete(outputPath, recursive: true);
        }
    }

    [Fact]
    public void Publish_ShouldWriteAChecksumForEveryFile_WhenGivenSampleAppHost()
    {
        var checksums = File.ReadAllLines(Path.Combine(package.Root, "checksums.sha256"))
            .ToDictionary(line => line[66..], line => line[..64]);
        var files = Directory.GetFiles(package.Root, "*", SearchOption.AllDirectories)
            .Where(file => Path.GetFileName(file) != "checksums.sha256")
            .ToDictionary(file => Path.GetRelativePath(package.Root, file).Replace('\\', '/'),
                file => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))));

        Assert.Equal(files.OrderBy(f => f.Key, StringComparer.Ordinal), checksums.OrderBy(c => c.Key, StringComparer.Ordinal));
        Assert.Contains("install.ps1", checksums.Keys);
    }
}
