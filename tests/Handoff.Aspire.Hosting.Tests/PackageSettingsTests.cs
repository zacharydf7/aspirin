using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Handoff.Aspire.Hosting.Tests;

public class PackageSettingsTests
{
    private static async Task<PackageSetting> ResolveAsync(
        string name, Action<IDistributedApplicationBuilder, IResourceBuilder<ProjectResource>> configure, bool includeSecretValues = false)
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var worker = builder.AddProject<TestProject>("worker");
        configure(builder, worker);

        await using var app = builder.Build();
        var settings = await PackageSettings.ResolveAsync(worker.Resource,
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(), NullLogger.Instance, includeSecretValues,
            TestContext.Current.CancellationToken);

        return Assert.Single(settings, setting => setting.Name == name);
    }

    [Fact]
    public async Task ResolveAsync_ShouldKeepTheValueAndMarkItSecret_WhenGivenSecretParameterAndSecretValuesAreIncluded()
    {
        var setting = await ResolveAsync("ApiKey", (builder, worker) =>
        {
            builder.Configuration["Parameters:apikey"] = "s3cret";
            worker.WithEnvironment("ApiKey", builder.AddParameter("apikey", secret: true));
        }, includeSecretValues: true);

        Assert.Equal(new PackageSetting("ApiKey", "s3cret", Secret: true), setting);
    }

    [Fact]
    public async Task ResolveAsync_ShouldKeepTheValueAndMarkItSecret_WhenGivenConnectionStringAndSecretValuesAreIncluded()
    {
        var setting = await ResolveAsync("ConnectionStrings__db", (builder, worker) =>
        {
            builder.Configuration["ConnectionStrings:db"] = "Server=db;Password=s3cret";
            worker.WithReference(builder.AddConnectionString("db"));
        }, includeSecretValues: true);

        Assert.Equal(new PackageSetting("ConnectionStrings__db", "Server=db;Password=s3cret", Secret: true), setting);
    }

    [Fact]
    public async Task ResolveAsync_ShouldThrow_WhenSecretValuesAreIncludedButTheSecretHasNoValue()
    {
        var resolve = () => ResolveAsync("ApiKey",
            (builder, worker) => worker.WithEnvironment("ApiKey", builder.AddParameter("apikey", secret: true)), includeSecretValues: true);

        await Assert.ThrowsAsync<MissingParameterValueException>(resolve);
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnTheValue_WhenGivenLiteral()
    {
        var setting = await ResolveAsync("Mode", (_, worker) => worker.WithEnvironment("Mode", "fast"));

        Assert.Equal(new PackageSetting("Mode", "fast"), setting);
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnTheValue_WhenGivenParameter()
    {
        var setting = await ResolveAsync("Greeting", (builder, worker) =>
        {
            builder.Configuration["Parameters:greeting"] = "hello";
            worker.WithEnvironment("Greeting", builder.AddParameter("greeting"));
        });

        Assert.Equal(new PackageSetting("Greeting", "hello"), setting);
    }

    [Fact]
    public async Task ResolveAsync_ShouldLeaveOutTheValue_WhenGivenSecretParameter()
    {
        var setting = await ResolveAsync("ApiKey", (builder, worker) =>
        {
            builder.Configuration["Parameters:apikey"] = "s3cret";
            worker.WithEnvironment("ApiKey", builder.AddParameter("apikey", secret: true));
        });

        Assert.Equal(new PackageSetting("ApiKey", Secret: true), setting);
    }

    [Fact]
    public async Task ResolveAsync_ShouldLeaveOutTheValue_WhenGivenConnectionString()
    {
        var setting = await ResolveAsync("ConnectionStrings__db", (builder, worker) =>
        {
            builder.Configuration["ConnectionStrings:db"] = "Server=db;Password=s3cret";
            worker.WithReference(builder.AddConnectionString("db"));
        });

        Assert.Equal(new PackageSetting("ConnectionStrings__db", Secret: true), setting);
    }

    [Fact]
    public async Task ResolveAsync_ShouldReturnTheExpression_WhenGivenEndpointOfAnotherResource()
    {
        var setting = await ResolveAsync("BaseUrl", (builder, worker) =>
        {
            var api = builder.AddProject<TestProject>("api").WithHttpEndpoint(name: "web");
            worker.WithEnvironment("BaseUrl", api.GetEndpoint("web"));
        });

        Assert.Equal(new PackageSetting("BaseUrl", Expression: "{api.bindings.web.url}"), setting);
    }
}
