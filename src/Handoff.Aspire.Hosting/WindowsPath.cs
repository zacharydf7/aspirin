namespace Handoff.Aspire.Hosting;

/// <summary>
/// Checks Windows paths as text. The package can be built on Linux, where System.IO.Path follows other rules.
/// </summary>
internal static class WindowsPath
{
    public static bool IsAbsolute(string path) =>
        path.Length > 3 &&
        ((char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\') || path.StartsWith(@"\\", StringComparison.Ordinal));

    /// <summary>Whether the folders are the same or one is inside the other.</summary>
    public static bool Overlap(string first, string second)
    {
        first = first.TrimEnd('\\') + '\\';
        second = second.TrimEnd('\\') + '\\';
        return first.StartsWith(second, StringComparison.OrdinalIgnoreCase) || second.StartsWith(first, StringComparison.OrdinalIgnoreCase);
    }
}
