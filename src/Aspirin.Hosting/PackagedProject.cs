using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;

namespace Aspirin.Hosting;

/// <summary>
/// What packaging a project as a Windows service and as an IIS site have in common.
/// </summary>
internal static class PackagedProject
{
    /// <summary>
    /// Puts the annotation on the project in place of any earlier one, and adds the package step to the pipeline the
    /// first time a project is packaged.
    /// </summary>
    public static IResourceBuilder<ProjectResource> Annotate(IResourceBuilder<ProjectResource> builder, PackagedProjectAnnotation annotation)
    {
        var packageStepExists = builder.ApplicationBuilder.Resources.Any(r => r.HasAnnotationOfType<PackagedProjectAnnotation>());
        foreach (var earlier in builder.Resource.Annotations.OfType<PackagedProjectAnnotation>().ToList())
        {
            builder.Resource.Annotations.Remove(earlier);
        }

        builder.WithAnnotation(annotation);

        if (!packageStepExists)
        {
            // Parameters are resolved before publish-prereq, and the package needs their values.
            builder.ApplicationBuilder.Pipeline.AddStep(AspirinPackage.StepName, AspirinPackage.CreateAsync,
                dependsOn: WellKnownPipelineSteps.PublishPrereq, requiredBy: WellKnownPipelineSteps.Publish);
        }

        return builder;
    }

    /// <summary>Returns the backup path the options give, or the default next to the install path.</summary>
    public static string BackupPathFor(string installPath, PackagedProjectOptions options) =>
        (options.BackupPath ?? installPath + ".backups").TrimEnd('\\');

    public static void ValidatePaths(string installPath, string backupPath, string exampleInstallPath)
    {
        if (!WindowsPath.IsAbsolute(installPath))
        {
            throw new ArgumentException(
                $"The install path '{installPath}' must be an absolute Windows path, such as {exampleInstallPath}.", nameof(installPath));
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
    }
}
