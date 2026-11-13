using Aspire.Hosting.ApplicationModel;
using Aspirin.Hosting;

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
        options.BackupPath = PackagedProject.BackupPathFor(installPath, options);
        PackagedProject.ValidatePaths(installPath, options.BackupPath, @"C:\Services\Worker");

        if (string.IsNullOrWhiteSpace(options.ServiceName) || options.ServiceName.Length > 256 || options.ServiceName.AsSpan().ContainsAny('/', '\\'))
        {
            throw new ArgumentException($"The service name '{options.ServiceName}' must be 1 to 256 characters with no slashes.", nameof(configure));
        }

        return PackagedProject.Annotate(builder, new WindowsServiceAnnotation(installPath, options));
    }
}
