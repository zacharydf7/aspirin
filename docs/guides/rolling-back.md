# Rolling back

`rollback.ps1` undoes the most recent run of `install.ps1`. Run it from the package folder in an elevated session:

```powershell
powershell -ExecutionPolicy Bypass -File rollback.ps1
```

## What comes back

Before the installer changes a service, it saves a backup: a copy of the installed files, and a record of the
service's program, display name, description, start mode and environment variables. Rolling back restores all of
that for each service the install changed.

What happens to a service depends on its state before the install:

| Before the install | After the rollback |
| --- | --- |
| The service existed | Its files and configuration are as they were. It is started if it was running. |
| The service didn't exist | The service and its install folder are removed. |
| The install never reached it | Nothing changes. |

Services are rolled back in the reverse of the order they were installed in.

## "The most recent run" is exact

Every backup an install takes is named after that run, with a UTC timestamp such as `20261113-014500-123`. The
rollback finds the newest run across all the services in the package and restores only the backups that carry its
name.

That matters when an install fails partway. Suppose a package holds three services and the second one fails to
start. The first two were changed and the third wasn't. Rolling back restores the first two and leaves the third
alone, instead of returning it to some older backup.

Running the rollback twice restores the same backups again. It doesn't step back through earlier installs.

## What doesn't come back

- **The service account.** Its password isn't kept, so the rollback can't set the earlier account. It warns when
  the account differs from the one before the install. Change it back in the Services console.
- **Anything outside the install folder.** Databases, files the service wrote elsewhere, and settings other
  software holds are not part of a backup.

## Backups someone else could have changed

A backup records which program the service runs. If an ordinary user could edit that record, they could make the
rollback point the service at a program of their choosing. The rollback therefore stops when an account other than
administrators owns the backup folder, or a folder above it, or can change it:

```text
ERROR: Accounts other than administrators own or can change these folders on the path to the backups:
    C:\Backups\Worker (BUILTIN\Users)
Check backup.json in each service's '20261113-014500-123' backup folder, then run rollback.ps1 -Force.
```

Open `backup.json`, confirm that `binaryPath` is the program you expect, then run the rollback with `-Force`.
Folders the installer created don't trigger this. See [Folder permissions](folder-permissions.md).

## How many backups are kept

Each install keeps the newest three backups of a service and removes older ones. `install.ps1 -KeepBackups 5`
keeps five. Only the newest run can be rolled back by the script, but the older folders hold the files if you need
to restore one by hand.
