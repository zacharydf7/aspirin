# IIS sites

An ASP.NET Core project can be packaged as an IIS site instead of a Windows service. The installer then creates or
updates the site and its application pool, and backs up both so the install can be rolled back.

```csharp
builder.AddProject<Projects.Web>("web")
    .WithReference(builder.AddConnectionString("jobs"))
    .PublishAsIisSite(@"C:\inetpub\Contoso\Web", site =>
    {
        site.SiteName = "Contoso Web";
        site.Bindings.Add(IisBinding.Http(80, "web.contoso.local"));
        site.Bindings.Add(IisBinding.Https(443, "web.contoso.local", certificateSubject: "CN=web.contoso.local"));
        site.HealthCheckPath = "/health";
    });
```

A package can hold sites and services together. They are installed in the order the AppHost declares them, as
[Several services in one package](several-services.md) describes.

## What the server needs

- **IIS 10 or later**, which comes with Windows Server 2016 and later.
- **The ASP.NET Core Module**, which the .NET Hosting Bundle installs. A self-contained project, the default,
  doesn't need the .NET runtime on the server, but IIS still needs the module to run it.
- **The certificates** for HTTPS bindings, already in the local machine's certificate store.
- **The Application Initialization feature**, only for a site with `AlwaysRunning` set.

The installer checks all of these before it changes anything, and stops with a message naming what's missing. It
doesn't install Windows features itself, because adding one can restart every site on the server.

## The site and its pool

The installer owns the site and its application pool. On every install it makes them match the AppHost:

| Option | Default | What it sets |
| --- | --- | --- |
| `SiteName` | The resource name | The site's name in IIS Manager. |
| `AppPoolName` | `SiteName` | The application pool the site runs in. No other site in the package may use it. |
| `Bindings` | None; at least one is required | The addresses the site answers on. |
| `Account` | The pool's own identity | The account the pool runs as. |
| `AlwaysRunning` | Off | Starts the site with IIS and keeps it running without waiting for a request, for a site that does background work. |
| `HealthCheckPath` | None | A path the installer asks for after starting the site. |

Names may use letters, digits, spaces, dots, hyphens and underscores, up to 64 characters.

The pool always runs with no .NET Framework runtime and the integrated pipeline, which is what ASP.NET Core needs.
Settings the installer doesn't manage, such as recycling, are kept from the existing pool.

## Bindings

`IisBinding.Http(port, hostName)` and `IisBinding.Https(port, hostName, ...)` describe one address each. Leave the
host name out to answer to any host name. A binding listens on every IP address the server has; to listen on one,
change it with a `with` expression:

```csharp
site.Bindings.Add(IisBinding.Http(80, "web.contoso.local") with { IpAddress = "10.0.0.5" });
```

The site's HTTP and HTTPS bindings are made to match the list on every install. A binding someone added in IIS
Manager is removed, and `install.ps1 -WhatIf`, run as an administrator, lists it first. Bindings for other
protocols, such as `net.tcp`, are left alone.

Two sites in one package can't answer on the same address, port and host name, and publishing fails if they do.
The installer also stops if a site that isn't in the package already answers there.

## HTTPS certificates

The package never carries a certificate. Name one that is already installed, by subject or by thumbprint:

```csharp
IisBinding.Https(443, "web.contoso.local", certificateSubject: "CN=web.contoso.local")
IisBinding.Https(443, "web.contoso.local", certificateThumbprint: "8A1F...")
```

By subject, the installer picks the newest certificate with that subject that has a private key and is valid that
day. When the certificate is renewed, the next install uses the new one, with no new package needed. By thumbprint,
it uses exactly that certificate.

The certificate is looked for in the `My` (Personal) store. Set `CertificateStore = "WebHosting"` with a `with`
expression to use the Web Hosting store instead.

An HTTPS binding with a host name uses Server Name Indication, so several sites can share port 443 with a
certificate each. Without a host name, the certificate belongs to the address and port, and the installer stops if
another site's HTTPS binding without SNI already uses that port.

## Settings

Settings become the application pool's environment variables. They're kept in IIS's own configuration, which only
administrators and the pool itself can read, rather than in `web.config`, so the installed files stay exactly the
ones in the package.

The settings Aspire derives from the project's own endpoints, such as `HTTP_PORTS` and `ASPNETCORE_URLS`, are left
out. IIS decides where the site listens, from its bindings.

## The pool's account

Without `Account`, the pool runs as its own identity, `IIS AppPool\<pool name>`, and needs no password. Built-in
accounts such as `NT AUTHORITY\NetworkService` need none either. For a user account the installer asks for the
password, or takes it from `-Credential`, as it does for a service. IIS keeps the password, so it's asked for only
when the pool is created or its account changes.

The account gets read access to the install folder.

## What the installer does

For each site:

1. **Backs up** the installed files, the site and its pool as IIS has them, and the certificates of its HTTPS
   bindings.
2. If the site exists, **shows an "updating" page**: it writes `app_offline.htm` into the site, which makes the
   ASP.NET Core Module stop the app and show the page to visitors.
3. **Replaces** the installed files with the ones in the package.
4. **Creates or updates the pool**: its account, its settings, and whether it keeps running.
5. **Creates or updates the site**: its folder, its pool and its bindings, and gives each HTTPS binding its
   certificate.
6. **Gives the pool's account** read access to the install folder.
7. **Removes the "updating" page and starts the site.**
8. **Checks the site answers.** With `HealthCheckPath`, it asks for that path and expects a success status within
   30 seconds. Without it, it asks for `/` and accepts any status below 500, which shows the app started.

Before the first site, it also backs up the whole IIS configuration, under a name like `aspirin-20261113-014500-123`,
for an administrator to restore by hand if a rollback isn't enough.

If an install fails partway, the "updating" page stays up until you roll back or install again.
