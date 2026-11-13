using Aspire.Hosting;

namespace Aspirin.Hosting.Tests;

public class AspirinPackageTests
{
    private static (string ResourceName, WindowsServiceAnnotation Service) Service(string name, string installPath, string backupPath) =>
        (name, new WindowsServiceAnnotation(installPath, new WindowsServiceOptions { ServiceName = name, BackupPath = backupPath }));

    [Fact]
    public void EnsureServicesAreSeparate_ShouldNotThrow_WhenEachServiceHasItsOwnNameAndFolders()
    {
        var exception = Record.Exception(() => AspirinPackage.EnsureServicesAreSeparate(
            [Service("first", @"C:\Services\First", @"C:\Backups\First"), Service("second", @"C:\Services\Second", @"C:\Backups\Second")]));

        Assert.Null(exception);
    }

    [Fact]
    public void EnsureServicesAreSeparate_ShouldThrow_WhenTwoResourcesUseTheSameServiceName()
    {
        var first = Service("Worker", @"C:\Services\First", @"C:\Backups\First");
        var second = Service("worker", @"C:\Services\Second", @"C:\Backups\Second");

        var exception = Assert.Throws<DistributedApplicationException>(() => AspirinPackage.EnsureServicesAreSeparate([first, second]));

        Assert.Contains("both use the service name 'Worker'", exception.Message);
    }

    [Theory]
    [InlineData(@"C:\Services\Second", @"C:\Backups")]
    [InlineData(@"C:\Services\First", @"C:\Backups\Second")]
    [InlineData(@"C:\Backups\First\Second", @"C:\Backups\Second")]
    [InlineData(@"C:\Services\Second", @"C:\Services\First\Backups")]
    public void EnsureServicesAreSeparate_ShouldThrow_WhenTwoResourcesUseOverlappingFolders(string secondInstallPath, string secondBackupPath)
    {
        var first = Service("first", @"C:\Services\First", @"C:\Backups\First");
        var second = Service("second", secondInstallPath, secondBackupPath);

        Assert.Throws<DistributedApplicationException>(() => AspirinPackage.EnsureServicesAreSeparate([first, second]));
    }
}
