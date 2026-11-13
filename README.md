# Aspirin

Aspirin builds an installation package for Windows services from an Aspire AppHost. A build machine creates the
package, and someone with access to the server installs it. The build machine never needs access to the server,
and neither Docker nor the Aspire CLI is involved.

The documentation, with a walkthrough and guides, is at <https://zacharydf7.github.io/aspirin/>.

## A quick look

Mark a project in the AppHost:

```csharp
builder.AddProject<Projects.Worker>("worker")
    .WithEnvironment("Worker__Greeting", builder.AddParameter("greeting"))
    .WithReference(builder.AddConnectionString("jobs"))
    .PublishAsWindowsService(@"C:\Program Files\Worker", service =>
    {
        service.Account = @"CORP\svc-worker";
        service.StartMode = WindowsServiceStartMode.DelayedAutomatic;
    });
```

Build the package on any machine with the .NET SDK:

```bash
export ASPIRE_INTERACTIVITY_ENABLED=false
dotnet run --project path/to/AppHost -- --operation publish --step publish --output-path artifacts --clear-cache true
```

Then, on the server, in the `artifacts/aspirin` folder:

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1 -WhatIf   # check the package and list the plan
powershell -ExecutionPolicy Bypass -File install.ps1           # install or update
powershell -ExecutionPolicy Bypass -File rollback.ps1          # undo the most recent install
```

## Status

Aspirin is a preview. It isn't on NuGet yet, and it packages Windows services only. Read
[What isn't handled yet](https://zacharydf7.github.io/aspirin/reference/limitations.html) before relying on it.

## Tests

```bash
dotnet test
```

The tests publish the sample AppHost and run the installer against the package. The tests that install the sample
service run only in an elevated session on Windows and are skipped otherwise. They install a service named
`AspirinSampleWorker` under `C:\Program Files` and remove it afterward.

GitHub Actions runs the tests on Linux and on Windows. The Windows runner is elevated, so that job includes the
tests that install the service. The Linux job shows that the package builds there.

The documentation site is in `docs`. Build it with `dotnet tool restore` and then `dotnet docfx docs/docfx.json`.
