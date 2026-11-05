using EmailOperations.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EmailOperations;

public sealed class IdentificationWorker(IEmailWorkflowService workflows, IEventPublisher events,
    IHostApplicationLifetime lifetime, ILogger<IdentificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            logger.LogError(error, "Email identification job failed");
            Environment.ExitCode = 1;
        }
        finally { lifetime.StopApplication(); }
    }

    public async Task RunOnceAsync(CancellationToken token)
    {
        await workflows.IdentifyAsync(events, token);
    }
}
