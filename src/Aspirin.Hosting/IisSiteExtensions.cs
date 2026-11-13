using System.Net;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspirin.Hosting;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for packaging projects as IIS sites.
/// </summary>
public static partial class IisSiteExtensions
{
    /// <summary>
    /// Packages the project as an IIS site when the application is published. Running the application locally is
    /// unaffected. The project must be an ASP.NET Core project, and the server needs the ASP.NET Core Module, which the
    /// .NET Hosting Bundle installs.
    /// </summary>
    /// <param name="builder">The project to package.</param>
    /// <param name="installPath">The folder on the target machine the site's files are installed into.</param>
    /// <param name="configure">Sets the bindings, which are required, and the site name, account and other options.</param>
    public static IResourceBuilder<ProjectResource> PublishAsIisSite(
        this IResourceBuilder<ProjectResource> builder, string installPath, Action<IisSiteOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(installPath);
        ArgumentNullException.ThrowIfNull(configure);

        installPath = installPath.TrimEnd('\\');
        var options = new IisSiteOptions();
        configure(options);
        options.SiteName ??= builder.Resource.Name;
        options.AppPoolName ??= options.SiteName;
        options.BackupPath = PackagedProject.BackupPathFor(installPath, options);
        PackagedProject.ValidatePaths(installPath, options.BackupPath, @"C:\inetpub\Contoso\Web");
        Validate(installPath, options);

        return PackagedProject.Annotate(builder, new IisSiteAnnotation(installPath, options));
    }

    private static void Validate(string installPath, IisSiteOptions options)
    {
        // The installer passes these to appcmd.exe inside single quotes.
        foreach (var path in new[] { installPath, options.BackupPath! })
        {
            if (path.AsSpan().ContainsAny('\'', '"'))
            {
                throw new ArgumentException($"The path '{path}' can't contain quotes.", nameof(installPath));
            }
        }

        foreach (var (kind, name) in new[] { ("site", options.SiteName!), ("application pool", options.AppPoolName!) })
        {
            if (!NamePattern().IsMatch(name))
            {
                throw new ArgumentException(
                    $"The {kind} name '{name}' must be 1 to 64 letters, digits, spaces, dots, hyphens or underscores.", nameof(options));
            }
        }

        if (options.Bindings.Count == 0)
        {
            throw new ArgumentException(
                $"The site '{options.SiteName}' needs at least one binding, such as {nameof(IisBinding)}.{nameof(IisBinding.Http)}(80).", nameof(options));
        }

        foreach (var binding in options.Bindings)
        {
            ValidateBinding(options.SiteName!, binding);
        }

        var duplicate = options.Bindings.GroupBy(BindingKey).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"The site '{options.SiteName}' has the binding {Describe(duplicate.First())} more than once.", nameof(options));
        }

        if (options.HealthCheckPath is { } healthCheckPath && !healthCheckPath.StartsWith('/'))
        {
            throw new ArgumentException($"The health check path '{healthCheckPath}' must start with a slash, such as /health.", nameof(options));
        }
    }

    private static void ValidateBinding(string siteName, IisBinding binding)
    {
        var problem = binding switch
        {
            { Port: < 1 or > 65535 } => "has a port outside 1 to 65535",
            { HostName: { } hostName } when !HostNamePattern().IsMatch(hostName) => "has a host name that isn't a valid DNS name",
            { IpAddress: not "*" } when !IsIpAddress(binding.IpAddress) => "has an IP address that is neither * nor a valid address",
            { Protocol: IisBindingProtocol.Https, CertificateSubject: null, CertificateThumbprint: null } =>
                "needs the certificate's subject or thumbprint",
            { Protocol: IisBindingProtocol.Https, CertificateSubject: not null, CertificateThumbprint: not null } =>
                "gives both a certificate subject and a thumbprint; give one",
            { CertificateThumbprint: { } thumbprint } when !ThumbprintPattern().IsMatch(thumbprint) =>
                "has a certificate thumbprint that isn't 40 hexadecimal characters",
            { CertificateSubject: { } subject } when string.IsNullOrWhiteSpace(subject) || subject.AsSpan().ContainsAny('\'', '"') =>
                "has a certificate subject that is empty or contains quotes",
            { Protocol: IisBindingProtocol.Https } when binding.CertificateStore is not ("My" or "WebHosting") =>
                "names a certificate store other than My or WebHosting",
            _ => null,
        };

        if (problem is not null)
        {
            throw new ArgumentException($"The binding {Describe(binding)} of the site '{siteName}' {problem}.", "options");
        }
    }

    // IPAddress.TryParse also accepts shorthand such as "10.0.0", which it reads as 10.0.0.0, so IPv4 needs four parts.
    private static bool IsIpAddress(string value) =>
        IPAddress.TryParse(value, out var address) &&
        (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 || value.Split('.').Length == 4);

    /// <summary>What makes two bindings collide: the same address, port and host name, whatever the protocol.</summary>
    internal static (string IpAddress, int Port, string HostName) BindingKey(IisBinding binding) =>
        (binding.IpAddress, binding.Port, (binding.HostName ?? "").ToLowerInvariant());

    internal static string Describe(IisBinding binding) =>
        $"{binding.Protocol.ToString().ToLowerInvariant()}://{binding.HostName ?? "*"}:{binding.Port} on {binding.IpAddress}";

    [GeneratedRegex(@"^[A-Za-z0-9 ._-]{1,64}$")]
    private static partial Regex NamePattern();

    // A DNS name, optionally with a leading wildcard label, which IIS 10 accepts as a host name.
    [GeneratedRegex(@"^(\*\.)?[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)*$")]
    private static partial Regex HostNamePattern();

    [GeneratedRegex("^[0-9A-Fa-f]{40}$")]
    private static partial Regex ThumbprintPattern();
}
