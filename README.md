# Aspirin

Aspirin builds an installation package for Windows services from an Aspire AppHost. A build machine creates the
package, and someone with access to the server installs it. The build machine never needs access to the server,
and neither Docker nor the Aspire CLI is involved.

## Describe the service

Reference `Aspirin.Hosting` from the AppHost and mark the project:

```csharp
builder.AddProject<Projects.Worker>("worker")
    .WithEnvironment("Worker__Greeting", builder.AddParameter("greeting"))
    .WithReference(builder.AddConnectionString("jobs"))
    .PublishAsWindowsService(@"C:\Program Files\Worker", service =>
    {
        service.ServiceName = "Worker";
        service.DisplayName = "Worker";
        service.Account = @"CORP\svc-worker";
        service.StartMode = WindowsServiceStartMode.DelayedAutomatic;
    });
```

The project must be able to run as a service, which for a .NET worker means calling `AddWindowsService()`.
Running the AppHost locally is unchanged: the package is only built when the AppHost is published.

## Build the package

```bash
export ASPIRE_INTERACTIVITY_ENABLED=false
dotnet run --project path/to/AppHost -- --operation publish --step publish --output-path artifacts --clear-cache true
```

The package is written to `artifacts/aspirin`.

Both settings matter on a build machine:

- Without `ASPIRE_INTERACTIVITY_ENABLED=false`, a parameter with no value makes the AppHost wait for input
  instead of failing.
- Without `--clear-cache true`, Aspire saves parameter values, secrets included, under `~/.aspire/deployments`.
  On the next build a saved value wins over a new one passed in the environment.

Give parameters their values as environment variables: `Parameters__greeting` for a parameter named `greeting`,
and `ConnectionStrings__jobs` for a connection string named `jobs`. Unless the package carries secret values (see
Settings below), a secret needs no value at this point: Aspire logs that it is missing, and the package is built
without it. Add `--environment <name>` to build for an environment other than Production.

### In Bitbucket Pipelines

```yaml
- step:
    name: Build the installation package
    image: mcr.microsoft.com/dotnet/sdk:10.0
    script:
      - export ASPIRE_INTERACTIVITY_ENABLED=false
      - dotnet run --project path/to/AppHost -- --operation publish --step publish --output-path artifacts --clear-cache true
    artifacts:
      - artifacts/aspirin/**
```

A self-hosted Windows runner needs the .NET SDK installed instead of the image. Its script lines are PowerShell, so
the first one becomes `$env:ASPIRE_INTERACTIVITY_ENABLED = 'false'`.

Pipeline variables reach the AppHost as environment variables. A variable doesn't have to be renamed to
`Parameters__<name>`: a parameter can read it under the name it already has, and the value stays in Bitbucket,
where it can be changed without a commit:

```csharp
var rabbitHost = builder.AddParameterFromConfiguration("rabbitmq-host", "RABBITMQ_HOST");
```

This step has not been run on Bitbucket yet.

## What the package holds

```text
aspirin/
  package.json        the services, where they are installed and their settings
  checksums.sha256    the SHA-256 hash of every other file
  install.ps1         installs or updates the services
  rollback.ps1        undoes the most recent install
  common.ps1          functions the two scripts share
  README.txt          instructions for the person installing
  resources/<name>/app/   the published project
```

Apart from the published files, the scripts read only `package.json` and `checksums.sha256`, so another build tool
can produce a package by writing those two files and the `resources` folder next to the scripts. `schemaVersion`
lets the scripts refuse a `package.json` in a format they don't know.

### Settings

A setting is an environment variable the service runs with. Aspirin takes them from the environment Aspire would
give the project, and writes each one to `package.json` in one of three ways:

| The value comes from | In the package |
| --- | --- |
| Text, or a parameter that isn't secret | The value |
| A secret parameter or a connection string | The name only, marked `secret`. With `IncludeSecretValues`, the value too, still marked `secret` |
| Something that only exists at run time, such as another project's endpoint | The name and the Aspire expression |

Settings without a value must be supplied when the package is installed, in a JSON file passed to
`install.ps1 -SettingsFile`. The same file can override a value the package carries.

```json
{ "worker": { "ConnectionStrings__jobs": "Server=sql01;Database=Jobs;Integrated Security=true" } }
```

For the package to carry secret values too, set `service.IncludeSecretValues = true`. Each secret then needs a value
when the package is built, and the package must be kept where only the people who may know those secrets can read
it. Unless a setting only exists at run time, the package can then be installed without a settings file.

## Install the package

`README.txt` in the package has the steps. In an elevated Windows PowerShell session:

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1 -WhatIf          # check the package and list the plan
powershell -ExecutionPolicy Bypass -File install.ps1 -SettingsFile C:\secure\settings.json
```

The installer changes nothing until the package has passed its checksums and every setting has a value. Then, for
each service, it:

1. Copies the installed files and the service's configuration to a new backup folder.
2. Stops the service, if it exists.
3. Replaces the installed files.
4. Creates the service or updates the existing one, so running the installer again never creates a second service.
5. Starts the service, unless its start mode is manual, and checks that it is still running a few seconds later.
6. Removes all but the newest three backups. `-KeepBackups` changes how many are kept.

A password is never stored in the package. When a service runs as a user account, the installer takes the password
from `-Credential`, or, without it, asks for the password when the service is created or its account changes.

When the installer creates an install or backup folder, or a folder above one, only administrators and SYSTEM can
change it. Every install gives the service account read access to the install folder. Unless it runs as
LocalSystem, a service therefore can't write to its own install folder, so logs and data belong elsewhere. A folder
that already exists keeps its permissions, and the installer warns when an account other than administrators owns
it or can change it. That includes the folders above the install and backup folders, because whoever can rename a
folder controls everything below it. A folder created some other way under `C:\Program Files` starts out protected;
one created directly under `C:\` does not.

`rollback.ps1` undoes the most recent install. Each service the install changed is restored from the backup taken
just before the change, and a service the install created is removed. A service the install did not reach, because
it failed earlier, is left alone. The service account is not restored, because its password isn't kept: if the
install changed the account, set it back in the Services console. The backup records which program the service
runs, so `rollback.ps1` stops when an account other than administrators could have changed the backup. After
checking `backup.json`, run it again with `-Force`.

Each service needs a name, an install folder and a backup folder of its own. Publishing fails when two services
in one AppHost share any of them, or when one service's folder is inside another's.

## Not handled yet

- IIS sites.
- Granting the service account the "Log on as a service" right. An account without it fails when the service starts.
- Checking the service account's password before the first change. A wrong password is found when the service
  starts, after its files were replaced.
- Group managed service accounts have not been tried.
- Settings are stored in the service's registry key, which local users can read. Treat a secret stored there
  like one stored in a configuration file on the server.

## Tests

```bash
dotnet test
```

The tests publish the sample AppHost and run the installer against the package. The tests that install the sample
service run only in an elevated session on Windows and are skipped otherwise. They install a service named
`AspirinSampleWorker` under `C:\Program Files` and remove it afterward.

GitHub Actions runs the tests on Linux and on Windows. The Windows runner is elevated, so that job includes the
tests that install the service. The Linux job shows that the package builds there.
