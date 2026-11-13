var builder = WebApplication.CreateBuilder(args);

// Lets the installer tests see what happens when an update can't start.
if (builder.Configuration.GetValue<bool>("Sample:FailOnStart"))
{
    throw new InvalidOperationException("Sample:FailOnStart is set.");
}

var app = builder.Build();
app.MapGet("/", (IConfiguration configuration) => configuration["Sample:Greeting"]);
app.MapGet("/health", () => "Healthy");
app.Run();
