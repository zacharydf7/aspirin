using Aspire.Hosting.ApplicationModel;

namespace Aspirin.Hosting;

/// <summary>
/// Marks a project to be packaged as a Windows service when the application is published.
/// </summary>
internal sealed class WindowsServiceAnnotation(string installPath, WindowsServiceOptions options) : IResourceAnnotation
{
    public string InstallPath { get; } = installPath;

    public WindowsServiceOptions Options { get; } = options;
}
