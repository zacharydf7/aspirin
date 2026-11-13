namespace Aspirin.Hosting;

/// <summary>
/// An address an IIS site answers on. Create one with <see cref="Http"/> or <see cref="Https"/>, and change the IP
/// address or certificate store with a <c>with</c> expression.
/// </summary>
public sealed record IisBinding
{
    private IisBinding(IisBindingProtocol protocol, int port, string? hostName)
    {
        Protocol = protocol;
        Port = port;
        HostName = hostName;
    }

    /// <summary>HTTP or HTTPS.</summary>
    public IisBindingProtocol Protocol { get; }

    /// <summary>The TCP port.</summary>
    public int Port { get; }

    /// <summary>The host name the site answers to, or <see langword="null"/> for any host name.</summary>
    public string? HostName { get; }

    /// <summary>The IP address the site listens on. Defaults to <c>*</c>, every address the server has.</summary>
    public string IpAddress { get; init; } = "*";

    /// <summary>
    /// For HTTPS, the subject of the certificate, as Windows shows it, such as <c>CN=web.contoso.local</c>. The
    /// installer picks the newest certificate with that subject that hasn't expired and has a private key, so a renewed
    /// certificate is used without building a new package.
    /// </summary>
    public string? CertificateSubject { get; private init; }

    /// <summary>For HTTPS, the thumbprint of the one certificate to use.</summary>
    public string? CertificateThumbprint { get; private init; }

    /// <summary>
    /// For HTTPS, the local machine certificate store that holds the certificate: <c>My</c> (Personal, the default)
    /// or <c>WebHosting</c>. The certificate must already be installed there; the package never carries one.
    /// </summary>
    public string CertificateStore { get; init; } = "My";

    /// <summary>An HTTP binding.</summary>
    /// <param name="port">The TCP port.</param>
    /// <param name="hostName">The host name the site answers to, or <see langword="null"/> for any host name.</param>
    public static IisBinding Http(int port, string? hostName = null) => new(IisBindingProtocol.Http, port, hostName);

    /// <summary>
    /// An HTTPS binding. Give the certificate's subject or its thumbprint. With a host name, the binding uses Server
    /// Name Indication, so several sites can share a port with a certificate each.
    /// </summary>
    /// <param name="port">The TCP port.</param>
    /// <param name="hostName">The host name the site answers to, or <see langword="null"/> for any host name.</param>
    /// <param name="certificateSubject">The subject of the certificate, such as <c>CN=web.contoso.local</c>.</param>
    /// <param name="certificateThumbprint">The thumbprint of the certificate.</param>
    public static IisBinding Https(int port, string? hostName = null, string? certificateSubject = null, string? certificateThumbprint = null) =>
        new(IisBindingProtocol.Https, port, hostName) { CertificateSubject = certificateSubject, CertificateThumbprint = certificateThumbprint };
}

/// <summary>
/// The protocol of an IIS binding.
/// </summary>
public enum IisBindingProtocol
{
    /// <summary>Plain HTTP.</summary>
    Http,

    /// <summary>HTTP over TLS, with a certificate from the server's certificate store.</summary>
    Https,
}
