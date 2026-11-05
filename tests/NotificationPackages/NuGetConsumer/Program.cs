using System.Net;
using System.Text.Json;
using NotificationService;

using var http = new HttpClient(new ConsumerHandler()) { BaseAddress = new Uri("https://notification.invalid/") };
var client = new NotificationClient(http);
foreach (var status in new[] { IngestionStatus.PRELIMINARY, IngestionStatus.EVIDENCE_EXTRACTION })
{
    await client.SendAsync(new DocumentIngestionNotice
    {
        Oid = "consumer-user",
        Data = new IngestionData { AccountId = "consumer-account", Filepath = "/file.pdf", Status = status }
    });
}
Console.WriteLine("Installed NuGet client transmitted both ingestion completion statuses.");

sealed class ConsumerHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.Method != HttpMethod.Post || request.RequestUri?.AbsoluteUri != "https://notification.invalid/api/sendNotice" ||
            request.Content?.Headers.ContentType?.MediaType != "application/json")
            throw new InvalidOperationException("Incorrect transmission request.");
        using var json = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token));
        if (json.RootElement.GetProperty("notificationType").GetString() != "INGESTION" ||
            json.RootElement.GetProperty("data").GetProperty("status").GetString() is not ("PRELIMINARY" or "EVIDENCE_EXTRACTION"))
            throw new InvalidOperationException("Incorrect serialized contract.");
        return new HttpResponseMessage(HttpStatusCode.Accepted);
    }
}
