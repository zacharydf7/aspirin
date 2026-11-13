# Moving build-time placeholders into AppHost parameters

Many pipelines fill in settings while they build: `appsettings.json` holds tokens such as `{SMTP_HOST}`, and a step
replaces each token with a pipeline variable before the project is published. An Aspirin package can't carry
settings that way, because it takes its settings from the AppHost. This guide moves each token into an AppHost
parameter that reads the same pipeline variable.

## Before

`appsettings.json` has a token for each value the pipeline supplies:

```json
{
  "Smtp": {
    "Host": "{SMTP_HOST}",
    "Password": "{SMTP_PASSWORD}"
  },
  "ConnectionStrings": {
    "Jobs": "{JOBS_DATABASE}"
  }
}
```

And a pipeline step replaces them, here in Bitbucket Pipelines:

```yaml
- step:
    name: Build
    script:
      - sed -i "s|{SMTP_HOST}|$SMTP_HOST|; s|{SMTP_PASSWORD}|$SMTP_PASSWORD|; s|{JOBS_DATABASE}|$JOBS_DATABASE|" src/Worker/appsettings.json
      - dotnet publish src/Worker --configuration Release --output out
```

This works, but it has costs. The password and the connection string end up in plain text in the published files
and in every artifact made from them. A variable that's missing is replaced with nothing, so the app starts with an
empty SMTP host and nobody is told.

## After

In the AppHost, declare a parameter for each token, bound to the variable that fills it today:

[!code-csharp[](../../samples/Placeholders.AppHost/AppHost.cs#parameters)]

Then give the parameters to the project as settings:

[!code-csharp[](../../samples/Placeholders.AppHost/AppHost.cs#project)]

A setting's name is its configuration path with `__` between the levels, so `Smtp__Host` is the value .NET
configuration reads as `Smtp:Host`. Settings are environment variables, which .NET reads after `appsettings.json`,
so they win over anything still in the file.

Finally, take the tokens out of `appsettings.json`, and swap the `sed` step for the package build from
[Building in a pipeline](pipelines.md). A value the package carries stays in its Bitbucket variable, and changing
it still only needs a new build. A secret can work differently, as the next section explains.

## Which values become secrets

A parameter declared with `secret: true` becomes a secret setting. Make a value secret if Bitbucket keeps it as a
secured variable, such as a password, connection string or API key.

Unlike a token, a secret doesn't have to be written into a build artifact. When the package is installed, it comes
from one of two places:

- **A settings file**, passed to the installer with `-SettingsFile`. The package carries only the secret's name,
  so the build doesn't use the secured variable at all: the value moves to the settings file, and changing it means
  changing that file. See [Supplying values at install time](settings.md#supplying-values-at-install-time).
- **The package itself**, with `IncludeSecretValues` set on the service or site. The build reads the secured
  variable as it did before, so this is the smaller change, but anyone who can read the package can read the
  secrets. See [Carrying secrets in the package](settings.md#carrying-secrets-in-the-package).

A parameter with no value when the package is built fails the build, unless it's a secret the package doesn't
carry. That turns a missing variable, which the token replacement left as an empty value, into an error the
pipeline reports.

## Running the AppHost locally

The parameters read the same configuration keys on a developer's machine, so give the AppHost local values, for
example with user secrets:

```bash
dotnet user-secrets set SMTP_HOST smtp.dev.local --project path/to/AppHost
```

Any value still missing is asked for in the Aspire dashboard when the AppHost starts.
