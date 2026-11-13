# API reference

The library has one entry point and two types that configure it:

| Type | Namespace | What it is |
| --- | --- | --- |
| [WindowsServiceExtensions](xref:Aspire.Hosting.WindowsServiceExtensions) | `Aspire.Hosting` | `PublishAsWindowsService`, which marks a project to be packaged as a Windows service. |
| [WindowsServiceOptions](xref:Aspirin.Hosting.WindowsServiceOptions) | `Aspirin.Hosting` | The service name, account, start mode and the other options. |
| [WindowsServiceStartMode](xref:Aspirin.Hosting.WindowsServiceStartMode) | `Aspirin.Hosting` | When Windows starts the service. |

`PublishAsWindowsService` is in the `Aspire.Hosting` namespace, like Aspire's own extension methods, so it shows up
on a project without another `using` line.
