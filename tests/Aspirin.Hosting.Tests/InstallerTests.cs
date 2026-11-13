using System.Security.Principal;

namespace Aspirin.Hosting.Tests;

/// <summary>
/// Runs the package's scripts in Windows PowerShell. The tests that install the service need an elevated session
/// and are skipped without one.
/// </summary>
public sealed class InstallerTests : IAsyncDisposable
{
    private const string ServiceName = "AspirinSampleWorker";
    private const string InstallPath = @"C:\Program Files\AspirinSampleWorker";
    private const string BackupPath = InstallPath + ".backups";

    private readonly PublishedPackage _package;
    private readonly string _workFolder = Directory.CreateTempSubdirectory("aspirin-installer-").FullName;
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
        var result = await PackageScripts.RunScriptAsync(_package.Root, "install.ps1", "-WhatIf", "-SettingsFile", _settingsFile);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Matches($@"Would create the Windows service '{ServiceName}' \(version 1\.2\.3(\+[0-9a-f]{{40}})?\)", result.Output);
        Assert.Contains("ConnectionStrings__sampledb, ", result.Output);
        Assert.DoesNotContain("from-the-settings-file", result.Output);
    }

    [Fact]
    public async Task Install_ShouldFailBeforeChangingAnything_WhenSecretSettingHasNoValue()
    {
        var result = await PackageScripts.RunScriptAsync(_package.Root, "install.ps1", "-WhatIf");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("worker: ConnectionStrings__sampledb", result.Output);
        Assert.Contains("Nothing was changed.", result.Output);
    }

    [Fact]
    public async Task Install_ShouldFailBeforeChangingAnything_WhenSettingsFileNamesUnknownResource()
    {
        File.WriteAllText(_settingsFile, """{ "wroker": { "ConnectionStrings__sampledb": "x" } }""");

        var result = await PackageScripts.RunScriptAsync(_package.Root, "install.ps1", "-WhatIf", "-SettingsFile", _settingsFile);

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

        var result = await PackageScripts.RunScriptAsync(copy, "install.ps1", "-WhatIf", "-SettingsFile", _settingsFile);

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
                { "backupPath": "{{firstBackups.Replace(@"\", @"\\")}}" },
                { "backupPath": "{{secondBackups.Replace(@"\", @"\\")}}" } ] }
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
        var owner = PackageScripts.IsElevated() ? "" : CurrentAccount();
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

        // A service account, not the account running the test: on GitHub's runners the running account ends up with full
        // control of a folder it grants itself read access to.
        const string Account = @"NT AUTHORITY\LOCAL SERVICE";

        var result = await RunFunctionsAsync($"New-ProtectedDirectory '{folder}'; Grant-DirectoryRead '{folder}' '{Account}'");

        Assert.True(result.ExitCode == 0, result.Output);
        // Assert.Contains would cut the listing short in its failure message, and the listing is what explains a failure.
        var permissions = (await PublishedPackage.RunAsync("icacls.exe", [folder])).Output;
        Assert.True(permissions.Contains($"{Account}:(OI)(CI)(RX)", StringComparison.OrdinalIgnoreCase), permissions);
        Assert.True(permissions.Contains(@"NT AUTHORITY\SYSTEM:(OI)(CI)(F)", StringComparison.Ordinal), permissions);
    }

    [Theory]
    [InlineData(@"CORP\svc-worker", @"Domain|CORP|svc-worker")]
    [InlineData("svc-worker@company.com", "Domain||svc-worker@company.com")]
    [InlineData(@".\svc-worker", "Machine|<computer>|svc-worker")]
    [InlineData(@"<computer>\svc-worker", "Machine|<computer>|svc-worker")]
    [InlineData("svc-worker", "Machine|<computer>|svc-worker")]
    public async Task GetAccountContext_ShouldPickWhereThePasswordIsChecked_WhenGivenEachAccountForm(string account, string expected)
    {
        var computer = Environment.MachineName;

        var result = await RunFunctionsAsync(
            $"$c = Get-AccountContext '{account.Replace("<computer>", computer)}'; \"$($c.ContextType)|$($c.ContextName)|$($c.UserName)\"");

        Assert.Equal(expected.Replace("<computer>", computer), result.Output.Trim(), ignoreCase: true);
    }

    [Fact]
    public async Task AssertAccountPassword_ShouldThrow_WhenTheLocalAccountDoesNotExist()
    {
        var result = await RunFunctionsAsync(CheckPasswordCommand(@".\aspirin-no-such-account", TestPassword));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(@"The password for '.\aspirin-no-such-account' is wrong", result.Output);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("computer")]
    public async Task AssertAccountPassword_ShouldPass_WhenGivenTheRightPasswordOfALocalAccount(string prefix)
    {
        Assert.SkipUnless(PackageScripts.IsElevated(), "Creating a local account needs an elevated session.");
        try
        {
            await CreateTestAccountAsync();
            var account = (prefix == "." ? "." : Environment.MachineName) + @"\" + TestAccount;

            var result = await RunFunctionsAsync(CheckPasswordCommand(account, TestPassword));

            Assert.True(result.ExitCode == 0, result.Output);
        }
        finally
        {
            await PublishedPackage.RunAsync("net.exe", ["user", TestAccount, "/delete"]);
        }
    }

    [Fact]
    public async Task AssertAccountPassword_ShouldThrow_WhenGivenAWrongPassword()
    {
        Assert.SkipUnless(PackageScripts.IsElevated(), "Creating a local account needs an elevated session.");
        try
        {
            await CreateTestAccountAsync();

            var result = await RunFunctionsAsync(CheckPasswordCommand($@".\{TestAccount}", "Not-the-password-1"));

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains($@"The password for '.\{TestAccount}' is wrong", result.Output);
        }
        finally
        {
            await PublishedPackage.RunAsync("net.exe", ["user", TestAccount, "/delete"]);
        }
    }

    [Fact]
    public async Task Install_ShouldUpdateTheSameService_WhenRunTwice()
    {
        Assert.SkipUnless(PackageScripts.IsElevated(), "Installing a Windows service needs an elevated session.");
        try
        {
            var first = await PackageScripts.RunScriptAsync(_package.Root, "install.ps1", "-SettingsFile", _settingsFile);
            Assert.True(first.ExitCode == 0, first.Output);
            var second = await PackageScripts.RunScriptAsync(_package.Root, "install.ps1", "-SettingsFile", _settingsFile);
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
        Assert.SkipUnless(PackageScripts.IsElevated(), "Installing a Windows service needs an elevated session.");
        try
        {
            var working = await PackageScripts.RunScriptAsync(_package.Root, "install.ps1", "-SettingsFile", _settingsFile);
            Assert.True(working.ExitCode == 0, working.Output);

            File.WriteAllText(_settingsFile, """{ "worker": { "ConnectionStrings__sampledb": "x", "Sample__FailOnStart": "true" } }""");
            var failing = await PackageScripts.RunScriptAsync(_package.Root, "install.ps1", "-SettingsFile", _settingsFile);
            Assert.Equal(1, failing.ExitCode);
            Assert.Contains("Run rollback.ps1 to undo it", failing.Output);

            var rollback = await PackageScripts.RunScriptAsync(_package.Root, "rollback.ps1");

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
        Assert.SkipUnless(PackageScripts.IsElevated(), "Installing a Windows service needs an elevated session.");
        try
        {
            var install = await PackageScripts.RunScriptAsync(_package.Root, "install.ps1", "-SettingsFile", _settingsFile);
            Assert.True(install.ExitCode == 0, install.Output);

            var rollback = await PackageScripts.RunScriptAsync(_package.Root, "rollback.ps1");

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
        Assert.SkipUnless(PackageScripts.IsElevated(), "Installing a Windows service needs an elevated session.");
        try
        {
            var install = await PackageScripts.RunScriptAsync(_package.Root, "install.ps1", "-SettingsFile", _settingsFile);
            Assert.True(install.ExitCode == 0, install.Output);
            await PublishedPackage.RunAsync("icacls.exe", [BackupPath, "/grant", "*S-1-5-32-545:(OI)(CI)M"]);

            var refused = await PackageScripts.RunScriptAsync(_package.Root, "rollback.ps1");

            Assert.Equal(1, refused.ExitCode);
            Assert.Contains($"{BackupPath} (", refused.Output);
            Assert.Contains("RUNNING", (await PublishedPackage.RunAsync("sc.exe", ["query", ServiceName])).Output);

            var forced = await PackageScripts.RunScriptAsync(_package.Root, "rollback.ps1", "-Force");

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
        Assert.SkipUnless(PackageScripts.IsElevated(), "Installing a Windows service needs an elevated session.");
        try
        {
            var first = await PackageScripts.RunScriptAsync(_package.Root, "install.ps1", "-SettingsFile", _settingsFile);
            Assert.True(first.ExitCode == 0, first.Output);

            // Stands in for a file that only the earlier version had: the second install removes it.
            var marker = Path.Combine(InstallPath, "from-the-earlier-version.txt");
            await PublishedPackage.RunAsync("sc.exe", ["stop", ServiceName]);
            File.WriteAllText(marker, "");
            var second = await PackageScripts.RunScriptAsync(_package.Root, "install.ps1", "-SettingsFile", _settingsFile);
            Assert.True(second.ExitCode == 0, second.Output);
            Assert.False(File.Exists(marker));

            var rollback = await PackageScripts.RunScriptAsync(_package.Root, "rollback.ps1");

            Assert.True(rollback.ExitCode == 0, rollback.Output);
            Assert.True(File.Exists(marker));
            Assert.Equal(0, (await PublishedPackage.RunAsync("sc.exe", ["query", ServiceName])).ExitCode);
        }
        finally
        {
            await RemoveServiceAsync();
        }
    }

    private Task<(int ExitCode, string Output)> RunFunctionsAsync(string command) => PackageScripts.RunFunctionsAsync(_package.Root, command);

    private const string TestAccount = "aspirin-test";
    // Windows Server rejects a password that contains the account name, and "net user" asks for confirmation of one
    // longer than 14 characters.
    private const string TestPassword = "Xq7-Chk-Pw-204";

    private static string CheckPasswordCommand(string account, string password) =>
        $"Assert-AccountPassword (New-Object PSCredential('{account}', (ConvertTo-SecureString '{password}' -AsPlainText -Force)))";

    private static async Task CreateTestAccountAsync()
    {
        var created = await PublishedPackage.RunAsync("net.exe", ["user", TestAccount, TestPassword, "/add"]);
        Assert.True(created.ExitCode == 0, created.Output);
    }

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

    // The account as Windows names it (DOMAIN\user). Building it from Environment.UserDomainName can give a different
    // domain, as it does on GitHub's runners.
    private static string CurrentAccount() => OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().Name : "";

}
