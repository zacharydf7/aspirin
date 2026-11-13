namespace Sample.Worker;

public class Worker(ILogger<Worker> logger, IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Lets the installer tests see what happens when an update can't start.
        if (configuration.GetValue<bool>("Sample:FailOnStart"))
        {
            throw new InvalidOperationException("Sample:FailOnStart is set.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("{Greeting} (database configured: {HasDatabase})",
                configuration["Sample:Greeting"], configuration.GetConnectionString("sampledb") is not null);
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
