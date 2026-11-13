using Aspire.Hosting.ApplicationModel;

namespace Aspirin.Hosting;

/// <summary>
/// Marks a project to be packaged when the application is published. A project has at most one.
/// </summary>
internal abstract class PackagedProjectAnnotation(string installPath) : IResourceAnnotation
{
    public string InstallPath { get; } = installPath;

    public abstract PackagedProjectOptions Options { get; }
}

/// <summary>
/// Marks a project to be packaged as a Windows service when the application is published.
/// </summary>
internal sealed class WindowsServiceAnnotation(string installPath, WindowsServiceOptions options) : PackagedProjectAnnotation(installPath)
{
    public override WindowsServiceOptions Options { get; } = options;
}

/// <summary>
/// Marks a project to be packaged as an IIS site when the application is published.
/// </summary>
internal sealed class IisSiteAnnotation(string installPath, IisSiteOptions options) : PackagedProjectAnnotation(installPath)
{
    public override IisSiteOptions Options { get; } = options;
}
