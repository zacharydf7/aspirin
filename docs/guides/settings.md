# Settings and secrets

A setting is an environment variable the service runs with. You don't list settings for Aspirin. It takes them
from the environment Aspire would give the project, as set by the same `WithEnvironment` and `WithReference` calls
that configure the project when you run the AppHost locally.

## Where a value comes from

Each setting ends up in `package.json` in one of three ways:

| The value comes from | In the package |
| --- | --- |
| Text, or a parameter that isn't secret | The value |
| A secret parameter or a connection string | The name only, marked `secret`. With `IncludeSecretValues`, the value too, still marked `secret` |
| Something that only exists at run time, such as another project's endpoint | The name and the Aspire expression |

Here is one of each:

```csharp
var mode = builder.AddParameter("mode");
var apiKey = builder.AddParameter("api-key", secret: true);
var api = builder.AddProject<Projects.Api>("api");

builder.AddProject<Projects.Worker>("worker")
    .WithEnvironment("Worker__Mode", mode)                      // the value
    .WithEnvironment("Worker__ApiKey", apiKey)                  // the name only
    .WithReference(builder.AddConnectionString("jobs"))         // the name only: ConnectionStrings__jobs
    .WithEnvironment("Worker__ApiUrl", api.GetEndpoint("http")) // the name and {api.bindings.http.url}
    .PublishAsWindowsService(@"C:\Program Files\Worker");
```

A double underscore in a name becomes a level in .NET configuration, so `Worker__Mode` is read as `Worker:Mode`.
A setting overrides the same key in `appsettings.json`, because environment variables are read after it.

## Giving parameters their values

A parameter takes its value from configuration when the package is built. The simplest source is an environment
variable named `Parameters__<name>`:

```bash
export Parameters__mode="batch"
```

A pipeline that already has a variable under another name doesn't have to rename it. Bind the parameter to that
name instead:

```csharp
var rabbitHost = builder.AddParameterFromConfiguration("rabbitmq-host", "RABBITMQ_HOST");
```

Either way the value stays outside source control. Changing it means changing the variable and building again.

A parameter with no value fails the build, unless it is a secret the package doesn't carry. Aspire logs that such
a secret is missing, and the package is built without it.

## Supplying values at install time

A setting with no value in the package must be supplied when the package is installed. Put the values in a JSON
file, keyed by resource name and then setting name, and pass it to the installer:

```json
{
  "worker": {
    "ConnectionStrings__jobs": "Server=sql01;Database=Jobs;Integrated Security=true",
    "Worker__ApiUrl": "https://api.internal.example:8443"
  }
}
```

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1 -SettingsFile C:\secure\settings.json
```

The same file can override a value the package carries, or add a setting the package doesn't know about. The
installer refuses a file that names a resource the package doesn't contain, which catches a misspelled name.

Keep the file outside the package folder. It holds secrets, and it isn't part of what was built and shipped.

## Carrying secrets in the package

When the people who install a package are allowed to know its secrets, the package can carry them, and the
installer then needs no settings file for them:

```csharp
builder.AddProject<Projects.Worker>("worker")
    .WithReference(builder.AddConnectionString("jobs"))
    .PublishAsWindowsService(@"C:\Program Files\Worker", service => service.IncludeSecretValues = true);
```

With this on:

- **Every secret needs a value when the package is built.** A missing one fails the build.
- **Anyone who can read the package can read the secrets.** They are plain text in `package.json`. Keep the package
  where only those people can reach it.
- **The installer still doesn't print them.** `-WhatIf` lists setting names only.

A setting that only exists at run time still has to come from a settings file.

## Where settings live on the server

The installer writes the settings to the service's `Environment` value in the registry, under
`HKLM\SYSTEM\CurrentControlSet\Services\<service name>`. Windows gives those variables to the service's process and
to nothing else.

Local users can read that registry key. Treat a secret stored there like one stored in a configuration file on the
server.
