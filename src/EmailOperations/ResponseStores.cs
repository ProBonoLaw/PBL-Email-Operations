using System.Net;
using System.Text.Json;
using Azure.Storage.Blobs;
using EmailOperations.Core;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;

namespace EmailOperations;

public interface IResultStore
{
    Task<JsonElement> ReadAsync(string type, string accountId, string responseId,
        string outputUrl, CancellationToken token);
}

public sealed class BlobResultStore : IResultStore
{
    private readonly BlobContainerClient container;

    public BlobResultStore() : this(new BlobContainerClient(
        new Uri(Settings.Required("OUTPUT_BLOB_CONTAINER_URI")), Settings.Credential)) { }

    public BlobResultStore(BlobContainerClient container) => this.container = container;

    public async Task<JsonElement> ReadAsync(string type, string accountId, string responseId,
        string outputUrl, CancellationToken token)
    {
        if (type is not ("declaration" or "qa") ||
            new[] { accountId, responseId }.Any(value => string.IsNullOrWhiteSpace(value) ||
                value.Contains('/') || value.Contains('\\')))
            throw new InvalidEventException("Invalid result identity");
        var blob = container.GetBlobClient($"{type}/{accountId}/{responseId}/result.json");
        if (!Uri.TryCreate(outputUrl, UriKind.Absolute, out var supplied) ||
            supplied != blob.Uri)
            throw new InvalidEventException("Result URL does not match the configured output container");
        var result = await blob.DownloadContentAsync(token);
        using var document = JsonDocument.Parse(result.Value.Content.ToString());
        return document.RootElement.Clone();
    }
}

public interface IResponseDeliveryStore
{
    Task<bool> WasDeliveredAsync(string accountId, string type, string responseId, CancellationToken token);
    Task MarkDeliveredAsync(string accountId, string type, string responseId, CancellationToken token);
}

public sealed class CosmosResponseDeliveryStore : IResponseDeliveryStore, IDisposable
{
    private sealed class DeliveryRecord
    {
        [JsonProperty("id")] public string Id { get; set; } = "";
        [JsonProperty("accountId")] public string AccountId { get; set; } = "";
        [JsonProperty("type")] public string Type { get; set; } = "EMAIL_RESPONSE_DELIVERY";
        [JsonProperty("deliveredAt")] public DateTimeOffset DeliveredAt { get; set; }
    }

    private readonly CosmosClient client = new(Settings.Required("COSMOS_CONNECTION_STRING"));
    private Container Container => client.GetContainer(Settings.Get("COSMOS_DATABASE") ??
        "db_conversation_history", Settings.Get("COSMOS_DELIVERY_CONTAINER") ?? "requests");
    private static string Id(string type, string responseId) => $"email-delivery-{type}-{responseId}";

    public async Task<bool> WasDeliveredAsync(string accountId, string type, string responseId,
        CancellationToken token)
    {
        try
        {
            await Container.ReadItemAsync<DeliveryRecord>(Id(type, responseId),
                new PartitionKey(accountId), cancellationToken: token);
            return true;
        }
        catch (CosmosException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task MarkDeliveredAsync(string accountId, string type, string responseId,
        CancellationToken token)
    {
        try
        {
            await Container.CreateItemAsync(new DeliveryRecord
            {
                Id = Id(type, responseId), AccountId = accountId, DeliveredAt = DateTimeOffset.UtcNow
            }, new PartitionKey(accountId), cancellationToken: token);
        }
        catch (CosmosException error) when (error.StatusCode == HttpStatusCode.Conflict) { }
    }

    public void Dispose() => client.Dispose();
}
