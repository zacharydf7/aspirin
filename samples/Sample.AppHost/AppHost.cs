using Aspirin.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var greeting = builder.AddParameter("greeting");
var database = builder.AddConnectionString("sampledb");

builder.AddProject<Projects.Sample_Worker>("worker")
    .WithEnvironment("Sample__Greeting", greeting)
    .WithReference(database)
    .PublishAsWindowsService(@"C:\Program Files\AspirinSampleWorker", service =>
    {
        service.ServiceName = "AspirinSampleWorker";
        service.DisplayName = "Aspirin Sample Worker";
        service.Description = "Sample worker installed from an Aspirin package.";
        service.StartMode = WindowsServiceStartMode.DelayedAutomatic;
    });

builder.Build().Run();
