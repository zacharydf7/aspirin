using System.Runtime.Versioning;
using System.Security.Principal;

namespace Aspirin.Hosting.Tests;

/// <summary>
/// Runs a package's scripts in Windows PowerShell, the way an operator would.
/// </summary>
internal static class PackageScripts
{
    public static Task<(int ExitCode, string Output)> RunScriptAsync(string packageRoot, string script, params string[] arguments) =>
        PublishedSample.RunAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(packageRoot, script), .. arguments]);

    // Runs a command with the functions the scripts share, without running either script.
    public static Task<(int ExitCode, string Output)> RunFunctionsAsync(string packageRoot, string command) =>
        PublishedSample.RunAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
                $". '{Path.Combine(packageRoot, "common.ps1")}'; . '{Path.Combine(packageRoot, "iis.ps1")}'; {command}"]);

    [SupportedOSPlatformGuard("windows")]
    public static bool IsElevated() =>
        OperatingSystem.IsWindows() && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
}
