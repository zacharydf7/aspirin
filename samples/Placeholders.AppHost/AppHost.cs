// The guide "Moving build-time placeholders into AppHost parameters" shows this file in pieces, one region each.
#region parameters
var builder = DistributedApplication.CreateBuilder(args);

var smtpHost = builder.AddParameterFromConfiguration("smtp-host", "SMTP_HOST");
var smtpPassword = builder.AddParameterFromConfiguration("smtp-password", "SMTP_PASSWORD", secret: true);
var jobsDatabase = builder.AddParameterFromConfiguration("jobs-database", "JOBS_DATABASE", secret: true);
#endregion
#region project
builder.AddProject<Projects.Worker>("worker")
    .WithEnvironment("Smtp__Host", smtpHost)
    .WithEnvironment("Smtp__Password", smtpPassword)
    .WithEnvironment("ConnectionStrings__Jobs", jobsDatabase)
    .PublishAsWindowsService(@"C:\Program Files\Contoso\Worker");

builder.Build().Run();
#endregion
