using System.Diagnostics;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Handoff.Aspire.Hosting;

/// <summary>
/// The pipeline step that writes the installation package.
/// </summary>
internal static class HandoffPackage
{
    public const string StepName = "handoff-package";

    private static readonly string[] s_packageFiles = ["install.ps1", "rollback.ps1", "common.ps1", "README.txt"];

    public static async Task CreateAsync(PipelineStepContext context)
    {
        try
        {
            await CreatePackageAsync(context);
        }
        catch
        {
            // An AppHost started with "dotnet run" exits with 0 even when the pipeline fails, which a CI step would
            // read as success. Only the Aspire CLI turns a failed pipeline into a failing exit code.
            Environment.ExitCode = 1;
            throw;
        }
    }

    private static async Task CreatePackageAsync(PipelineStepContext context)
    {
        var cancellationToken = context.CancellationToken;
        var services = context.Model.Resources.OfType<ProjectResource>()
            .Where(resource => !resource.IsExcludedFromPublish())
            .SelectMany(resource => resource.Annotations.OfType<WindowsServiceAnnotation>().Select(annotation => (resource, annotation)))
            .ToList();
        EnsureServicesAreSeparate([.. services.Select(service => (service.resource.Name, service.annotation))]);

        // The package owns this folder, so it can be emptied without touching what other publishers wrote.
        var packageRoot = Path.Combine(context.Services.GetRequiredService<IPipelineOutputService>().GetOutputDirectory(), "handoff");
        if (Directory.Exists(packageRoot))
        {
            Directory.Delete(packageRoot, recursive: true);
        }

        Directory.CreateDirectory(packageRoot);

        // Resources are installed in the order they are declared. A resource can only refer to ones declared before
        // it, so whatever it depends on is installed first.
        var resources = new List<PackageResource>();
        foreach (var (resource, annotation) in services)
        {
            resources.Add(await PackageWindowsServiceAsync(context, resource, annotation, packageRoot));
        }

        var environment = context.Services.GetRequiredService<IHostEnvironment>();
        var manifest = new PackageManifest(PackageManifest.CurrentSchemaVersion, environment.ApplicationName, environment.EnvironmentName, resources);
        await File.WriteAllTextAsync(Path.Combine(packageRoot, "package.json"), manifest.ToJson(), cancellationToken);

        foreach (var fileName in s_packageFiles)
        {
            await using var source = typeof(HandoffPackage).Assembly.GetManifestResourceStream(fileName)!;
            await using var destination = File.Create(Path.Combine(packageRoot, fileName));
            await source.CopyToAsync(destination, cancellationToken);
        }

        await PackageChecksums.WriteAsync(packageRoot, cancellationToken);
        context.Summary.Add("Installation package", packageRoot);
    }

    /// <summary>
    /// Fails when two services share a name or a folder. The installer keeps each service's files and backups in
    /// that service's own folders, so shared ones would overwrite each other.
    /// </summary>
    internal static void EnsureServicesAreSeparate(IReadOnlyList<(string ResourceName, WindowsServiceAnnotation Service)> services)
    {
        for (var i = 0; i < services.Count; i++)
        {
            for (var j = i + 1; j < services.Count; j++)
            {
                var (first, second) = (services[i], services[j]);
                if (string.Equals(first.Service.Options.ServiceName, second.Service.Options.ServiceName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new DistributedApplicationException(
                        $"The resources '{first.ResourceName}' and '{second.ResourceName}' both use the service name '{first.Service.Options.ServiceName}'.");
                }

                foreach (var firstPath in new[] { first.Service.InstallPath, first.Service.Options.BackupPath! })
                {
                    foreach (var secondPath in new[] { second.Service.InstallPath, second.Service.Options.BackupPath! })
                    {
                        if (WindowsPath.Overlap(firstPath, secondPath))
                        {
                            throw new DistributedApplicationException(
                                $"The resources '{first.ResourceName}' and '{second.ResourceName}' use the folders '{firstPath}' and '{secondPath}'. " +
                                "Each service needs install and backup folders of its own, with none inside another.");
                        }
                    }
                }
            }
        }
    }

    private static async Task<PackageResource> PackageWindowsServiceAsync(
        PipelineStepContext context, ProjectResource resource, WindowsServiceAnnotation annotation, string packageRoot)
    {
        var options = annotation.Options;
        var projectPath = resource.GetProjectMetadata().ProjectPath;
        var executable = options.ExecutableName ?? Path.GetFileNameWithoutExtension(projectPath) + ".exe";
        var source = $"resources/{resource.Name}/app";
        var appDirectory = Path.Combine(packageRoot, "resources", resource.Name, "app");

        var task = await context.ReportingStep.CreateTaskAsync($"Publishing {resource.Name} for {options.RuntimeIdentifier}", context.CancellationToken);
        await using (task.ConfigureAwait(false))
        {
            await RunDotnetAsync(
                ["publish", projectPath, "--configuration", "Release", "--runtime", options.RuntimeIdentifier,
                    "--self-contained", options.SelfContained ? "true" : "false", "--output", appDirectory, "--nologo"],
                context.CancellationToken);

            if (!File.Exists(Path.Combine(appDirectory, executable)))
            {
                throw new DistributedApplicationException(
                    $"Publishing '{resource.Name}' did not produce '{executable}'. Set {nameof(WindowsServiceOptions.ExecutableName)} to the executable the project builds.");
            }

            await task.CompleteAsync($"Published {resource.Name}", CompletionState.Completed, context.CancellationToken);
        }

        var assemblyPath = Path.Combine(appDirectory, Path.ChangeExtension(executable, ".dll"));
        var version = File.Exists(assemblyPath) ? FileVersionInfo.GetVersionInfo(assemblyPath).ProductVersion : null;
        var settings = await PackageSettings.ResolveAsync(resource, context.ExecutionContext, context.Logger, context.CancellationToken);

        return new PackageResource(resource.Name, source, version,
            new WindowsServiceManifest(options.ServiceName!, options.DisplayName!, options.Description, executable, annotation.InstallPath,
                options.BackupPath!, options.StartMode, options.Account),
            settings);
    }

    private static async Task RunDotnetAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new DistributedApplicationException("The dotnet command could not be started.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new DistributedApplicationException(
                $"'dotnet {string.Join(' ', arguments)}' failed with exit code {process.ExitCode}.{Environment.NewLine}{await output}{await error}");
        }
    }
}
