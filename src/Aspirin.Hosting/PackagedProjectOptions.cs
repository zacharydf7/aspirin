namespace Aspirin.Hosting;

/// <summary>
/// Options every packaged project has, whether it is installed as a Windows service or as an IIS site.
/// </summary>
public abstract class PackagedProjectOptions
{
    /// <summary>
    /// The folder on the target machine that holds backups of earlier installs. Defaults to the install path with
    /// <c>.backups</c> appended.
    /// </summary>
    public string? BackupPath { get; set; }

    /// <summary>
    /// Whether the package carries the values of secret settings, such as connection strings, so the project can be
    /// installed without supplying them. Off by default. When it is on, anyone who can read the package can read the
    /// secrets, and publishing fails if a secret has no value.
    /// </summary>
    public bool IncludeSecretValues { get; set; }

    /// <summary>The runtime identifier the project is published for.</summary>
    public string RuntimeIdentifier { get; set; } = "win-x64";

    /// <summary>Whether the package carries the .NET runtime, so the target machine doesn't need it installed.</summary>
    public bool SelfContained { get; set; } = true;
}
