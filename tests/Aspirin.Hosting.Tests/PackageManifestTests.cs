namespace Aspirin.Hosting.Tests;

public class PackageManifestTests
{
    [Fact]
    public void ToJson_ShouldWriteCharactersAsTheyAre_WhenGivenVersionAndSettingWithSymbols()
    {
        var service = new WindowsServiceManifest("Worker", "Worker", null, "Worker.exe", @"C:\Services\Worker", @"C:\Backups\Worker",
            WindowsServiceStartMode.Automatic, null);
        var manifest = new PackageManifest(PackageManifest.CurrentSchemaVersion, "AppHost", "Production",
            [new PackageResource("worker", "resources/worker/app", "1.2.3+abc", service, [new("Url", "https://example.test/?a=1&b='2'")])]);

        var json = manifest.ToJson();

        Assert.Contains("\"version\": \"1.2.3+abc\"", json);
        Assert.Contains("\"value\": \"https://example.test/?a=1&b='2'\"", json);
    }
}
