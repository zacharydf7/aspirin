namespace Aspirin.Hosting;

/// <summary>
/// Describes how a project is installed as a Windows service on the target machine.
/// </summary>
public sealed class WindowsServiceOptions : PackagedProjectOptions
{
    /// <summary>The service name. Defaults to the resource name.</summary>
    public string? ServiceName { get; set; }

    /// <summary>The name shown in the Services console. Defaults to <see cref="ServiceName"/>.</summary>
    public string? DisplayName { get; set; }

    /// <summary>The description shown in the Services console.</summary>
    public string? Description { get; set; }

    /// <summary>The executable to run. Defaults to the project file name with an <c>.exe</c> extension.</summary>
    public string? ExecutableName { get; set; }

    /// <summary>
    /// The account the service runs as, such as <c>NT AUTHORITY\NetworkService</c> or <c>CORP\svc-worker</c>. Defaults
    /// to LocalSystem. A password is never stored in the package: the installer asks for it or takes it from its
    /// <c>-Credential</c> parameter.
    /// </summary>
    public string? Account { get; set; }

    /// <summary>When Windows starts the service.</summary>
    public WindowsServiceStartMode StartMode { get; set; } = WindowsServiceStartMode.Automatic;
}

/// <summary>
/// When Windows starts a service.
/// </summary>
public enum WindowsServiceStartMode
{
    /// <summary>At boot.</summary>
    Automatic,

    /// <summary>Shortly after boot, once the automatic services have started.</summary>
    DelayedAutomatic,

    /// <summary>Only when something starts it. The installer leaves a manual service stopped.</summary>
    Manual,
}
