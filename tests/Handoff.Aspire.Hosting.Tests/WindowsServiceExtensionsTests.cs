using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Handoff.Aspire.Hosting.Tests;

public class WindowsServiceExtensionsTests
{
    private static IResourceBuilder<ProjectResource> AddProject(string name = "worker") =>
        DistributedApplication.CreateBuilder(["--operation", "publish"]).AddProject<TestProject>(name);

    [Fact]
    public void PublishAsWindowsService_ShouldDefaultNamesAndBackupPath_WhenGivenOnlyInstallPath()
    {
        var project = AddProject().PublishAsWindowsService(@"C:\Services\Worker\");

        var annotation = Assert.Single(project.Resource.Annotations.OfType<WindowsServiceAnnotation>());
        Assert.Equal(@"C:\Services\Worker", annotation.InstallPath);
        Assert.Equal("worker", annotation.Options.ServiceName);
        Assert.Equal("worker", annotation.Options.DisplayName);
        Assert.Equal(@"C:\Services\Worker.backups", annotation.Options.BackupPath);
    }

    [Fact]
    public void PublishAsWindowsService_ShouldKeepTheLastOptions_WhenCalledTwiceOnOneProject()
    {
        var project = AddProject()
            .PublishAsWindowsService(@"C:\Services\Old")
            .PublishAsWindowsService(@"C:\Services\New");

        var annotation = Assert.Single(project.Resource.Annotations.OfType<WindowsServiceAnnotation>());
        Assert.Equal(@"C:\Services\New", annotation.InstallPath);
    }

    [Fact]
    public void PublishAsWindowsService_ShouldRegisterThePackageStepOnce_WhenCalledForTwoProjects()
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        builder.AddProject<TestProject>("first").PublishAsWindowsService(@"C:\Services\First");

        // The pipeline rejects a second step with the same name, so a duplicate registration would throw here.
        var exception = Record.Exception(() => builder.AddProject<TestProject>("second").PublishAsWindowsService(@"C:\Services\Second"));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData(@"Services\Worker")]
    [InlineData("/opt/worker")]
    [InlineData(@"C:\")]
    public void PublishAsWindowsService_ShouldThrow_WhenGivenInstallPathThatIsNotAnAbsoluteWindowsFolder(string installPath)
    {
        var project = AddProject();

        Assert.Throws<ArgumentException>(() => project.PublishAsWindowsService(installPath));
    }

    [Theory]
    [InlineData(@"C:\Services\Worker\backups")]
    [InlineData(@"C:\Services")]
    [InlineData(@"c:\services\worker")]
    public void PublishAsWindowsService_ShouldThrow_WhenGivenBackupPathThatOverlapsInstallPath(string backupPath)
    {
        var project = AddProject();

        Assert.Throws<ArgumentException>(() => project.PublishAsWindowsService(@"C:\Services\Worker", o => o.BackupPath = backupPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"Workers\One")]
    [InlineData("Workers/One")]
    public void PublishAsWindowsService_ShouldThrow_WhenGivenServiceNameWindowsRejects(string serviceName)
    {
        var project = AddProject();

        Assert.Throws<ArgumentException>(() => project.PublishAsWindowsService(@"C:\Services\Worker", o => o.ServiceName = serviceName));
    }
}
