// The second service in the documentation's showcase. It only has to build.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService();

builder.Build().Run();
