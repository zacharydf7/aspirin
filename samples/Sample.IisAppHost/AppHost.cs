using Aspirin.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var greeting = builder.AddParameter("greeting");

builder.AddProject<Projects.Sample_Web>("web")
    .WithEnvironment("Sample__Greeting", greeting)
    .PublishAsIisSite(@"C:\inetpub\AspirinSampleWeb", site =>
    {
        site.SiteName = "Aspirin Sample Web";
        site.Bindings.Add(IisBinding.Http(18091, "aspirin-sample.test"));
        site.Bindings.Add(IisBinding.Https(18443, "aspirin-sample.test", certificateSubject: "CN=aspirin-sample.test"));
        site.HealthCheckPath = "/health";
    });

builder.Build().Run();
