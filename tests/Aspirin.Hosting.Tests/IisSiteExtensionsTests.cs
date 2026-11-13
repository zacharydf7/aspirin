using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Aspirin.Hosting.Tests;

public class IisSiteExtensionsTests
{
    private static IResourceBuilder<ProjectResource> AddProject(string name = "web") =>
        DistributedApplication.CreateBuilder(["--operation", "publish"]).AddProject<TestProject>(name);

    private static Action<IisSiteOptions> WithBinding(IisBinding binding, Action<IisSiteOptions>? configure = null) => site =>
    {
        site.Bindings.Add(binding);
        configure?.Invoke(site);
    };

    [Fact]
    public void PublishAsIisSite_ShouldDefaultNamesAndBackupPath_WhenGivenOnlyABinding()
    {
        var project = AddProject().PublishAsIisSite(@"C:\inetpub\Web\", WithBinding(IisBinding.Http(80)));

        var annotation = Assert.Single(project.Resource.Annotations.OfType<IisSiteAnnotation>());
        Assert.Equal(@"C:\inetpub\Web", annotation.InstallPath);
        Assert.Equal("web", annotation.Options.SiteName);
        Assert.Equal("web", annotation.Options.AppPoolName);
        Assert.Equal(@"C:\inetpub\Web.backups", annotation.Options.BackupPath);
    }

    [Fact]
    public void PublishAsIisSite_ShouldReplaceTheWindowsService_WhenCalledAfterPublishAsWindowsService()
    {
        var project = AddProject()
            .PublishAsWindowsService(@"C:\Services\Web")
            .PublishAsIisSite(@"C:\inetpub\Web", WithBinding(IisBinding.Http(80)));

        var annotation = Assert.Single(project.Resource.Annotations.OfType<PackagedProjectAnnotation>());
        Assert.IsType<IisSiteAnnotation>(annotation);
    }

    [Fact]
    public void PublishAsIisSite_ShouldThrow_WhenGivenNoBindings()
    {
        var project = AddProject();

        var exception = Assert.Throws<ArgumentException>(() => project.PublishAsIisSite(@"C:\inetpub\Web", _ => { }));

        Assert.Contains("needs at least one binding", exception.Message);
    }

    [Theory]
    [InlineData("Web/Admin")]
    [InlineData("Web's")]
    [InlineData("")]
    public void PublishAsIisSite_ShouldThrow_WhenGivenSiteNameWithCharactersIisRejects(string siteName)
    {
        var project = AddProject();

        Assert.Throws<ArgumentException>(() =>
            project.PublishAsIisSite(@"C:\inetpub\Web", WithBinding(IisBinding.Http(80), site => site.SiteName = siteName)));
    }

    public static TheoryData<IisBinding, string> InvalidBindings => new()
    {
        { IisBinding.Http(0), "has a port outside 1 to 65535" },
        { IisBinding.Http(80, "web_site.test"), "isn't a valid DNS name" },
        { IisBinding.Http(80) with { IpAddress = "10.0.0" }, "neither * nor a valid address" },
        { IisBinding.Https(443, "web.test"), "needs the certificate's subject or thumbprint" },
        { IisBinding.Https(443, "web.test", "CN=web.test", new string('A', 40)), "gives both" },
        { IisBinding.Https(443, "web.test", certificateThumbprint: "ABC"), "isn't 40 hexadecimal characters" },
        { IisBinding.Https(443, "web.test", "CN=web.test") with { CertificateStore = "Root" }, "other than My or WebHosting" },
    };

    [Theory]
    [MemberData(nameof(InvalidBindings))]
    public void PublishAsIisSite_ShouldThrow_WhenGivenInvalidBinding(IisBinding binding, string expected)
    {
        var project = AddProject();

        var exception = Assert.Throws<ArgumentException>(() => project.PublishAsIisSite(@"C:\inetpub\Web", WithBinding(binding)));

        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public void PublishAsIisSite_ShouldThrow_WhenGivenTheSameAddressTwice()
    {
        var project = AddProject();

        var exception = Assert.Throws<ArgumentException>(() => project.PublishAsIisSite(@"C:\inetpub\Web", site =>
        {
            site.Bindings.Add(IisBinding.Http(8080, "web.test"));
            site.Bindings.Add(IisBinding.Https(8080, "WEB.test", certificateSubject: "CN=web.test"));
        }));

        Assert.Contains("more than once", exception.Message);
    }

    [Fact]
    public void PublishAsIisSite_ShouldThrow_WhenGivenHealthCheckPathWithoutLeadingSlash()
    {
        var project = AddProject();

        Assert.Throws<ArgumentException>(() =>
            project.PublishAsIisSite(@"C:\inetpub\Web", WithBinding(IisBinding.Http(80), site => site.HealthCheckPath = "health")));
    }

    [Fact]
    public void PublishAsIisSite_ShouldAcceptWildcardHostNameAndIpv6Address_WhenGivenThem()
    {
        var project = AddProject();

        var exception = Record.Exception(() =>
            project.PublishAsIisSite(@"C:\inetpub\Web", WithBinding(IisBinding.Http(80, "*.web.test") with { IpAddress = "::1" })));

        Assert.Null(exception);
    }
}
