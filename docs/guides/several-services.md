# Several services in one package

Every project marked with `PublishAsWindowsService` goes into the same package, and one run of the installer
installs them all:

```csharp
builder.AddProject<Projects.Intake>("intake")
    .PublishAsWindowsService(@"C:\Program Files\Contoso\Intake");

builder.AddProject<Projects.Billing>("billing")
    .PublishAsWindowsService(@"C:\Program Files\Contoso\Billing");
```

Each project is published into its own folder under `resources/`, and `package.json` lists them all.

## Order

Services are installed in the order the AppHost declares them. An AppHost can only refer to a resource it has
already declared, so whatever a service depends on is installed before it. A rollback goes through them in the
reverse order.

## Each service needs its own name and folders

Two services can't share a service name, an install folder or a backup folder, and one service's folder can't be
inside another's. The installer mirrors each install folder and prunes each backup folder, so shared folders would
overwrite each other. Publishing fails with a message that names the two resources:

```text
The resources 'intake' and 'billing' use the folders 'C:\Program Files\Contoso' and 'C:\Program Files\Contoso\Billing'. Each service needs install and backup folders of its own, with none inside another.
```

Sibling folders under a shared parent, as in the example at the top, are fine.

The backup folder defaults to the install folder with `.backups` added, which is always separate. Set
`service.BackupPath` to keep backups elsewhere.

## When one service fails

The installer stops at the first service that fails. Services it had already installed stay installed, and the
ones after it are not touched. [Rolling back](rolling-back.md) then undoes exactly the services that run changed.

## Different settings per service

A settings file is keyed by resource name, so each service gets its own values:

```json
{
  "intake": { "ConnectionStrings__jobs": "Server=sql01;Database=Jobs;Integrated Security=true" },
  "billing": { "Billing__ApiKey": "..." }
}
```
