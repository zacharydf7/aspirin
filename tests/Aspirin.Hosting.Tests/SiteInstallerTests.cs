namespace Aspirin.Hosting.Tests;

/// <summary>
/// Installs the sample IIS site with the package's scripts. The tests that change IIS need an elevated session,
/// IIS and the ASP.NET Core Module, and are skipped without them.
/// </summary>
public sealed class SiteInstallerTests : IClassFixture<SiteInstallerTests.Certificate>, IAsyncDisposable
{
    private const string SiteName = "Aspirin Sample Web";
    private const string InstallPath = @"C:\inetpub\AspirinSampleWeb";
    private const string BackupPath = InstallPath + ".backups";
    private const string HostName = "aspirin-sample.test";
    private const int HttpPort = 18091;
    private const int HttpsPort = 18443;

    private static readonly string s_appCmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\inetsrv\appcmd.exe");

    private readonly PublishedSitePackage _package;
    private readonly string _workFolder = Directory.CreateTempSubdirectory("aspirin-site-installer-").FullName;
    private readonly string _settingsFile;

    public SiteInstallerTests(PublishedSitePackage package)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The installer runs on Windows only.");
        _package = package;
        _settingsFile = Path.Combine(_workFolder, "settings.json");
    }

    public ValueTask DisposeAsync()
    {
        Directory.Delete(_workFolder, recursive: true);
        return ValueTask.CompletedTask;
    }

    // Installing needs elevation, and the package's pre-checks need IIS and the ASP.NET Core Module. WhatIf runs the
    // same pre-checks when it's elevated.
    private static bool CanInstall() =>
        PackageScripts.IsElevated() && File.Exists(s_appCmd) &&
        File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"IIS\Asp.Net Core Module\V2\aspnetcorev2.dll"));

    [Fact]
    public async Task Install_ShouldListTheSiteAndItsBindings_WhenGivenWhatIf()
    {
        Assert.SkipWhen(PackageScripts.IsElevated() && !CanInstall(), "An elevated WhatIf checks IIS, which this machine doesn't have.");

        var result = await PackageScripts.RunScriptAsync(_package.Root, "install.ps1", "-WhatIf");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Matches($@"Would (create|create or update) the IIS site '{SiteName}'", result.Output);
        Assert.Contains($"https://{HostName}:{HttpsPort}", result.Output);
        Assert.Matches(@"Settings:    .*Sample__Greeting", result.Output);
    }

    [Fact]
    public async Task Install_ShouldCreateThenUpdateTheSite_WhenRunTwice()
    {
        Assert.SkipUnless(CanInstall(), "Installing an IIS site needs an elevated session, IIS and the ASP.NET Core Module.");
        try
        {
            var first = await PackageScripts.RunScriptAsync(_package.Root, "install.ps1");
            Assert.True(first.ExitCode == 0, first.Output);
            var second = await PackageScripts.RunScriptAsync(_package.Root, "install.ps1");
            Assert.True(second.ExitCode == 0, second.Output);

            Assert.Contains("Creating the site", first.Output);
            Assert.Contains("Updating the site", second.Output);
            Assert.Equal($"200 {PublishedSitePackage.Greeting}", await GetAsync("/"));
            Assert.False(File.Exists(Path.Combine(InstallPath, "app_offline.htm")));
            Assert.Equal(2, Directory.GetDirectories(BackupPath).Length);

            var pool = await PublishedSample.RunAsync(s_appCmd, ["list", "apppool", SiteName, "/config"]);
            Assert.Contains($"name=\"Sample__Greeting\" value=\"{PublishedSitePackage.Greeting}\"", pool.Output);
            Assert.Contains("managedRuntimeVersion=\"\"", pool.Output);

            var certificate = await PublishedSample.RunAsync("netsh.exe", ["http", "show", "sslcert", $"hostnameport={HostName}:{HttpsPort}"]);
            Assert.Equal(0, certificate.ExitCode);
        }
        finally
        {
            await RemoveSiteAsync();
        }
    }

    [Fact]
    public async Task Install_ShouldRemoveABindingAddedByHand_WhenUpdatingTheSite()
    {
        Assert.SkipUnless(CanInstall(), "Installing an IIS site needs an elevated session, IIS and the ASP.NET Core Module.");
        try
        {
            var first = await PackageScripts.RunScriptAsync(_package.Root, "install.ps1");
            Assert.True(first.ExitCode == 0, first.Output);
            await PublishedSample.RunAsync(s_appCmd, ["set", "site", $"/site.name:{SiteName}", "/+bindings.[protocol='http',bindingInformation='*:18092:']"]);

            var plan = await PackageScripts.RunScriptAsync(_package.Root, "install.ps1", "-WhatIf");
            var second = await PackageScripts.RunScriptAsync(_package.Root, "install.ps1");

            Assert.Contains("Remove:      the binding http *:18092:", plan.Output);
            Assert.True(second.ExitCode == 0, second.Output);
            var site = await PublishedSample.RunAsync(s_appCmd, ["list", "site", SiteName]);
            Assert.DoesNotContain("18092", site.Output);
            Assert.Contains($"{HttpPort}", site.Output);
        }
        finally
        {
            await RemoveSiteAsync();
        }
    }

    [Fact]
    public async Task Rollback_ShouldRestoreTheWorkingSite_WhenAnUpdateFailsItsHealthCheck()
    {
        Assert.SkipUnless(CanInstall(), "Installing an IIS site needs an elevated session, IIS and the ASP.NET Core Module.");
        try
        {
            var working = await PackageScripts.RunScriptAsync(_package.Root, "install.ps1");
            Assert.True(working.ExitCode == 0, working.Output);

            File.WriteAllText(_settingsFile, """{ "web": { "Sample__FailOnStart": "true" } }""");
            var failing = await PackageScripts.RunScriptAsync(_package.Root, "install.ps1", "-SettingsFile", _settingsFile);
            Assert.Equal(1, failing.ExitCode);
            Assert.Contains("didn't answer", failing.Output);
            Assert.Contains("Run rollback.ps1 to undo it", failing.Output);

            var rollback = await PackageScripts.RunScriptAsync(_package.Root, "rollback.ps1");

            Assert.True(rollback.ExitCode == 0, rollback.Output);
            Assert.Equal($"200 {PublishedSitePackage.Greeting}", await GetAsync("/"));
            Assert.False(File.Exists(Path.Combine(InstallPath, "app_offline.htm")));
            var pool = await PublishedSample.RunAsync(s_appCmd, ["list", "apppool", SiteName, "/config"]);
            Assert.DoesNotContain("Sample__FailOnStart", pool.Output);
        }
        finally
        {
            await RemoveSiteAsync();
        }
    }

    [Fact]
    public async Task Rollback_ShouldRemoveTheSiteAndPool_WhenTheyDidNotExistBeforeTheInstall()
    {
        Assert.SkipUnless(CanInstall(), "Installing an IIS site needs an elevated session, IIS and the ASP.NET Core Module.");
        try
        {
            var install = await PackageScripts.RunScriptAsync(_package.Root, "install.ps1");
            Assert.True(install.ExitCode == 0, install.Output);

            var rollback = await PackageScripts.RunScriptAsync(_package.Root, "rollback.ps1");

            Assert.True(rollback.ExitCode == 0, rollback.Output);
            Assert.NotEqual(0, (await PublishedSample.RunAsync(s_appCmd, ["list", "site", SiteName])).ExitCode);
            Assert.NotEqual(0, (await PublishedSample.RunAsync(s_appCmd, ["list", "apppool", SiteName])).ExitCode);
            Assert.NotEqual(0, (await PublishedSample.RunAsync("netsh.exe", ["http", "show", "sslcert", $"hostnameport={HostName}:{HttpsPort}"])).ExitCode);
            Assert.False(Directory.Exists(InstallPath));
        }
        finally
        {
            await RemoveSiteAsync();
        }
    }

    [Fact]
    public async Task Install_ShouldFailBeforeChangingAnything_WhenTheCertificateIsMissing()
    {
        Assert.SkipUnless(CanInstall(), "Installing an IIS site needs an elevated session, IIS and the ASP.NET Core Module.");
        try
        {
            await Certificate.RemoveAsync();

            var result = await PackageScripts.RunScriptAsync(_package.Root, "install.ps1");

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("No certificate in LocalMachine\\My with the subject 'CN=aspirin-sample.test'", result.Output);
            Assert.Contains("Nothing was changed.", result.Output);
            Assert.NotEqual(0, (await PublishedSample.RunAsync(s_appCmd, ["list", "site", SiteName])).ExitCode);
        }
        finally
        {
            await Certificate.CreateAsync();
            await RemoveSiteAsync();
        }
    }

    // Asks the site on this machine's address, naming its host in the Host header, so no DNS entry is needed.
    private static async Task<string> GetAsync(string path)
    {
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{HttpPort}{path}");
        request.Headers.Host = $"{HostName}:{HttpPort}";
        using var response = await client.SendAsync(request);
        return $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}";
    }

    private static async Task RemoveSiteAsync()
    {
        await PublishedSample.RunAsync(s_appCmd, ["delete", "site", SiteName]);
        await PublishedSample.RunAsync(s_appCmd, ["delete", "apppool", SiteName]);
        await PublishedSample.RunAsync("netsh.exe", ["http", "delete", "sslcert", $"hostnameport={HostName}:{HttpsPort}"]);

        // The installer backs up the whole IIS configuration on each run; these tests made the ones named after Aspirin.
        var backups = await PublishedSample.RunAsync(s_appCmd, ["list", "backup"]);
        foreach (var name in backups.Output.Split('"').Where(part => part.StartsWith("aspirin-", StringComparison.Ordinal)))
        {
            await PublishedSample.RunAsync(s_appCmd, ["delete", "backup", name]);
        }

        // A worker process of the deleted pool can take a few seconds to exit and release the site's files.
        foreach (var folder in new[] { InstallPath, BackupPath })
        {
            for (var attempt = 1; Directory.Exists(folder); attempt++)
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception exception) when (attempt < 15 && exception is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
            }
        }
    }

    /// <summary>
    /// The self-signed certificate the sample site's HTTPS binding names, in the machine store while the tests run.
    /// </summary>
    public sealed class Certificate : IAsyncLifetime
    {
        public async ValueTask InitializeAsync()
        {
            if (CanInstall())
            {
                await CreateAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (CanInstall())
            {
                await RemoveAsync();
            }
        }

        public static Task CreateAsync() => RunPowerShellAsync(
            $"New-SelfSignedCertificate -DnsName '{HostName}' -CertStoreLocation Cert:\\LocalMachine\\My | Out-Null");

        public static Task RemoveAsync() => RunPowerShellAsync(
            $"Get-ChildItem Cert:\\LocalMachine\\My | Where-Object Subject -eq 'CN={HostName}' | Remove-Item");

        private static async Task RunPowerShellAsync(string command)
        {
            var result = await PublishedSample.RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", command]);
            Assert.True(result.ExitCode == 0, result.Output);
        }
    }
}
