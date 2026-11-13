using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Handoff.Aspire.Hosting;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for packaging projects as Windows services.
/// </summary>
public static class WindowsServiceExtensions
{
    /// <summary>
    /// Packages the project as a Windows service when the application is published. Running the application locally
    /// is unaffected.
    /// </summary>
    /// <param name="builder">The project to package.</param>
    /// <param name="installPath">The folder on the target machine the service is installed into.</param>
    /// <param name="configure">Sets the service name, account and other options.</param>
    public static IResourceBuilder<ProjectResource> PublishAsWindowsService(
        this IResourceBuilder<ProjectResource> builder, string installPath, Action<WindowsServiceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(installPath);

        installPath = installPath.TrimEnd('\\');
        var options = new WindowsServiceOptions();
        configure?.Invoke(options);
        options.ServiceName ??= builder.Resource.Name;
        options.DisplayName ??= options.ServiceName;
        options.BackupPath = (options.BackupPath ?? installPath + ".backups").TrimEnd('\\');
        Validate(installPath, options.BackupPath, options.ServiceName);

        var packageStepExists = builder.ApplicationBuilder.Resources.Any(r => r.HasAnnotationOfType<WindowsServiceAnnotation>());
        builder.WithAnnotation(new WindowsServiceAnnotation(installPath, options), ResourceAnnotationMutationBehavior.Replace);

        if (!packageStepExists)
        {
            // Parameters are resolved before publish-prereq, and the package needs their values.
            builder.ApplicationBuilder.Pipeline.AddStep(HandoffPackage.StepName, HandoffPackage.CreateAsync,
                dependsOn: WellKnownPipelineSteps.PublishPrereq, requiredBy: WellKnownPipelineSteps.Publish);
        }

        return builder;
    }

    private static void Validate(string installPath, string backupPath, string serviceName)
    {
        if (!WindowsPath.IsAbsolute(installPath))
        {
            throw new ArgumentException(
                $"The install path '{installPath}' must be an absolute Windows path, such as C:\\Services\\Worker.", nameof(installPath));
        }

        if (!WindowsPath.IsAbsolute(backupPath))
        {
            throw new ArgumentException($"The backup path '{backupPath}' must be an absolute Windows path.", nameof(backupPath));
        }

        // The installer mirrors the install folder into the backup folder.
        if (WindowsPath.Overlap(installPath, backupPath))
        {
            throw new ArgumentException(
                $"The backup path '{backupPath}' and the install path '{installPath}' must be separate folders, with neither inside the other.",
                nameof(backupPath));
        }

        if (string.IsNullOrWhiteSpace(serviceName) || serviceName.Length > 256 || serviceName.AsSpan().ContainsAny('/', '\\'))
        {
            throw new ArgumentException($"The service name '{serviceName}' must be 1 to 256 characters with no slashes.", nameof(serviceName));
        }
    }
}
