using System.Security.Cryptography;
using System.Text;

namespace Handoff.Aspire.Hosting;

internal static class PackageChecksums
{
    public const string FileName = "checksums.sha256";

    /// <summary>
    /// Writes the SHA-256 hash of every other file in the package, in the format <c>sha256sum</c> reads.
    /// </summary>
    public static async Task WriteAsync(string packageRoot, CancellationToken cancellationToken)
    {
        var lines = new StringBuilder();
        var relativePaths = Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(packageRoot, file).Replace('\\', '/'))
            .Where(path => path != FileName)
            .Order(StringComparer.Ordinal);

        foreach (var relativePath in relativePaths)
        {
            await using var stream = File.OpenRead(Path.Combine(packageRoot, relativePath));
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            lines.Append(Convert.ToHexStringLower(hash)).Append("  ").Append(relativePath).Append('\n');
        }

        await File.WriteAllTextAsync(Path.Combine(packageRoot, FileName), lines.ToString(), cancellationToken);
    }
}
