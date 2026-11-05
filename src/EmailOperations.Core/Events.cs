using System.Text.Json;
using Azure.Messaging.EventHubs;
using Azure.Messaging.EventHubs.Producer;

namespace EmailOperations.Core;

public sealed class InvalidEventException(string message) : Exception(message);

public static class Events
{
    public static JsonElement Content(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("contentData", out var content)
            ? content : root;

    public static string? String(JsonElement content, string key) =>
        content.ValueKind == JsonValueKind.Object && content.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static string Required(JsonElement content, string key) =>
        !string.IsNullOrWhiteSpace(String(content, key)) ? String(content, key)!
            : throw new InvalidEventException($"Event is missing {key}");

    public static string Text(JsonElement content) =>
        !string.IsNullOrWhiteSpace(String(content, "question")) ? String(content, "question")! :
        !string.IsNullOrWhiteSpace(String(content, "input")) ? String(content, "input")! :
        throw new InvalidEventException("Event is missing input or question");

    public static bool Matches(JsonElement content, string type) =>
        String(content, "type") == type;
}

public interface IEventPublisher
{
    Task PublishAsync(object content, CancellationToken token);
}

public sealed class EventPublisher(string hubName) : IEventPublisher, IAsyncDisposable
{
    private readonly EventHubProducerClient producer = new(Settings.Required("EVENTHUB_NAMESPACE"), hubName,
        Settings.Credential, new EventHubProducerClientOptions { ConnectionOptions = Settings.ConnectionOptions() });

    public Task PublishAsync(object content, CancellationToken token) =>
        producer.SendAsync([new EventData(JsonSerializer.SerializeToUtf8Bytes(new { contentData = content }))], token);

    public ValueTask DisposeAsync() => producer.DisposeAsync();
}
