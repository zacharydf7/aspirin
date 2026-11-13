# Getting started

This walkthrough packages a worker project as a Windows service and installs it on your own machine. It takes
about ten minutes.

You need the .NET 10 SDK and an Aspire 13.6 AppHost. Installing the package needs Windows and an elevated Windows
PowerShell session. Building it works on Linux too.

## Add Aspirin to the AppHost

Aspirin isn't on NuGet yet, so reference it from source. Clone the repository into your solution folder:

```bash
git clone https://github.com/zacharydf7/aspirin.git
```

Then add a project reference to the AppHost. `IsAspireProjectResource="false"` tells Aspire that this is a library
and not one of your app's projects:

```xml
<ItemGroup>
  <ProjectReference Include="..\aspirin\src\Aspirin.Hosting\Aspirin.Hosting.csproj" IsAspireProjectResource="false" />
</ItemGroup>
```

## Let the project run as a service

A Windows service has to tell Windows when it has started and stopped. For a .NET worker, add the
`Microsoft.Extensions.Hosting.WindowsServices` package and call `AddWindowsService()`:

[!code-csharp[](../samples/Sample.Worker/Program.cs)]

The call does nothing when the project runs outside a service, so local runs are unchanged.

## Describe the service

In the AppHost, call `PublishAsWindowsService` on the project and give it the folder to install into:

[!code-csharp[](../samples/Sample.AppHost/AppHost.cs)]

Only the install folder is required. The service name defaults to the resource name, and the account to
LocalSystem. [WindowsServiceOptions](xref:Aspirin.Hosting.WindowsServiceOptions) lists the rest.

Running the AppHost locally doesn't change. The package is only built when the AppHost is published.

## Build the package

Publish the AppHost from its folder. The sample has one parameter, `greeting`, which takes its value from an
environment variable:

```bash
export ASPIRE_INTERACTIVITY_ENABLED=false
export Parameters__greeting="Hello from the build"
dotnet run -- --operation publish --step publish --output-path artifacts --clear-cache true
```

In PowerShell, set the two variables with `$env:ASPIRE_INTERACTIVITY_ENABLED = 'false'` and
`$env:Parameters__greeting = 'Hello from the build'`.

[Building in a pipeline](guides/pipelines.md) explains what the two settings on that command are for.

## Look inside

The package is the `artifacts/aspirin` folder:

```text
aspirin/
  package.json        the services, where they are installed and their settings
  checksums.sha256    the SHA-256 hash of every other file
  install.ps1         installs or updates the services
  rollback.ps1        undoes the most recent install
  common.ps1          functions the two scripts share
  README.txt          instructions for the person installing
  resources/worker/app/   the published project
```

`package.json` says what will be installed. Here is the part for the worker, with each setting on one line:

```json
{
  "name": "worker",
  "source": "resources/worker/app",
  "version": "1.2.3+a8ae002eb25ab17369dd50a8d4c1eb777196eb98",
  "windowsService": {
    "serviceName": "AspirinSampleWorker",
    "displayName": "Aspirin Sample Worker",
    "description": "Sample worker installed from an Aspirin package.",
    "executable": "Sample.Worker.exe",
    "installPath": "C:\\Program Files\\AspirinSampleWorker",
    "backupPath": "C:\\Program Files\\AspirinSampleWorker.backups",
    "startMode": "delayedAutomatic",
    "account": null
  },
  "settings": [
    { "name": "ConnectionStrings__sampledb", "value": null, "secret": true, "expression": null },
    { "name": "OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY", "value": "in_memory", "secret": false, "expression": null },
    { "name": "Sample__Greeting", "value": "Hello from the build", "secret": false, "expression": null }
  ]
}
```

The greeting came from the parameter. The connection string is a secret, so the package carries its name and not
its value. [Settings and secrets](guides/settings.md) covers both.

## Check the plan

In the package folder, ask the installer what it would do:

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1 -WhatIf
```

It stops, because the connection string has no value:

```text
ERROR: These settings have no value in the package. Supply them with -SettingsFile:
    worker: ConnectionStrings__sampledb
Nothing was changed.
```

Put the value in a JSON file outside the package folder, for example `C:\secure\settings.json`:

```json
{ "worker": { "ConnectionStrings__sampledb": "Server=sql01;Database=Sample;Integrated Security=true" } }
```

Then ask again:

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1 -WhatIf -SettingsFile C:\secure\settings.json
```

```text
The package passed its integrity check. Nothing will be changed.
[worker] Would create the Windows service 'AspirinSampleWorker' (version 1.2.3+a8ae002eb25ab17369dd50a8d4c1eb777196eb98)
    Install to:  C:\Program Files\AspirinSampleWorker
    Back up to:  C:\Program Files\AspirinSampleWorker.backups
    Run as:      LocalSystem
    Start mode:  delayedAutomatic
    Settings:    ConnectionStrings__sampledb, OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY, Sample__Greeting
```

The plan names the settings but never prints their values.

## Install it

Open Windows PowerShell as an administrator and run the same command without `-WhatIf`:

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1 -SettingsFile C:\secure\settings.json
```

The service is now installed and running. Run the command again and the installer updates the same service instead
of creating a second one.

`rollback.ps1` undoes the most recent install. After a first install it removes the service. After an update it
returns the service to the previous version:

```powershell
powershell -ExecutionPolicy Bypass -File rollback.ps1
```

## Clean up

There is no uninstall script yet. To remove the sample service and its folders by hand:

```powershell
sc.exe stop AspirinSampleWorker
sc.exe delete AspirinSampleWorker
Remove-Item 'C:\Program Files\AspirinSampleWorker', 'C:\Program Files\AspirinSampleWorker.backups' -Recurse
```

## Next steps

- [Settings and secrets](guides/settings.md): where each setting's value comes from.
- [Building in a pipeline](guides/pipelines.md): Bitbucket Pipelines and GitHub Actions.
- [Installing a package](guides/installing.md): every step the installer takes.
- [What isn't handled yet](reference/limitations.md): read this before relying on Aspirin for a real server.
