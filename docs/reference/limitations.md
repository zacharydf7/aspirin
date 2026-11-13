# What isn't handled yet

Aspirin is a preview. It packages and installs Windows services, and these are the gaps to know about before you
rely on it. The ones that are planned work are tracked on [GitHub](https://github.com/zacharydf7/aspirin/issues).

## Not built

- **IIS sites.** Only Windows services can be packaged. A web project that runs under IIS needs its site and
  application pool set up some other way.
- **Uninstalling.** There is no script that removes a service. A rollback removes a service only when the last
  install created it.
- **A single archive.** The package is a folder. Zip it yourself if you need one file to move.
- **NuGet.** The library has to be referenced from source.

## Service accounts

- **The "Log on as a service" right isn't granted.** An account without it fails when the service starts.
- **The password isn't checked before the first change.** A wrong password is found when the service starts, after
  its files were replaced. Roll back, then install again with the right one.
- **Group managed service accounts have not been tried.**
- **An earlier account keeps its read access.** When an install changes the account, the previous account's
  permission on the install folder is not removed.
- **A rollback doesn't restore the account**, because its password isn't kept.

## Secrets

- **Settings are stored in the service's registry key, which local users can read.** Treat a secret stored there
  like one stored in a configuration file on the server.
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
