using Handoff.Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var greeting = builder.AddParameter("greeting");
var database = builder.AddConnectionString("sampledb");

builder.AddProject<Projects.Sample_Worker>("worker")
    .WithEnvironment("Sample__Greeting", greeting)
    .WithReference(database)
    .PublishAsWindowsService(@"C:\Program Files\HandoffSampleWorker", service =>
    {
        service.ServiceName = "HandoffSampleWorker";
        service.DisplayName = "Handoff Sample Worker";
        service.Description = "Sample worker installed from a Handoff package.";
        service.StartMode = WindowsServiceStartMode.DelayedAutomatic;
    });

builder.Build().Run();
