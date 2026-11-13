# Building in a pipeline

Building a package is one command, run from the AppHost's folder or with `--project`:

```bash
export ASPIRE_INTERACTIVITY_ENABLED=false
dotnet run --project path/to/AppHost -- --operation publish --step publish --output-path artifacts --clear-cache true
```

The package is written to `artifacts/aspirin`. The build needs the .NET SDK and nothing else: no Docker, no Aspire
CLI, and no connection to the server the package is for.

## The two settings that matter

Both exist because of how Aspire behaves when it isn't run by a person:

- **`ASPIRE_INTERACTIVITY_ENABLED=false`** makes a parameter with no value fail the build. Without it the AppHost
  waits for someone to type the value, and the pipeline step hangs until it times out.
- **`--clear-cache true`** stops Aspire from saving parameter values, secrets included, under
  `~/.aspire/deployments` on the build machine. Without it, a value saved by an earlier build wins over a new one
  from the environment, so a changed pipeline variable would have no effect on a runner that keeps its disk.

Add `--environment <name>` to build for an environment other than Production. The name is recorded in
`package.json`.

## Exit codes

A failure while building the package exits with a code other than zero, so the pipeline step fails. Aspirin sets
that code for its own step, because an AppHost started with `dotnet run` otherwise exits with zero even when
publishing failed. A failure in another publisher's step in the same AppHost can still exit with zero.

## Bitbucket Pipelines

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

A self-hosted Windows runner doesn't use the image, so it needs the .NET SDK installed. Its script lines are
PowerShell, so the first one becomes `$env:ASPIRE_INTERACTIVITY_ENABLED = 'false'`.

Repository and deployment variables reach the AppHost as environment variables. A parameter can read such a
variable without renaming it, so its value stays in Bitbucket, where it can be changed without a commit. See
[Giving parameters their values](settings.md#giving-parameters-their-values).

This step has not been run on Bitbucket yet.

## GitHub Actions

```yaml
- uses: actions/setup-dotnet@v6
  with:
    dotnet-version: 10.0.x
- run: dotnet run --project path/to/AppHost -- --operation publish --step publish --output-path artifacts --clear-cache true
  env:
    ASPIRE_INTERACTIVITY_ENABLED: false
- uses: actions/upload-artifact@v7
  with:
    name: installation-package
    path: artifacts/aspirin
```

Aspirin's own tests run the same `dotnet run` command on both `ubuntu-latest` and `windows-latest`.

## Building on Linux for Windows

The package can be built on Linux. Each project is published for `win-x64` by default, and self-contained, so the
server doesn't need .NET installed. Change either on the service:

```csharp
.PublishAsWindowsService(@"C:\Program Files\Worker", service =>
{
    service.RuntimeIdentifier = "win-arm64";
    service.SelfContained = false;
});
```
