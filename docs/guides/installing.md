# Installing a package

This page is for the person who installs a package on a server. The package also carries a short `README.txt`
with the same commands.

The scripts are written for Windows PowerShell 5.1, which every supported version of Windows Server includes.
Installing needs an elevated session. Checking a package with `-WhatIf` does not.

## Check the package first

From the package folder:

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1 -WhatIf
```

`-WhatIf` checks the checksums and the settings, then lists what would be done, without changing anything. It
doesn't need an elevated session and never asks for a password, so a real install can still stop on either:

```text
The package passed its integrity check. Nothing will be changed.
[worker] Would create the Windows service 'AspirinSampleWorker' (version 1.2.3+a8ae002eb25ab17369dd50a8d4c1eb777196eb98)
    Install to:  C:\Program Files\AspirinSampleWorker
    Back up to:  C:\Program Files\AspirinSampleWorker.backups
    Run as:      LocalSystem
    Start mode:  delayedAutomatic
    Settings:    ConnectionStrings__sampledb, OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY, Sample__Greeting
```

"Would create" becomes "Would update" when the service already exists.

Only administrators can read the IIS configuration, so for an IIS site a `-WhatIf` without elevation says "Would
create or update" and names the certificate it would look for. Run it elevated to see whether the site exists,
which bindings would be removed, and which certificate each HTTPS binding would get.

## Install or update

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1
```

The same command installs a new service and updates an existing one. It takes these options:

| Option | What it does |
| --- | --- |
| `-SettingsFile <path>` | Supplies settings the package doesn't carry, or overrides ones it does. See [Settings and secrets](settings.md#supplying-values-at-install-time). |
| `-Credential <credential>` | The service account and its password, for a service that runs as a user account. |
| `-KeepBackups <number>` | How many backups to keep for each service. The default is 3. |
| `-WhatIf` | Checks the checksums and settings, and lists the plan without changing anything. |

## Before anything changes

The installer stops, with nothing changed, unless all of these hold:

- **Every file matches its checksum.** `checksums.sha256` lists the SHA-256 hash of every other file. A file that
  was changed, added or removed since the package was built fails the check.
- **Every setting has a value**, from the package or from the settings file.
- **The session is elevated.**
- **The server has what the IIS sites need**: IIS 10 or later, the ASP.NET Core Module, the certificates for HTTPS
  bindings, and no other site on the same addresses. See [IIS sites](iis-sites.md#what-the-server-needs).
- **Each user account has a password to use, it's right, and the account may log on the way it will run.** See
  [Service accounts](#service-accounts).

The checksums catch a package that was damaged or edited on the way to the server. They are not a signature:
someone who can rewrite the package can rewrite the checksums too.

## What the installer does

For each service, in order:

1. **Backs up** the installed files and the service's configuration to a new folder under the backup path.
2. **Stops** the service, if it exists, and waits up to a minute for it to stop.
3. **Replaces** the installed files with the ones in the package. Files that aren't in the package are removed.
4. **Creates** the service, or **updates** the existing one: its program, display name, description and start mode.
5. **Sets the account** the service runs as, and gives that account read access to the install folder. If the
   account changed, the earlier one loses the read access the installer gave it.
6. **Writes the settings** as the service's environment variables.
7. **Starts** the service and checks that it is still running three seconds later. A service whose start mode is
   manual is left stopped.
8. **Removes old backups**, keeping the newest three unless `-KeepBackups` says otherwise.

A service is only changed after its backup is complete, so the install can be [rolled back](rolling-back.md).
An IIS site goes through similar steps, which [IIS sites](iis-sites.md#what-the-installer-does) lists.
Folders the installer has to create are locked down first, as [Folder permissions](folder-permissions.md) describes.

## Service accounts

A service runs as LocalSystem unless the package names another account.

Built-in accounts such as `NT AUTHORITY\NetworkService` and virtual accounts need no password. Group managed
service accounts are treated the same way, but that has not been tried. For a user account, the installer needs the
password when the service is created or its account changes, and it opens a credential prompt to ask for it.

Before it changes anything, the installer checks each password it was given: against the domain for an account such
as `CORP\svc-worker` or `svc-worker@corp.local`, and against the server itself for a local account such as
`.\svc-worker`. An account written as `name@suffix` is looked up through the server's own domain, which finds it
anywhere in the forest. The install stops with nothing changed if the password is wrong, or if the account doesn't
exist, is locked out or is disabled. If the domain can't be reached, the check can't be made, and the install stops
too.

It then checks that the account may log on the way it will run: as a service, with the "Log on as a service" right,
or as an IIS application pool, with "Log on as a batch job". It does this by logging the account on, the same way
Windows will when the service or pool starts, so the check takes in the account's groups and any policy that denies
the right. Each check shows in the Security event log as a logon.
For a pool, the account isn't checked on its own when the `IIS_IUSRS` group has the right, because IIS adds that
group to every pool's identity. The installer doesn't grant a missing right, because Group Policy usually manages
these rights and would undo it. Instead, it stops with nothing changed, and its message asks you to have an
administrator grant the right.

To supply the password yourself, pass a credential. A credential can't cross from one PowerShell process to
another, so run the script in the session that holds it. The installer then sets the password on every run:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\install.ps1 -Credential (Get-Credential CORP\svc-worker)
```

The password is never stored in the package or in a service's backup. An IIS application pool works the same way,
except that IIS keeps the password with the pool, so a site's backup can hold it.

## When an install fails

The installer stops at the first failure and says which of two states the server is in:

- **"Nothing was changed."** A check failed before any service was touched. Fix the cause and run it again.
- **"The installation stopped partway. Run rollback.ps1 to undo it: services this run did not reach are left
  alone."** At least one service was changed. [Roll back](rolling-back.md), or fix the cause and run the installer
  again. Running it again backs up the server as it is now, so roll back first if you may want the earlier version.

The most common failure after files are replaced is a service that stops right after it starts. The Application
event log usually says why.
