using System.Text.Json;
using EmailOperations.Core;

namespace EmailOperations;

public sealed class IdentificationPublisher(IEventPublisher aimieRequests, IEventPublisher emailEvents)
    : IEventPublisher, IAsyncDisposable
{
    public Task PublishAsync(object content, CancellationToken token)
    {
        var item = content is JsonElement json ? json : JsonSerializer.SerializeToElement(content);
        return Events.String(item, "type") switch
        {
            "declaration" or "qa" or "argument-builder" or "timeline-builder" =>
                aimieRequests.PublishAsync(item, token),
            "email-data-ingestion" => emailEvents.PublishAsync(item, token),
            _ => throw new InvalidEventException("Unsupported identification event type")
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (aimieRequests is IAsyncDisposable aimie) await aimie.DisposeAsync();
        if (emailEvents is IAsyncDisposable email) await email.DisposeAsync();
    }
}
