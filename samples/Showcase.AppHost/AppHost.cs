// The documentation's landing page shows this file in pieces. Each region is one piece: a region named after a
// feature appears when the reader switches that feature on, and the others always appear.
#region base-start
using Aspirin.Hosting;

var builder = DistributedApplication.CreateBuilder(args);
#endregion
#region settings-declare
var greeting = builder.AddParameter("greeting");
var jobs = builder.AddConnectionString("jobs");
#endregion
#region base-worker
builder.AddProject<Projects.Worker>("worker")
#endregion
#region settings-use
    .WithEnvironment("Worker__Greeting", greeting)
    .WithReference(jobs)
#endregion
#region base-service
    .PublishAsWindowsService(@"C:\Program Files\Contoso\Worker", service =>
    {
        service.DisplayName = "Contoso Worker";
#endregion
#region account
        service.Account = @"CORP\svc-worker";
        service.StartMode = WindowsServiceStartMode.DelayedAutomatic;
#endregion
#region secrets
        service.IncludeSecretValues = true;
#endregion
#region base-end
    });
#endregion
#region second
builder.AddProject<Projects.Reports>("reports")
    .PublishAsWindowsService(@"C:\Program Files\Contoso\Reports");
#endregion
#region base-run
builder.Build().Run();
#endregion
