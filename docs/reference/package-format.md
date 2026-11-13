# The package format

A package is a folder. Nothing in it is compiled or compressed except the published projects themselves, so it
can be inspected with a text editor.

```text
aspirin/
  package.json
  checksums.sha256
  install.ps1
  rollback.ps1
  common.ps1
  iis.ps1
  README.txt
  resources/
    <resource name>/
      app/
```

Apart from the published files, the scripts read only `package.json` and `checksums.sha256`. Another build tool
can produce a package by writing those two files and the `resources` folder next to the scripts.

## package.json

```json
{
  "schemaVersion": 2,
  "application": "Sample.AppHost",
  "environment": "Production",
  "resources": [
    {
      "name": "worker",
      "kind": "windowsService",
      "source": "resources/worker/app",
      "version": "1.2.3+a8ae002eb25ab17369dd50a8d4c1eb777196eb98",
      "installPath": "C:\\Program Files\\AspirinSampleWorker",
      "backupPath": "C:\\Program Files\\AspirinSampleWorker.backups",
      "windowsService": {
        "serviceName": "AspirinSampleWorker",
        "displayName": "Aspirin Sample Worker",
        "description": "Sample worker installed from an Aspirin package.",
        "executable": "Sample.Worker.exe",
        "startMode": "delayedAutomatic",
        "account": null
      },
      "iisSite": null,
      "settings": [
        { "name": "Sample__Greeting", "value": "Hello from the build", "secret": false, "expression": null }
      ]
    }
  ]
}
```

Every property is always present. One with no value is `null`.

| Property | Meaning |
| --- | --- |
| `schemaVersion` | The version of this format. The scripts refuse a file with any value but `2`. |
| `application` | The name of the AppHost that built the package. |
| `environment` | The environment the package was built for, from `--environment`. |
| `resources` | The services and sites, in the order they are installed. |

Version 1 had no `kind`, and kept `installPath` and `backupPath` inside `windowsService`.

### A resource

| Property | Meaning |
| --- | --- |
| `name` | The resource's name in the AppHost. A settings file uses it as a key. |
| `kind` | `windowsService` or `iisSite`, which says which of the two objects below describes the resource. The other is `null`. |
| `source` | The folder that holds the published files, relative to the package, with forward slashes. |
| `version` | The product version of the published assembly, or `null`. Inside a git repository the .NET SDK adds the commit after a `+`. |
| `installPath` | The folder on the server the files are copied to. |
| `backupPath` | The folder on the server that holds backups of earlier installs. |
| `windowsService` | How the service is installed, for a service. |
| `iisSite` | How the site is installed, for a site. |
| `settings` | The environment variables the service or site runs with. |

### windowsService

| Property | Meaning |
| --- | --- |
| `serviceName` | The name Windows knows the service by. |
| `displayName` | The name shown in the Services console. |
| `description` | The description shown in the Services console, or `null` to leave it as it is. |
| `executable` | The program to run, relative to `source`. |
| `startMode` | `automatic`, `delayedAutomatic` or `manual`. |
| `account` | The account the service runs as, or `null` for LocalSystem. |

### iisSite

```json
"iisSite": {
  "siteName": "Aspirin Sample Web",
  "appPoolName": "Aspirin Sample Web",
  "account": null,
  "alwaysRunning": false,
  "healthCheckPath": "/health",
  "bindings": [
    { "protocol": "http", "ipAddress": "*", "port": 18091, "hostName": "aspirin-sample.test",
      "certificateSubject": null, "certificateThumbprint": null, "certificateStore": null, "sni": false },
    { "protocol": "https", "ipAddress": "*", "port": 18443, "hostName": "aspirin-sample.test",
      "certificateSubject": "CN=aspirin-sample.test", "certificateThumbprint": null, "certificateStore": "My", "sni": true }
  ]
}
```

| Property | Meaning |
| --- | --- |
| `siteName` | The site's name in IIS. |
| `appPoolName` | The application pool the site runs in. |
| `account` | The account the pool runs as, or `null` for the pool's own identity. |
| `alwaysRunning` | Whether the pool starts with IIS and the site preloads, instead of waiting for a request. |
| `healthCheckPath` | The path the installer asks for after starting the site, or `null` to ask for `/`. |
| `bindings` | The site's HTTP and HTTPS bindings. |

A binding has a `protocol` (`http` or `https`), an `ipAddress` (`*` for every address), a `port` and a `hostName`
(`null` for any host name). An HTTPS binding names its certificate by `certificateSubject` or by
`certificateThumbprint` (in uppercase), and `certificateStore` says which local machine store holds it. `sni` is
`true` for an HTTPS binding with a host name.

### A setting

| Property | Meaning |
| --- | --- |
| `name` | The environment variable's name. |
| `value` | Its value, or `null` when it must be supplied at install time. |
| `secret` | Whether the value is a secret. The installer doesn't act on it; it tells a reader why a value is missing. |
| `expression` | For a value that only exists at run time, the Aspire expression it came from, such as `{api.bindings.http.url}`. |

## checksums.sha256

One line for every file in the package except `checksums.sha256` itself: the SHA-256 hash in lowercase
hexadecimal, two spaces, and the file's path relative to the package with forward slashes. Lines are sorted by
path.

```text
9cf5eeda0d7e83c1cb2653d5062db769914818ff1a0b9a46396deb76f616dd71  package.json
d8ea1fbbaafdc9734420131c5d1f4624826e411bad3e51d3eb4fb95ab154f66f  resources/worker/app/Microsoft.CSharp.dll
```

It is the format `sha256sum` writes, so `sha256sum --check checksums.sha256` verifies a package on Linux.

The installer fails a package that has a file the list doesn't name, lacks a file it does name, or holds a file
whose hash differs.

## On the server

The installer leaves two things behind, besides the service or site itself.

**The install folder** holds an exact copy of `source`.

**The backup folder** holds one folder for each of the most recent install runs that changed the service or site,
named with the UTC time the run started. Each one has an `app` folder with the files that were installed before the
run, if there were any, and a `backup.json` with the earlier configuration. For a service:

```json
{
  "serviceExisted": true,
  "wasRunning": true,
  "binaryPath": "\"C:\\Program Files\\AspirinSampleWorker\\Sample.Worker.exe\"",
  "displayName": "Aspirin Sample Worker",
  "description": "Sample worker installed from an Aspirin package.",
  "startMode": "Auto",
  "delayedAutoStart": true,
  "account": "LocalSystem",
  "environment": [ "Sample__Greeting=Hello from the build" ]
}
```

For a service that didn't exist before the run, the file holds only `"serviceExisted": false`.

For a site, `backup.json` says whether the site and pool existed and whether the site was started, and records the
certificate of each HTTPS binding's endpoint as HTTP.sys had it. Next to it, `site.xml` and `apppool.xml` hold the
site and pool as `appcmd list ... /config /xml` describes them, which `appcmd add ... /in` restores:

```json
{
  "siteExisted": true,
  "poolExisted": true,
  "siteWasStarted": true,
  "certificateBindings": [
    { "kind": "hostnameport", "endpoint": "aspirin-sample.test:18443",
      "existed": true, "certificateHash": "3B1F...", "appId": "{4dc3e181-e14b-4a21-b022-59fc669b0914}", "store": "MY" }
  ]
}
```

A run that installs a site also backs up the whole IIS configuration as `aspirin-<run>`, which
`appcmd list backup` shows.
