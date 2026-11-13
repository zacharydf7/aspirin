using System.Text.Json;
using System.Text.Json.Serialization;

namespace Handoff.Aspire.Hosting;

/// <summary>
/// The contents of <c>package.json</c>, which the install and rollback scripts read. Every property is always
/// written, nulls included, so the scripts can rely on a fixed shape.
/// </summary>
internal sealed record PackageManifest(int SchemaVersion, string Application, string Environment, IReadOnlyList<PackageResource> Resources)
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string ToJson() => JsonSerializer.Serialize(this, s_jsonOptions);
}

/// <param name="Source">The folder that holds the published files, relative to the package root.</param>
/// <param name="Version">The product version of the published assembly, when it has one.</param>
internal sealed record PackageResource(
    string Name, string Source, string? Version, WindowsServiceManifest WindowsService, IReadOnlyList<PackageSetting> Settings);

internal sealed record WindowsServiceManifest(
    string ServiceName, string DisplayName, string? Description, string Executable, string InstallPath, string BackupPath,
    WindowsServiceStartMode StartMode, string? Account);

/// <summary>
/// An environment variable the service runs with. A setting with no <see cref="Value"/> must be supplied when the
/// package is installed, either because it is a secret the package doesn't carry or because it refers to something
/// the package can't know, such as another service's address.
/// </summary>
/// <param name="Expression">For a setting that couldn't be resolved, the Aspire expression it came from.</param>
internal sealed record PackageSetting(string Name, string? Value = null, bool Secret = false, string? Expression = null);
