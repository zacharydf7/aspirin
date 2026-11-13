using System.Text.Json;

namespace Aspirin.Hosting.Tests;

/// <summary>
/// Builds the example in the guide "Moving build-time placeholders into AppHost parameters" the way a pipeline would,
/// with its variables set.
/// </summary>
public class PlaceholdersPackageTests
{
    [Fact]
    public async Task Publish_ShouldCarryTheVariableValuesButNotTheSecrets_WhenGivenThePipelineVariables()
    {
        var outputPath = Directory.CreateTempSubdirectory("aspirin-placeholders-").FullName;
        try
        {
            var result = await PublishedSample.PublishSampleAsync(outputPath,
                new() { ["SMTP_HOST"] = "smtp.contoso.local", ["SMTP_PASSWORD"] = "smtp-s3cret", ["JOBS_DATABASE"] = "Server=sql01;Password=db-s3cret" },
                "Placeholders.AppHost");
            Assert.True(result.ExitCode == 0, result.Output);
            // A secret the package doesn't carry is never read, so a parameter bound to the wrong variable doesn't fail
            // the build. The only sign is Aspire reporting its configuration key as missing.
            Assert.DoesNotContain("is missing", result.Output);

            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(outputPath, "aspirin", "package.json")));
            var settings = manifest.RootElement.GetProperty("resources")[0].GetProperty("settings").EnumerateArray()
                .ToDictionary(setting => setting.GetProperty("name").GetString()!);

            Assert.Equal("smtp.contoso.local", settings["Smtp__Host"].GetProperty("value").GetString());
            foreach (var secret in new[] { "Smtp__Password", "ConnectionStrings__Jobs" })
            {
                Assert.True(settings[secret].GetProperty("secret").GetBoolean());
                Assert.Equal(JsonValueKind.Null, settings[secret].GetProperty("value").ValueKind);
            }
        }
        finally
        {
            Directory.Delete(outputPath, recursive: true);
        }
    }
}
