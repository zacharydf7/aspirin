# Folder permissions

A Windows service often runs with more rights than the people signed in to the server. If one of them can change
the service's files, they can run their own code with the service's rights. The installer closes that gap for the
folders it creates and warns about the ones it didn't.

## Folders the installer creates

When an install folder, a backup folder, or a folder above one doesn't exist, the installer creates it so that:

- Only administrators and SYSTEM can change it.
- It doesn't inherit permissions from the folder above.

Each install also gives the service account permission to read and run the files in the install folder. LocalSystem
needs no grant, because SYSTEM already has full control.

The backup folder gets no grant for the service account. A backup holds the service's settings, which can include
secrets.

## A service can't write to its install folder

Unless it runs as LocalSystem, a service has read access to an install folder the installer created and nothing
more. Write logs and data somewhere else, and give the service account access to that folder yourself.

## Folders that already exist

The installer leaves the permissions of a folder it didn't create as they are, apart from adding the service
account's read access to the install folder and, when the account changes, removing the read access it gave the
earlier account. Replacing them could break something that depends on them. Instead it
warns when an account other than administrators owns such a folder or can change it:

```text
WARNING: Accounts other than administrators own or can change these folders:
    C:\Services (NT AUTHORITY\Authenticated Users)
The installer left their permissions unchanged. If any of these accounts shouldn't be able to change the service, fix the folders' permissions.
```

The check covers the folders above the install and backup folders too, because whoever can rename a folder
controls everything below it. For a folder above, only the rights to delete it, rename it or change its permissions
count. Being able to add an entry, which every user can do in `C:\ProgramData`, doesn't.

## Choosing a location

Where a folder sits decides the permissions it starts with:

| Location | A folder created there by something else |
| --- | --- |
| `C:\Program Files` | Starts protected: only administrators can change it. |
| `C:\ProgramData` | Users can add files to it but can't change what others put there. |
| Directly under `C:\` | Any signed-in user can change it. |

The installer protects whatever it creates in any of them. The difference only matters for a folder that exists
before the first install, such as `C:\Services` made by hand.
