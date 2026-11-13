using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Handoff.Aspire.Hosting;

internal static class PackageSettings
{
    /// <summary>
    /// Turns the environment variables Aspire would give the resource into package settings.
    /// </summary>
    /// <param name="includeSecretValues">Whether a secret setting keeps its value. It is marked secret either way.</param>
    public static async Task<IReadOnlyList<PackageSetting>> ResolveAsync(
        IResource resource, DistributedApplicationExecutionContext executionContext, ILogger logger, bool includeSecretValues,
        CancellationToken cancellationToken)
    {
        var configuration = await ExecutionConfigurationBuilder.Create(resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(executionContext, logger, cancellationToken);
        if (configuration.Exception is { } exception)
        {
            throw exception;
        }

        var settings = new List<PackageSetting>();
        foreach (var (name, (unprocessed, expression)) in configuration.EnvironmentVariablesWithUnprocessed.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            var parameters = new List<ParameterResource>();
            if (unprocessed is string literal)
            {
                settings.Add(new(name, literal));
            }
            else if (unprocessed is not IValueProvider provider || !DependsOnlyOnParameters(unprocessed, parameters))
            {
                settings.Add(new(name, Expression: expression));
            }
            else
            {
                var secret = parameters.Any(p => p.Secret);
                var value = secret && !includeSecretValues ? null : await provider.GetValueAsync(cancellationToken);
                settings.Add(new(name, value, secret));
            }
        }

        return settings;
    }

    // In publish mode Aspire can give a value to a parameter, but not to an endpoint or a container, which only
    // exist once something is running.
    private static bool DependsOnlyOnParameters(object value, List<ParameterResource> parameters)
    {
        switch (value)
        {
            case string:
                return true;
            case ParameterResource parameter:
                parameters.Add(parameter);
                return true;
            case IValueWithReferences withReferences:
                return withReferences.References.All(reference => DependsOnlyOnParameters(reference, parameters));
            default:
                return false;
        }
    }
}
