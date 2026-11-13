# What isn't handled yet

Aspirin is a preview. It packages and installs Windows services and IIS sites, and these are the gaps to know
about before you rely on it. The ones that are planned work are tracked on
[GitHub](https://github.com/zacharydf7/aspirin/issues).

## Not built

- **Applications under an existing IIS site.** A project is packaged as a whole site with its own pool. It can't be
  added as an application or virtual directory under a site the package doesn't own.
- **Uninstalling.** There is no script that removes a service or site. A rollback removes one only when the last
  install created it.
- **A single archive.** The package is a folder. Zip it yourself if you need one file to move.
- **NuGet.** The library has to be referenced from source.

## Service accounts

- **The "Log on as a service" right isn't granted.** The installer checks it before it changes anything and stops if
  it's missing, but an administrator or Group Policy has to grant it. It's checked only on runs that set the
  account's password, so a run that keeps the same account without `-Credential` doesn't check it.
- **Group managed service accounts have not been tried.**
- **An earlier account keeps its read access.** When an install changes the account, the previous account's
  permission on the install folder is not removed.
- **A rollback doesn't restore the account**, because its password isn't kept.

## IIS sites

- **A policy that denies a pool account the "Log on as a batch job" right isn't always found.** When `IIS_IUSRS`
  has the right, as it does by default, the installer doesn't check the account itself, because IIS adds that group
  to every pool's identity.
- **Nothing the sites need is installed.** IIS, the ASP.NET Core Module and Application Initialization must already
  be on the server.

## Secrets

- **Settings are stored in the service's registry key, which local users can read.** Treat a secret stored there
  like one stored in a configuration file on the server. A site's settings are kept in IIS's configuration, which
  only administrators and the site's pool can read.
- **A site's backup can hold its pool account's password.** The backup folder is for administrators only.
- **A package that carries secrets holds them as plain text.** Keep it where only the people allowed to know them
  can read it.

## Not yet verified

- **Bitbucket Pipelines.** The build is one `dotnet run` and is tested on GitHub's Linux and Windows runners, but
  the Bitbucket step in [Building in a pipeline](../guides/pipelines.md) has not been run there.
- **Runtimes other than `win-x64`.**
- **PowerShell 7.** The scripts are written for and tested with Windows PowerShell 5.1.

## Depends on Aspire

Aspirin is built on Aspire's pipeline APIs, which Aspire marks as experimental and may change in any release. It
is built and tested against Aspire 13.6.5.
