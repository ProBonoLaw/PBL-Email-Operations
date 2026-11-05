using Azure;
using Azure.Core;
using Azure.Messaging.WebPubSub;
using Microsoft.Azure.Cosmos;
using System.Text;

namespace NotificationService;

public interface INoticeStore
{
    Task StoreAsync(INotice notice, string json, CancellationToken token);
}

public interface INoticeBroadcaster
{
    Task SendAsync(INotice notice, string json, CancellationToken token);
}

public sealed class CosmosNoticeStore(CosmosClient client, string database, string container) : INoticeStore
{
    public async Task StoreAsync(INotice notice, string json, CancellationToken token)
    {
        var partitionKey = new PartitionKeyBuilder().Add(notice.Oid!).Add(notice.NotificationType).Build();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        using var response = await client.GetContainer(database, container).CreateItemStreamAsync(stream, partitionKey,
            cancellationToken: token);
        response.EnsureSuccessStatusCode();
    }
}

public sealed class WebPubSubNoticeBroadcaster(WebPubSubServiceClient client) : INoticeBroadcaster
{
    public async Task SendAsync(INotice notice, string json, CancellationToken token)
    {
        await client.SendToGroupAsync("USER_" + notice.Oid, RequestContent.Create(json),
            new ContentType("application/json"), context: new RequestContext { CancellationToken = token });
    }
}

public sealed class NoticeService(INoticeStore store, INoticeBroadcaster broadcaster)
{
    public async Task SendAsync(INotice notice, CancellationToken token)
    {
        var json = notice.ToJson();
        await store.StoreAsync(notice, json, token);
        await broadcaster.SendAsync(notice, json, token);
    }
}
