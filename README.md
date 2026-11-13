<h1>
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/wordmark-light.svg">
    <img src="assets/wordmark-dark.svg" alt="Aspirin" width="320">
  </picture>
</h1>

Aspirin builds an installation package for Windows services and IIS sites from an Aspire AppHost. A build machine creates the
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

Aspirin is a preview, and it isn't on NuGet yet. Read
[What isn't handled yet](https://zacharydf7.github.io/aspirin/reference/limitations.html) before relying on it.

## Tests

```bash
dotnet test
```

The tests publish the sample AppHosts and run the installer against the packages. The tests that install something
run only in an elevated session on Windows and are skipped otherwise:

- The service tests install a service named `AspirinSampleWorker` under `C:\Program Files`.
- The site tests also need IIS and the ASP.NET Core Module. They install a site named `Aspirin Sample Web` under
  `C:\inetpub` on ports 18091 and 18443, with a self-signed certificate for `aspirin-sample.test` in the machine's
  certificate store.

Both remove what they installed afterward.

GitHub Actions runs the tests on Linux and on Windows. The Windows runner is elevated and has IIS, and the job
installs the .NET Hosting Bundle, so it includes the tests that install the service and the site. The Linux job
shows that the packages build there.

The documentation site is in `docs`. Build it with `dotnet tool restore` and then `dotnet docfx docs/docfx.json`.
