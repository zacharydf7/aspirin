Installation package
====================

package.json lists the Windows services and IIS sites in this package and how each one is installed.
The scripts are plain PowerShell and can be read before they are run.

To see what would be done, without changing anything:

    powershell -ExecutionPolicy Bypass -File install.ps1 -WhatIf

    Run it as an administrator to also compare the sites with what IIS has now and to see which
    certificates they would use.

To install or update:

    1. Open Windows PowerShell as an administrator.
    2. Change to this folder.
    3. Run:  powershell -ExecutionPolicy Bypass -File install.ps1

    If the installer reports settings with no value, put them in a JSON file and pass it:

        powershell -ExecutionPolicy Bypass -File install.ps1 -SettingsFile C:\path\settings.json

    The file looks like this:

        { "<resource name>": { "<setting name>": "<value>" } }

    If a service or application pool runs as a user account, the installer asks for the password
    when it is created or its account changes.

    IIS sites need IIS 10 or later and the ASP.NET Core Module, which the .NET Hosting Bundle
    installs. Certificates for HTTPS must already be in the server's certificate store.

To roll back (when the installer tells you to, or when whoever sent the package asks you to):

    powershell -ExecutionPolicy Bypass -File rollback.ps1

    This undoes the most recent install. Each service and site the install changed is restored
    from the backup taken just before the change, and one the install created is removed.
    A service's account is not restored. If the install changed it, set it back in the
    Services console.

The installer verifies every file against checksums.sha256 before it changes anything, so
do not add, edit or remove files in this folder.
