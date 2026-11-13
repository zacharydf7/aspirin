namespace Aspirin.Hosting;

/// <summary>
/// Describes how a project is installed as an IIS site on the target machine. The installer owns the site and its
/// application pool: on every install it makes them match these options.
/// </summary>
public sealed class IisSiteOptions : PackagedProjectOptions
{
    /// <summary>The site name shown in IIS Manager. Defaults to the resource name.</summary>
    public string? SiteName { get; set; }

    /// <summary>
    /// The application pool the site runs in. Defaults to <see cref="SiteName"/>. The installer creates it, and no
    /// other site in the package may use it.
    /// </summary>
    public string? AppPoolName { get; set; }

    /// <summary>
    /// The addresses the site answers on. At least one is required. On every install the site's bindings are made to
    /// match this list, so a binding added by hand in IIS Manager is removed.
    /// </summary>
    public IList<IisBinding> Bindings { get; } = [];

    /// <summary>
    /// The account the application pool runs as, such as <c>NT AUTHORITY\NetworkService</c> or <c>CORP\svc-web</c>.
    /// Defaults to the pool's own identity, <c>IIS AppPool\&lt;pool name&gt;</c>. A password is never stored in the
    /// package: the installer asks for it or takes it from its <c>-Credential</c> parameter.
    /// </summary>
    public string? Account { get; set; }

    /// <summary>
    /// Whether the site starts with IIS and keeps running without waiting for a request, for a site that does
    /// background work. The server needs the IIS Application Initialization feature.
    /// </summary>
    public bool AlwaysRunning { get; set; }

    /// <summary>
    /// A path the installer requests after starting the site, such as <c>/health</c>. The install fails unless it
    /// answers with a success status within 30 seconds. Leave it unset to only check that the site is running.
    /// </summary>
    public string? HealthCheckPath { get; set; }
}
