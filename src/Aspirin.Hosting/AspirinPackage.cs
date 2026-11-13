using System.Diagnostics;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aspirin.Hosting;

/// <summary>
/// The pipeline step that writes the installation package.
/// </summary>
internal static class AspirinPackage
{
    public const string StepName = "aspirin-package";

    private static readonly string[] s_packageFiles = ["install.ps1", "rollback.ps1", "common.ps1", "iis.ps1", "README.txt"];

    // IIS decides where a site listens, from its bindings, so the settings Aspire derives from the project's endpoints
    // don't apply. Left in, they would have no value, and the installer would ask for them.
    private static readonly HashSet<string> s_addressSettings = new(StringComparer.OrdinalIgnoreCase)
    {
        "ASPNETCORE_URLS", "HTTP_PORTS", "HTTPS_PORTS", "ASPNETCORE_HTTP_PORTS", "ASPNETCORE_HTTPS_PORTS",
    };

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
        var projects = context.Model.Resources.OfType<ProjectResource>()
            .Where(resource => !resource.IsExcludedFromPublish())
            .SelectMany(resource => resource.Annotations.OfType<PackagedProjectAnnotation>().Select(annotation => (resource, annotation)))
            .ToList();
        EnsureResourcesAreSeparate([.. projects.Select(project => (project.resource.Name, project.annotation))]);

        // The package owns this folder, so it can be emptied without touching what other publishers wrote.
        var packageRoot = Path.Combine(context.Services.GetRequiredService<IPipelineOutputService>().GetOutputDirectory(), "aspirin");
        if (Directory.Exists(packageRoot))
        {
            Directory.Delete(packageRoot, recursive: true);
        }

        Directory.CreateDirectory(packageRoot);

        // Resources are installed in the order they are declared. A resource can only refer to ones declared before
        // it, so whatever it depends on is installed first.
        var resources = new List<PackageResource>();
        foreach (var (resource, annotation) in projects)
        {
            resources.Add(await PackageProjectAsync(context, resource, annotation, packageRoot));
        }

        var environment = context.Services.GetRequiredService<IHostEnvironment>();
        var manifest = new PackageManifest(PackageManifest.CurrentSchemaVersion, environment.ApplicationName, environment.EnvironmentName, resources);
        await File.WriteAllTextAsync(Path.Combine(packageRoot, "package.json"), manifest.ToJson(), cancellationToken);

        foreach (var fileName in s_packageFiles)
        {
            await using var source = typeof(AspirinPackage).Assembly.GetManifestResourceStream(fileName)!;
            await using var destination = File.Create(Path.Combine(packageRoot, fileName));
            await source.CopyToAsync(destination, cancellationToken);
        }

        await PackageChecksums.WriteAsync(packageRoot, cancellationToken);
        context.Summary.Add("Installation package", packageRoot);
    }

    /// <summary>
    /// Fails when two resources share a name, a folder or a site binding. The installer keeps each resource's files
    /// and backups in that resource's own folders, so shared ones would overwrite each other.
    /// </summary>
    internal static void EnsureResourcesAreSeparate(IReadOnlyList<(string ResourceName, PackagedProjectAnnotation Annotation)> resources)
    {
        for (var i = 0; i < resources.Count; i++)
        {
            for (var j = i + 1; j < resources.Count; j++)
            {
                var (first, second) = (resources[i], resources[j]);
                foreach (var (kind, firstName, secondName) in SharedNames(first.Annotation, second.Annotation))
                {
                    if (string.Equals(firstName, secondName, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new DistributedApplicationException(
                            $"The resources '{first.ResourceName}' and '{second.ResourceName}' both use the {kind} '{firstName}'.");
                    }
                }

                foreach (var firstPath in new[] { first.Annotation.InstallPath, first.Annotation.Options.BackupPath! })
                {
                    foreach (var secondPath in new[] { second.Annotation.InstallPath, second.Annotation.Options.BackupPath! })
                    {
                        if (WindowsPath.Overlap(firstPath, secondPath))
                        {
                            throw new DistributedApplicationException(
                                $"The resources '{first.ResourceName}' and '{second.ResourceName}' use the folders '{firstPath}' and '{secondPath}'. " +
                                "Each resource needs install and backup folders of its own, with none inside another.");
                        }
                    }
                }

                if (first.Annotation is IisSiteAnnotation firstSite && second.Annotation is IisSiteAnnotation secondSite &&
                    firstSite.Options.Bindings.FirstOrDefault(binding => secondSite.Options.Bindings.Any(other => Collide(binding, other))) is { } shared)
                {
                    throw new DistributedApplicationException(
                        $"The resources '{first.ResourceName}' and '{second.ResourceName}' both answer on {IisSiteExtensions.Describe(shared)}.");
                }
            }
        }
    }

    // The names that must differ between two resources of the same kind.
    private static IEnumerable<(string Kind, string FirstName, string SecondName)> SharedNames(PackagedProjectAnnotation first, PackagedProjectAnnotation second)
    {
        switch (first, second)
        {
            case (WindowsServiceAnnotation firstService, WindowsServiceAnnotation secondService):
                yield return ("service name", firstService.Options.ServiceName!, secondService.Options.ServiceName!);
                break;
            case (IisSiteAnnotation firstSite, IisSiteAnnotation secondSite):
                yield return ("site name", firstSite.Options.SiteName!, secondSite.Options.SiteName!);
                yield return ("application pool", firstSite.Options.AppPoolName!, secondSite.Options.AppPoolName!);
                break;
        }
    }

    // A binding on every address (*) also takes the port on each single address.
    private static bool Collide(IisBinding first, IisBinding second)
    {
        var (firstAddress, firstPort, firstHost) = IisSiteExtensions.BindingKey(first);
        var (secondAddress, secondPort, secondHost) = IisSiteExtensions.BindingKey(second);
        return firstPort == secondPort && firstHost == secondHost && (firstAddress == secondAddress || firstAddress == "*" || secondAddress == "*");
    }

    private static async Task<PackageResource> PackageProjectAsync(
        PipelineStepContext context, ProjectResource resource, PackagedProjectAnnotation annotation, string packageRoot)
    {
        var options = annotation.Options;
        var projectPath = resource.GetProjectMetadata().ProjectPath;
        var source = $"resources/{resource.Name}/app";
        var appDirectory = Path.Combine(packageRoot, "resources", resource.Name, "app");

        var task = await context.ReportingStep.CreateTaskAsync($"Publishing {resource.Name} for {options.RuntimeIdentifier}", context.CancellationToken);
        await using (task.ConfigureAwait(false))
        {
            await RunDotnetAsync(
                ["publish", projectPath, "--configuration", "Release", "--runtime", options.RuntimeIdentifier,
                    "--self-contained", options.SelfContained ? "true" : "false", "--output", appDirectory, "--nologo"],
                context.CancellationToken);
            await task.CompleteAsync($"Published {resource.Name}", CompletionState.Completed, context.CancellationToken);
        }

        var settings = await PackageSettings.ResolveAsync(
            resource, context.ExecutionContext, context.Logger, options.IncludeSecretValues, context.CancellationToken);
        var assemblyName = Path.GetFileNameWithoutExtension(projectPath);

        switch (annotation)
        {
            case WindowsServiceAnnotation { Options: var service }:
                var executable = service.ExecutableName ?? assemblyName + ".exe";
                if (!File.Exists(Path.Combine(appDirectory, executable)))
                {
                    throw new DistributedApplicationException(
                        $"Publishing '{resource.Name}' did not produce '{executable}'. Set {nameof(WindowsServiceOptions.ExecutableName)} to the executable the project builds.");
                }

                return new PackageResource(resource.Name, PackageResourceKind.WindowsService, source,
                    VersionOf(Path.Combine(appDirectory, Path.ChangeExtension(executable, ".dll"))), annotation.InstallPath, service.BackupPath!,
                    new WindowsServiceManifest(service.ServiceName!, service.DisplayName!, service.Description, executable, service.StartMode, service.Account),
                    IisSite: null, settings);

            case IisSiteAnnotation { Options: var site }:
                // The ASP.NET Core Module reads web.config to find the app, and only web projects publish one.
                if (!File.Exists(Path.Combine(appDirectory, "web.config")))
                {
                    throw new DistributedApplicationException(
                        $"Publishing '{resource.Name}' did not produce a web.config, so IIS couldn't run it. Only an ASP.NET Core project can be packaged as an IIS site.");
                }

                var bindings = site.Bindings.Select(binding => new IisBindingManifest(binding.Protocol, binding.IpAddress, binding.Port, binding.HostName,
                    binding.CertificateSubject, binding.CertificateThumbprint?.ToUpperInvariant(),
                    binding.Protocol == IisBindingProtocol.Https ? binding.CertificateStore : null,
                    Sni: binding.Protocol == IisBindingProtocol.Https && binding.HostName is not null)).ToList();
                return new PackageResource(resource.Name, PackageResourceKind.IisSite, source,
                    VersionOf(Path.Combine(appDirectory, assemblyName + ".dll")), annotation.InstallPath, site.BackupPath!,
                    WindowsService: null,
                    new IisSiteManifest(site.SiteName!, site.AppPoolName!, site.Account, site.AlwaysRunning, site.HealthCheckPath, bindings),
                    [.. settings.Where(setting => !s_addressSettings.Contains(setting.Name))]);

            default:
                throw new InvalidOperationException($"Unknown annotation {annotation.GetType().Name}.");
        }
    }

    private static string? VersionOf(string assemblyPath) =>
        File.Exists(assemblyPath) ? FileVersionInfo.GetVersionInfo(assemblyPath).ProductVersion : null;

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
