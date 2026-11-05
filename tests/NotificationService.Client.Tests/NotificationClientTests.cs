using System.Net;
using System.Text.Json;
using NotificationService;
using Xunit;

namespace NotificationService.Client.Tests;

public sealed class NotificationClientTests
{
    [Theory]
    [InlineData(IngestionStatus.PRELIMINARY)]
    [InlineData(IngestionStatus.EVIDENCE_EXTRACTION)]
    public async Task SendsTypedIngestionNotice(IngestionStatus status)
    {
        var handler = new RecordingHandler(HttpStatusCode.Accepted);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.org/") };
        await new NotificationClient(http).SendAsync(CreateNotice(status));

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://example.org/api/sendNotice", handler.Uri!.AbsoluteUri);
        Assert.Equal("application/json", handler.ContentType);
        Assert.True(NoticeParser.TryCreate(handler.Body!, out var notice));
        Assert.Equal(status, Assert.IsType<DocumentIngestionNotice>(notice).Data!.Status);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("existing-id", body.RootElement.GetProperty("id").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.OK)]
    public async Task ExposesErrorsWithoutRetrying(HttpStatusCode status)
    {
        var handler = new RecordingHandler(status);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.org/") };
        var error = await Assert.ThrowsAsync<NotificationTransmissionException>(() =>
            new NotificationClient(http).SendAsync(CreateNotice(IngestionStatus.PRELIMINARY)));
        Assert.Equal(status, error.StatusCode);
        Assert.Equal("backend response", error.ResponseBody);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task HonorsCancellation()
    {
        using var http = new HttpClient(new RecordingHandler(HttpStatusCode.Accepted))
            { BaseAddress = new Uri("https://example.org/") };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NotificationClient(http)
            .SendAsync(CreateNotice(IngestionStatus.PRELIMINARY), cancellation.Token));
    }

    private static DocumentIngestionNotice CreateNotice(IngestionStatus status) => new()
    {
        Oid = "user-1", Id = "existing-id",
        Data = new IngestionData { AccountId = "account-1", Filepath = "/file.pdf", Status = status }
    };

    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public string? ContentType { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            Method = request.Method;
            Uri = request.RequestUri;
            ContentType = request.Content!.Headers.ContentType!.MediaType;
            Body = await request.Content.ReadAsStringAsync(token);
            return new HttpResponseMessage(status) { Content = new StringContent("backend response") };
        }
    }
}
