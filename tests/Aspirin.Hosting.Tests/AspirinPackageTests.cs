using Aspire.Hosting;

namespace Aspirin.Hosting.Tests;

public class AspirinPackageTests
{
    private static (string ResourceName, PackagedProjectAnnotation Annotation) Service(string name, string installPath, string backupPath) =>
        (name, new WindowsServiceAnnotation(installPath, new WindowsServiceOptions { ServiceName = name, BackupPath = backupPath }));

    private static (string ResourceName, PackagedProjectAnnotation Annotation) Site(
        string name, string pool, IisBinding binding, string installPath = @"C:\Sites\First", string backupPath = @"C:\Backups\First")
    {
        var options = new IisSiteOptions { SiteName = name, AppPoolName = pool, BackupPath = backupPath };
        options.Bindings.Add(binding);
        return (name, new IisSiteAnnotation(installPath, options));
    }

    [Fact]
    public void EnsureResourcesAreSeparate_ShouldNotThrow_WhenEachServiceHasItsOwnNameAndFolders()
    {
        var exception = Record.Exception(() => AspirinPackage.EnsureResourcesAreSeparate(
            [Service("first", @"C:\Services\First", @"C:\Backups\First"), Service("second", @"C:\Services\Second", @"C:\Backups\Second")]));

        Assert.Null(exception);
    }

    [Fact]
    public void EnsureResourcesAreSeparate_ShouldThrow_WhenTwoResourcesUseTheSameServiceName()
    {
        var first = Service("Worker", @"C:\Services\First", @"C:\Backups\First");
        var second = Service("worker", @"C:\Services\Second", @"C:\Backups\Second");

        var exception = Assert.Throws<DistributedApplicationException>(() => AspirinPackage.EnsureResourcesAreSeparate([first, second]));

        Assert.Contains("both use the service name 'Worker'", exception.Message);
    }

    [Theory]
    [InlineData(@"C:\Services\Second", @"C:\Backups")]
    [InlineData(@"C:\Services\First", @"C:\Backups\Second")]
    [InlineData(@"C:\Backups\First\Second", @"C:\Backups\Second")]
    [InlineData(@"C:\Services\Second", @"C:\Services\First\Backups")]
    public void EnsureResourcesAreSeparate_ShouldThrow_WhenTwoResourcesUseOverlappingFolders(string secondInstallPath, string secondBackupPath)
    {
        var first = Service("first", @"C:\Services\First", @"C:\Backups\First");
        var second = Service("second", secondInstallPath, secondBackupPath);

        Assert.Throws<DistributedApplicationException>(() => AspirinPackage.EnsureResourcesAreSeparate([first, second]));
    }

    [Fact]
    public void EnsureResourcesAreSeparate_ShouldThrow_WhenSiteUsesTheFoldersOfAService()
    {
        var service = Service("worker", @"C:\Apps\Shared", @"C:\Backups\Worker");
        var site = Site("web", "web", IisBinding.Http(80), installPath: @"C:\Apps\Shared\Web");

        Assert.Throws<DistributedApplicationException>(() => AspirinPackage.EnsureResourcesAreSeparate([service, site]));
    }

    [Theory]
    [InlineData("Web", "Second", "both use the site name 'Web'")]
    [InlineData("Second", "Pool", "both use the application pool 'Pool'")]
    public void EnsureResourcesAreSeparate_ShouldThrow_WhenTwoSitesShareASiteOrPoolName(string secondName, string secondPool, string expected)
    {
        var first = Site("Web", "Pool", IisBinding.Http(80, "first.test"));
        var second = Site(secondName, secondPool, IisBinding.Http(80, "second.test"), @"C:\Sites\Second", @"C:\Backups\Second");

        var exception = Assert.Throws<DistributedApplicationException>(() => AspirinPackage.EnsureResourcesAreSeparate([first, second]));

        Assert.Contains(expected, exception.Message);
    }

    [Theory]
    [InlineData("*", "*")]
    [InlineData("*", "10.0.0.5")]
    [InlineData("10.0.0.5", "10.0.0.5")]
    public void EnsureResourcesAreSeparate_ShouldThrow_WhenTwoSitesAnswerOnTheSameAddress(string firstAddress, string secondAddress)
    {
        var first = Site("first", "first", IisBinding.Http(80, "web.test") with { IpAddress = firstAddress });
        var second = Site("second", "second", IisBinding.Https(80, "WEB.test", certificateSubject: "CN=web.test") with { IpAddress = secondAddress },
            @"C:\Sites\Second", @"C:\Backups\Second");

        var exception = Assert.Throws<DistributedApplicationException>(() => AspirinPackage.EnsureResourcesAreSeparate([first, second]));

        Assert.Contains("both answer on", exception.Message);
    }

    [Theory]
    [InlineData("*", "other.test", "*")]
    [InlineData("10.0.0.5", "web.test", "10.0.0.6")]
    public void EnsureResourcesAreSeparate_ShouldNotThrow_WhenTwoSitesDifferInHostNameOrAddress(
        string firstAddress, string secondHostName, string secondAddress)
    {
        var first = Site("first", "first", IisBinding.Http(80, "web.test") with { IpAddress = firstAddress });
        var second = Site("second", "second", IisBinding.Http(80, secondHostName) with { IpAddress = secondAddress },
            @"C:\Sites\Second", @"C:\Backups\Second");

        var exception = Record.Exception(() => AspirinPackage.EnsureResourcesAreSeparate([first, second]));

        Assert.Null(exception);
    }
}
