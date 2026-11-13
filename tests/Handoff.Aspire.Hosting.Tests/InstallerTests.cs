using System.Runtime.Versioning;
using System.Security.Principal;

namespace Handoff.Aspire.Hosting.Tests;

/// <summary>
/// Runs the package's scripts in Windows PowerShell. The tests that install the service need an elevated session
/// and are skipped without one.
/// </summary>
public sealed class InstallerTests : IAsyncDisposable
{
    private const string ServiceName = "HandoffSampleWorker";
    private const string InstallPath = @"C:\Program Files\HandoffSampleWorker";
    private const string BackupPath = InstallPath + ".backups";

    private readonly PublishedPackage _package;
    private readonly string _workFolder = Directory.CreateTempSubdirectory("handoff-installer-").FullName;
    private readonly string _settingsFile;

    public InstallerTests(PublishedPackage package)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The installer runs on Windows only.");
        _package = package;
        _settingsFile = Path.Combine(_workFolder, "settings.json");
        File.WriteAllText(_settingsFile, """{ "worker": { "ConnectionStrings__sampledb": "Server=db;Password=from-the-settings-file" } }""");
    }

    public async ValueTask DisposeAsync()
    {
        // New-ProtectedDirectory can take away this process's access to the folders it creates; resetting the
        // permissions gives it back so the folder can be deleted.
        await PublishedPackage.RunAsync("icacls.exe", [_workFolder, "/reset", "/T"]);
        Directory.Delete(_workFolder, recursive: true);
    }

    [Fact]
    public async Task Install_ShouldListThePlanWithoutSecretValues_WhenGivenWhatIf()
    {
        var result = await RunScriptAsync(_package.Root, "install.ps1", "-WhatIf", "-SettingsFile", _settingsFile);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains($"Would create the Windows service '{ServiceName}' (version 1.2.3)", result.Output);
        Assert.Contains("ConnectionStrings__sampledb, ", result.Output);
        Assert.DoesNotContain("from-the-settings-file", result.Output);
    }

    [Fact]
    public async Task Install_ShouldFailBeforeChangingAnything_WhenSecretSettingHasNoValue()
    {
        var result = await RunScriptAsync(_package.Root, "install.ps1", "-WhatIf");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("worker: ConnectionStrings__sampledb", result.Output);
        Assert.Contains("Nothing was changed.", result.Output);
    }

    [Fact]
    public async Task Install_ShouldFailBeforeChangingAnything_WhenSettingsFileNamesUnknownResource()
    {
        File.WriteAllText(_settingsFile, """{ "wroker": { "ConnectionStrings__sampledb": "x" } }""");

        var result = await RunScriptAsync(_package.Root, "install.ps1", "-WhatIf", "-SettingsFile", _settingsFile);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("names the resource 'wroker'", result.Output);
    }

    [Theory]
    [InlineData("changed", "changed since the package was built: resources/worker/app/appsettings.json")]
    [InlineData("added", "not part of the package: resources/worker/app/extra.dll")]
    [InlineData("removed", "missing: resources/worker/app/appsettings.json")]
    public async Task Install_ShouldFailBeforeChangingAnything_WhenPackageFileWasTamperedWith(string tampering, string expectedProblem)
    {
        var copy = Path.Combine(_workFolder, "package");
        CopyDirectory(_package.Root, copy);
        var appFolder = Path.Combine(copy, "resources", "worker", "app");
        switch (tampering)
        {
            case "changed":
                File.AppendAllText(Path.Combine(appFolder, "appsettings.json"), " ");
                break;
            case "added":
                File.WriteAllText(Path.Combine(appFolder, "extra.dll"), "");
                break;
            case "removed":
                File.Delete(Path.Combine(appFolder, "appsettings.json"));
                break;
        }

        var result = await RunScriptAsync(copy, "install.ps1", "-WhatIf", "-SettingsFile", _settingsFile);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(expectedProblem, result.Output);
    }

    [Fact]
    public async Task GetLastRunId_ShouldReturnTheNewestCompleteRun_WhenServicesWereBackedUpByDifferentRuns()
    {
        var firstBackups = Path.Combine(_workFolder, "first");
        var secondBackups = Path.Combine(_workFolder, "second");
        foreach (var completeBackup in new[] { (firstBackups, "20260101-000000-000"), (firstBackups, "20260102-000000-000"), (secondBackups, "20260101-000000-000") })
        {
            Directory.CreateDirectory(Path.Combine(completeBackup.Item1, completeBackup.Item2));
            File.WriteAllText(Path.Combine(completeBackup.Item1, completeBackup.Item2, "backup.json"), "{}");
        }

        // A backup with no backup.json was interrupted, so the run that started it doesn't count.
        Directory.CreateDirectory(Path.Combine(secondBackups, "20260103-000000-000"));
        var manifest = Path.Combine(_workFolder, "manifest.json");
        File.WriteAllText(manifest, $$"""
            { "resources": [
                { "windowsService": { "backupPath": "{{firstBackups.Replace(@"\", @"\\")}}" } },
                { "windowsService": { "backupPath": "{{secondBackups.Replace(@"\", @"\\")}}" } } ] }
            """);

        var result = await RunFunctionsAsync($"Get-LastRunId (Get-Content -LiteralPath '{manifest}' -Raw | ConvertFrom-Json)");

        Assert.Equal("20260102-000000-000", result.Output.Trim());
    }

    [Fact]
    public async Task NewProtectedDirectory_ShouldNotInheritWriteAccess_WhenParentFolderGrantsItToUsers()
    {
        var parent = Path.Combine(_workFolder, "parent");
        var folder = Path.Combine(parent, "service");
        Directory.CreateDirectory(parent);
        await PublishedPackage.RunAsync("icacls.exe", [parent, "/grant", "*S-1-5-32-545:(OI)(CI)M"]);

        var result = await RunFunctionsAsync(
            $"(Get-DirectoryWriters '{parent}') -join '|'; New-ProtectedDirectory '{folder}'; (Get-DirectoryWriters '{folder}') -join '|'");

        // Only the owner is left: an elevated session creates folders owned by Administrators, any other by the user.
        var owner = IsElevated() ? "" : $@"{Environment.UserDomainName}\{Environment.UserName}";
        var writers = result.Output.Split(Environment.NewLine);
        Assert.Contains(@"BUILTIN\Users", writers[0]);
        Assert.Equal(owner, writers[1], ignoreCase: true);
    }

    [Fact]
    public async Task GetUnprotectedFolders_ShouldNameTheParentFolder_WhenUsersCanChangeIt()
    {
        var parent = Path.Combine(_workFolder, "parent");
        Directory.CreateDirectory(parent);
        await PublishedPackage.RunAsync("icacls.exe", [parent, "/grant", "*S-1-5-32-545:(OI)(CI)M"]);

        var result = await RunFunctionsAsync($"Get-UnprotectedFolders '{Path.Combine(parent, "not-created-yet")}'");

        Assert.Contains(result.Output.Split(Environment.NewLine), line => line.StartsWith($"{parent} (") && line.Contains(@"BUILTIN\Users"));
    }

    [Fact]
    public async Task GetUnprotectedFolders_ShouldNotBlameUsers_WhenTheyCanOnlyAddEntriesToTheParentFolder()
    {
        // These are the rights every user has on C:\ProgramData. They don't let anyone replace a folder that exists.
        var parent = Path.Combine(_workFolder, "parent");
        Directory.CreateDirectory(parent);
        await PublishedPackage.RunAsync("icacls.exe", [parent, "/grant", "*S-1-5-32-545:(CI)(WD,AD,WEA,WA)"]);

        var asParent = await RunFunctionsAsync($"Get-UnprotectedFolders '{Path.Combine(parent, "not-created-yet")}'");
        var asTarget = await RunFunctionsAsync($"Get-UnprotectedFolders '{parent}'");

        Assert.DoesNotContain(asParent.Output.Split(Environment.NewLine), line => line.StartsWith($"{parent} (") && line.Contains(@"BUILTIN\Users"));
        Assert.Contains(asTarget.Output.Split(Environment.NewLine), line => line.StartsWith($"{parent} (") && line.Contains(@"BUILTIN\Users"));
    }

    [Fact]
    public async Task GrantDirectoryRead_ShouldAddReadAccessAndKeepTheOtherPermissions_WhenFolderAlreadyExists()
    {
        var folder = Path.Combine(_workFolder, "service");
        var account = $@"{Environment.UserDomainName}\{Environment.UserName}";

        await RunFunctionsAsync($"New-ProtectedDirectory '{folder}'; Grant-DirectoryRead '{folder}' '{account}'");

        var permissions = (await PublishedPackage.RunAsync("icacls.exe", [folder])).Output;
        Assert.Contains($"{account}:(OI)(CI)(RX)", permissions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"NT AUTHORITY\SYSTEM:(OI)(CI)(F)", permissions);
    }

    [Fact]
    public async Task Install_ShouldUpdateTheSameService_WhenRunTwice()
    {
        Assert.SkipUnless(IsElevated(), "Installing a Windows service needs an elevated session.");
        try
        {
            var first = await RunScriptAsync(_package.Root, "install.ps1", "-SettingsFile", _settingsFile);
            Assert.True(first.ExitCode == 0, first.Output);
            var second = await RunScriptAsync(_package.Root, "install.ps1", "-SettingsFile", _settingsFile);
            Assert.True(second.ExitCode == 0, second.Output);

            Assert.Contains("Creating the service", first.Output);
            Assert.Contains("Updating the service", second.Output);
            Assert.Contains("RUNNING", (await PublishedPackage.RunAsync("sc.exe", ["query", ServiceName])).Output);
            Assert.True(File.Exists(Path.Combine(InstallPath, "Sample.Worker.exe")));
            Assert.Equal(2, Directory.GetDirectories(BackupPath).Length);

            var environment = await PublishedPackage.RunAsync("reg.exe",
                ["query", $@"HKLM\SYSTEM\CurrentControlSet\Services\{ServiceName}", "/v", "Environment"]);
            Assert.Contains($"Sample__Greeting={PublishedPackage.Greeting}", environment.Output);
            Assert.Contains("ConnectionStrings__sampledb=Server=db;Password=from-the-settings-file", environment.Output);

            var writers = await RunFunctionsAsync($"@(Get-DirectoryWriters '{InstallPath}').Count; @(Get-DirectoryWriters '{BackupPath}').Count");
            Assert.Equal(["0", "0"], writers.Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        }
        finally
        {
            await RemoveServiceAsync();
        }
    }

    [Fact]
    public async Task Rollback_ShouldRestoreTheWorkingService_WhenAnUpdateFailsToStart()
    {
        Assert.SkipUnless(IsElevated(), "Installing a Windows service needs an elevated session.");
        try
        {
            var working = await RunScriptAsync(_package.Root, "install.ps1", "-SettingsFile", _settingsFile);
            Assert.True(working.ExitCode == 0, working.Output);

            File.WriteAllText(_settingsFile, """{ "worker": { "ConnectionStrings__sampledb": "x", "Sample__FailOnStart": "true" } }""");
            var failing = await RunScriptAsync(_package.Root, "install.ps1", "-SettingsFile", _settingsFile);
            Assert.Equal(1, failing.ExitCode);
            Assert.Contains("Run rollback.ps1 to undo it", failing.Output);

            var rollback = await RunScriptAsync(_package.Root, "rollback.ps1");

            Assert.True(rollback.ExitCode == 0, rollback.Output);
            Assert.Contains("RUNNING", (await PublishedPackage.RunAsync("sc.exe", ["query", ServiceName])).Output);
            var environment = await PublishedPackage.RunAsync("reg.exe",
                ["query", $@"HKLM\SYSTEM\CurrentControlSet\Services\{ServiceName}", "/v", "Environment"]);
            Assert.DoesNotContain("Sample__FailOnStart", environment.Output);
        }
        finally
        {
            await RemoveServiceAsync();
        }
    }

    [Fact]
    public async Task Rollback_ShouldRemoveTheService_WhenItDidNotExistBeforeTheInstall()
    {
        Assert.SkipUnless(IsElevated(), "Installing a Windows service needs an elevated session.");
        try
        {
            var install = await RunScriptAsync(_package.Root, "install.ps1", "-SettingsFile", _settingsFile);
            Assert.True(install.ExitCode == 0, install.Output);

            var rollback = await RunScriptAsync(_package.Root, "rollback.ps1");

            Assert.True(rollback.ExitCode == 0, rollback.Output);
            Assert.NotEqual(0, (await PublishedPackage.RunAsync("sc.exe", ["query", ServiceName])).ExitCode);
            Assert.False(Directory.Exists(InstallPath));
        }
        finally
        {
            await RemoveServiceAsync();
        }
    }

    [Fact]
    public async Task Rollback_ShouldStopUntilForced_WhenUsersCanChangeTheBackup()
    {
        Assert.SkipUnless(IsElevated(), "Installing a Windows service needs an elevated session.");
        try
        {
            var install = await RunScriptAsync(_package.Root, "install.ps1", "-SettingsFile", _settingsFile);
            Assert.True(install.ExitCode == 0, install.Output);
            await PublishedPackage.RunAsync("icacls.exe", [BackupPath, "/grant", "*S-1-5-32-545:(OI)(CI)M"]);

            var refused = await RunScriptAsync(_package.Root, "rollback.ps1");

            Assert.Equal(1, refused.ExitCode);
            Assert.Contains($"{BackupPath} (", refused.Output);
            Assert.Contains("RUNNING", (await PublishedPackage.RunAsync("sc.exe", ["query", ServiceName])).Output);

            var forced = await RunScriptAsync(_package.Root, "rollback.ps1", "-Force");

            Assert.True(forced.ExitCode == 0, forced.Output);
            Assert.NotEqual(0, (await PublishedPackage.RunAsync("sc.exe", ["query", ServiceName])).ExitCode);
        }
        finally
        {
            await RemoveServiceAsync();
        }
    }

    [Fact]
    public async Task Rollback_ShouldRestoreThePreviousFiles_WhenTheServiceWasUpdated()
    {
        Assert.SkipUnless(IsElevated(), "Installing a Windows service needs an elevated session.");
        try
        {
            var first = await RunScriptAsync(_package.Root, "install.ps1", "-SettingsFile", _settingsFile);
            Assert.True(first.ExitCode == 0, first.Output);

            // Stands in for a file that only the earlier version had: the second install removes it.
            var marker = Path.Combine(InstallPath, "from-the-earlier-version.txt");
            await PublishedPackage.RunAsync("sc.exe", ["stop", ServiceName]);
            File.WriteAllText(marker, "");
            var second = await RunScriptAsync(_package.Root, "install.ps1", "-SettingsFile", _settingsFile);
            Assert.True(second.ExitCode == 0, second.Output);
            Assert.False(File.Exists(marker));

            var rollback = await RunScriptAsync(_package.Root, "rollback.ps1");

            Assert.True(rollback.ExitCode == 0, rollback.Output);
            Assert.True(File.Exists(marker));
            Assert.Equal(0, (await PublishedPackage.RunAsync("sc.exe", ["query", ServiceName])).ExitCode);
        }
        finally
        {
            await RemoveServiceAsync();
        }
    }

    private static Task<(int ExitCode, string Output)> RunScriptAsync(string packageRoot, string script, params string[] arguments) =>
        PublishedPackage.RunAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(packageRoot, script), .. arguments]);

    // Runs a command with the functions the scripts share, without running either script.
    private Task<(int ExitCode, string Output)> RunFunctionsAsync(string command) =>
        PublishedPackage.RunAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", $". '{Path.Combine(_package.Root, "common.ps1")}'; {command}"]);

    private static async Task RemoveServiceAsync()
    {
        // "sc stop" returns before the process has exited, and a running process keeps its folder locked.
        await PublishedPackage.RunAsync("sc.exe", ["stop", ServiceName]);
        await PublishedPackage.RunAsync("taskkill.exe", ["/F", "/IM", "Sample.Worker.exe"]);
        await PublishedPackage.RunAsync("sc.exe", ["delete", ServiceName]);
        foreach (var folder in new[] { InstallPath, BackupPath })
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    [SupportedOSPlatformGuard("windows")]
    private static bool IsElevated() =>
        OperatingSystem.IsWindows() && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
}
