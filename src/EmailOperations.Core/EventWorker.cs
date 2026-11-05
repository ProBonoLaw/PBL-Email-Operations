using System.Text.Json;
using Azure;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Consumer;
using Azure.Messaging.EventHubs.Processor;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EmailOperations.Core;

public interface IEventHandler
{
    Task HandleAsync(JsonElement content, string partition, long sequence, CancellationToken token);
}

public sealed class EventWorker : IHostedService, IDisposable
{
    private readonly EventProcessorClient processor;
    private readonly BlobContainerClient failedEvents;
    private readonly SingleRequestGate processing = new();
    private readonly IEventHandler handler;
    private readonly IHostApplicationLifetime lifetime;
    private readonly ILogger<EventWorker> logger;
    private readonly string consumerGroup;
    private int stopping;

    public EventWorker(IEventHandler handler, IHostApplicationLifetime lifetime, ILogger<EventWorker> logger)
    {
        this.handler = handler;
        this.lifetime = lifetime;
        this.logger = logger;
        consumerGroup = Settings.Required("EVENTHUB_CONSUMER_GROUP");
        var checkpoint = new BlobContainerClient(new Uri(Settings.Required("CHECKPOINT_BLOB_CONTAINER_URI")),
            Settings.Credential);
        failedEvents = new BlobContainerClient(new Uri(Settings.Required("FAILED_EVENTS_BLOB_CONTAINER_URI")),
            Settings.Credential);
        processor = new EventProcessorClient(checkpoint, consumerGroup,
            Settings.Required("EVENTHUB_NAMESPACE"), Settings.Required("EVENTHUB_NAME"),
            Settings.Credential, new EventProcessorClientOptions { ConnectionOptions = Settings.ConnectionOptions() });
        processor.PartitionInitializingAsync += args =>
        {
            args.DefaultStartingPosition = EventPosition.Latest;
            return Task.CompletedTask;
        };
        processor.ProcessEventAsync += ProcessAsync;
        processor.ProcessErrorAsync += ErrorAsync;
    }

    public Task StartAsync(CancellationToken token) => processor.StartProcessingAsync(token);
    public Task StopAsync(CancellationToken token) => processor.StopProcessingAsync(token);
    public void Dispose() => processing.Dispose();

    private async Task ProcessAsync(ProcessEventArgs args)
    {
        if (args.Data is null) return;
        try { await processing.RunAsync(() => ProcessOneAsync(args), args.CancellationToken); }
        catch (OperationCanceledException) when (args.CancellationToken.IsCancellationRequested) { return; }
    }

    private async Task ProcessOneAsync(ProcessEventArgs args)
    {
        try
        {
            if (Volatile.Read(ref stopping) != 0) return;
            using var document = JsonDocument.Parse(args.Data.EventBody.ToMemory());
            try
            {
                var content = Events.Content(document.RootElement);
                if (content.ValueKind != JsonValueKind.Object)
                    throw new InvalidEventException("Event content must be an object");
                await handler.HandleAsync(content, args.Partition.PartitionId,
                    args.Data.SequenceNumber, args.CancellationToken);
            }
            catch (InvalidEventException error)
            {
                await RetainAsync(args, error);
            }
            await args.UpdateCheckpointAsync(args.CancellationToken);
        }
        catch (JsonException error)
        {
            try
            {
                await RetainAsync(args, error);
                await args.UpdateCheckpointAsync(args.CancellationToken);
            }
            catch (Exception retentionError) { Stop(retentionError, args); }
        }
        catch (OperationCanceledException) when (args.CancellationToken.IsCancellationRequested) { }
        catch (Exception error) { Stop(error, args); }
    }

    private async Task RetainAsync(ProcessEventArgs args, Exception error)
    {
        await failedEvents.GetBlobClient($"{consumerGroup}/{args.Partition.PartitionId}/{args.Data!.SequenceNumber}.json")
            .UploadAsync(new BinaryData(args.Data.EventBody.ToMemory()), overwrite: true,
                cancellationToken: args.CancellationToken);
        logger.LogError(error, "Retained invalid event at {Partition}/{Sequence}",
            args.Partition.PartitionId, args.Data.SequenceNumber);
    }

    private Task ErrorAsync(ProcessErrorEventArgs args)
    {
        logger.LogError(args.Exception, "Event Hubs processor error during {Operation}", args.Operation);
        if (args.Exception is RequestFailedException) Stop(args.Exception);
        return Task.CompletedTask;
    }

    private void Stop(Exception error, ProcessEventArgs? args = null)
    {
        logger.LogCritical(error, "Worker stopped without checkpoint at {Partition}/{Sequence}",
            args?.Partition.PartitionId, args?.Data?.SequenceNumber);
        if (Interlocked.Exchange(ref stopping, 1) == 0)
        {
            Environment.ExitCode = 1;
            lifetime.StopApplication();
        }
    }
}
