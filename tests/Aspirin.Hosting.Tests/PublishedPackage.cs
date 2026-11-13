using System.Diagnostics;
using Aspirin.Hosting.Tests;

[assembly: AssemblyFixture(typeof(PublishedPackage))]

namespace Aspirin.Hosting.Tests;

/// <summary>
/// Publishes the sample AppHost once, the way a CI step would, and shares the package with every test.
/// </summary>
public sealed class PublishedPackage : IAsyncLifetime
{
    public const string Greeting = "Hello from the tests";
    public const string ConnectionString = "Server=db;Password=not-in-the-package";

    private readonly string _outputPath = Directory.CreateTempSubdirectory("aspirin-package-").FullName;

    public string Root => Path.Combine(_outputPath, "aspirin");

    public async ValueTask InitializeAsync()
    {
        var result = await PublishSampleAsync(_outputPath,
            new() { ["Parameters__greeting"] = Greeting, ["ConnectionStrings__sampledb"] = ConnectionString });

        Assert.True(result.ExitCode == 0, result.Output);
    }

    public ValueTask DisposeAsync()
    {
        Directory.Delete(_outputPath, recursive: true);
        return ValueTask.CompletedTask;
    }

    public static Task<(int ExitCode, string Output)> PublishSampleAsync(string outputPath, Dictionary<string, string> parameters)
    {
        // Without this, a parameter with no value makes the AppHost wait for someone to type one.
        parameters["ASPIRE_INTERACTIVITY_ENABLED"] = "false";

        // "--clear-cache true" stops Aspire from saving parameter values under the user profile and from preferring
        // values saved by an earlier run over the ones given here.
        return RunAsync("dotnet",
            ["run", "--project", Path.Combine(FindRepositoryRoot(), "samples", "Sample.AppHost"), "--",
                "--operation", "publish", "--step", "publish", "--output-path", outputPath, "--clear-cache", "true"],
            parameters);
    }

    public static async Task<(int ExitCode, string Output)> RunAsync(
        string fileName, string[] arguments, Dictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment ?? [])
        {
            startInfo.Environment[name] = value;
        }

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "Aspirin.slnx")))
        {
            directory = directory.Parent ?? throw new InvalidOperationException("Aspirin.slnx was not found above the test output folder.");
        }

        return directory.FullName;
    }
}
