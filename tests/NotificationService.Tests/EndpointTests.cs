using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace NotificationService.Tests;

public sealed class EndpointTests
{
    [Theory]
    [InlineData("PRELIMINARY")]
    [InlineData("EVIDENCE_EXTRACTION")]
    public async Task AcceptsAndTransmitsNewIngestionStatuses(string status)
    {
        using var factory = new ServiceFactory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/api/sendNotice", Payload(status));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        Assert.Equal(factory.Store.Json, factory.Broadcaster.Json);
        using var json = JsonDocument.Parse(factory.Store.Json!);
        Assert.Equal(status, json.RootElement.GetProperty("data").GetProperty("status").GetString());
    }

    [Fact]
    public async Task RejectsInvalidStatusBeforePersistence()
    {
        using var factory = new ServiceFactory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/api/sendNotice", Payload("preliminary"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(factory.Store.Json);
        Assert.Null(factory.Broadcaster.Json);
    }

    [Fact]
    public async Task ReturnsProblemWhenPersistenceFails()
    {
        using var factory = new ServiceFactory();
        factory.Store.Fail = true;
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/api/sendNotice", Payload("PRELIMINARY"));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Null(factory.Broadcaster.Json);
    }

    [Fact]
    public async Task ServesEmbeddedOpenApiWithIngestionSpecificStatuses()
    {
        using var factory = new ServiceFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        var content = await response.Content.ReadAsStringAsync();
        using var embedded = typeof(NoticeEndpoints).Assembly.GetManifestResourceStream("NotificationService.openapi.json")!;
        using var reader = new StreamReader(embedded);
        Assert.Equal(await reader.ReadToEndAsync(), content);
        using var json = JsonDocument.Parse(content);
        Assert.Equal("3.1.0", json.RootElement.GetProperty("openapi").GetString());
        var schemas = json.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.Equal(Enum.GetNames<IngestionStatus>(), schemas.GetProperty("IngestionStatus")
            .GetProperty("enum").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(Enum.GetNames<WorkflowStatus>(), schemas.GetProperty("WorkflowStatus")
            .GetProperty("enum").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(9, schemas.GetProperty("SendNoticeRequest").GetProperty("oneOf").GetArrayLength());
    }

    private static StringContent Payload(string status) => new(
        $$$"""{"oid":"u","notificationType":"INGESTION","data":{"accountId":"a","filepath":"/file.pdf","status":"{{{status}}}"}}""",
        Encoding.UTF8, "application/json");

    private sealed class ServiceFactory : WebApplicationFactory<Program>
    {
        public FakeStore Store { get; } = new();
        public FakeBroadcaster Broadcaster { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("COSMOS_ENDPOINT", "https://localhost:8081");
            builder.UseSetting("COSMOS_KEY", Convert.ToBase64String(new byte[64]));
            builder.UseSetting("WEB_PUBSUB_CONNECTION_STRING",
                "Endpoint=https://localhost;AccessKey=" + Convert.ToBase64String(new byte[32]) + ";Version=1.0;");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<INoticeStore>();
                services.RemoveAll<INoticeBroadcaster>();
                services.AddSingleton<INoticeStore>(Store);
                services.AddSingleton<INoticeBroadcaster>(Broadcaster);
            });
        }
    }

    private sealed class FakeStore : INoticeStore
    {
        public bool Fail { get; set; }
        public string? Json { get; private set; }
        public Task StoreAsync(INotice notice, string json, CancellationToken token)
        {
            if (Fail) throw new InvalidOperationException("Persistence failed");
            Json = json;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeBroadcaster : INoticeBroadcaster
    {
        public string? Json { get; private set; }
        public Task SendAsync(INotice notice, string json, CancellationToken token)
        {
            Json = json;
            return Task.CompletedTask;
        }
    }
}
