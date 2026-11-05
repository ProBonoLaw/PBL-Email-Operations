using System.Text.Json;
using System.Text.Json.Nodes;
using NotificationService;
using Xunit;

namespace NotificationService.Tests;

public sealed class NoticeTests
{
    [Theory]
    [InlineData("DECLARATION", """{"accountId":"a","sessionId":"s","declarationUrl":"https://example.org/file","status":"STARTED"}""")]
    [InlineData("ARGUMENT_BUILDER", """{"accountId":"a","sessionId":"s","status":"FACT_VALIDATION"}""")]
    [InlineData("TIMELINE_BUILDER", """{"accountId":"a","sessionId":"s","status":"FAILED"}""")]
    [InlineData("INGESTION", """{"accountId":"a","filepath":"/file.pdf","status":"SUCCESS"}""")]
    [InlineData("INGESTION", """{"accountId":"a","filepath":"/file.pdf","status":"PRELIMINARY"}""")]
    [InlineData("INGESTION", """{"accountId":"a","filepath":"/file.pdf","status":"EVIDENCE_EXTRACTION"}""")]
    [InlineData("EGESTION", """{"accountId":"a","filepath":"/file.pdf","status":"SUCCESS"}""")]
    [InlineData("CASE_CREATED", """{"accountId":"a","status":"STARTED"}""")]
    [InlineData("CASE_DELETED", """{"accountId":"a","status":"FAILED"}""")]
    [InlineData("ALERT", """{"accountId":"a","sourceId":42,"clientFeedbackRequestId":"f","markdownPath":"/m","status":"FEEDBACK_REQUIRED"}""")]
    [InlineData("CHAT_MESSAGE", """{"message":"hello"}""")]
    public void CreatesTypedNoticesWithCompatibleJson(string type, string data)
    {
        var request = $$"""{"oid":"user-1","notificationType":"{{type}}","data":{{data}}}""";
        Assert.True(NoticeParser.TryCreate(request, out var notice));
        Assert.NotNull(notice);
        Assert.Equal("user-1", notice.Oid);
        Assert.Equal(type, notice.NotificationType);
        Assert.False(notice.Read);
        Assert.True(Guid.TryParse(notice.Id, out _));
        Assert.True(notice.CreatedAt > DateTime.UtcNow.AddMinutes(-1));
        Assert.IsType(ExpectedClass(type), notice);

        using var persisted = JsonDocument.Parse(notice.ToJson());
        var root = persisted.RootElement;
        Assert.Equal(type, root.GetProperty("notificationType").GetString());
        Assert.Equal("user-1", root.GetProperty("oid").GetString());
        Assert.False(root.GetProperty("read").GetBoolean());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(data), JsonNode.Parse(root.GetProperty("data").GetRawText())));
    }

    [Theory]
    [InlineData("""{"oid":"u","notificationType":"CASE_CREATED","data":{"accountId":"a","status":"PENDING"}}""")]
    [InlineData("""{"oid":"u","notificationType":"INGESTION","data":{"accountId":"a","status":"STARTED"}}""")]
    [InlineData("""{"oid":"u","notificationType":"ALERT","data":{"accountId":"a","sourceId":-1,"clientFeedbackRequestId":"f","markdownPath":"/m","status":"FEEDBACK_REQUIRED"}}""")]
    [InlineData("""{"oid":"u","notificationType":"CASE_DELETED","data":{"accountId":"a","status":"SUCCESS","extra":true}}""")]
    [InlineData("""{"oid":"u","notificationType":"CASE_CREATED","data":{"accountId":"a","status":"success"}}""")]
    [InlineData("""{"oid":"u","notificationType":"CASE_CREATED","data":{"accountId":"a","status":"SUCCESS","errorMessage":null}}""")]
    [InlineData("""{"oid":"u","notificationType":"UNKNOWN","data":{}}""")]
    public void RejectsInvalidPayload(string json) => Assert.False(NoticeParser.TryCreate(json, out _));

    [Theory]
    [InlineData("EGESTION", "PRELIMINARY")]
    [InlineData("EGESTION", "EVIDENCE_EXTRACTION")]
    [InlineData("CASE_CREATED", "PRELIMINARY")]
    [InlineData("CASE_DELETED", "EVIDENCE_EXTRACTION")]
    [InlineData("TIMELINE_BUILDER", "PRELIMINARY")]
    [InlineData("DECLARATION", "EVIDENCE_EXTRACTION")]
    [InlineData("ARGUMENT_BUILDER", "PRELIMINARY")]
    [InlineData("INGESTION", "preliminary")]
    [InlineData("INGESTION", "evidence_extraction")]
    [InlineData("INGESTION", "3")]
    [InlineData("INGESTION", "PRELIMINARY, EVIDENCE_EXTRACTION")]
    public void RejectsStatusesOutsideTheirWorkflow(string type, string status)
    {
        var fields = type switch
        {
            "INGESTION" or "EGESTION" => "\"filepath\":\"/file.pdf\",",
            "DECLARATION" => "\"sessionId\":\"s\",\"declarationUrl\":\"https://example.org/file\",",
            "TIMELINE_BUILDER" or "ARGUMENT_BUILDER" => "\"sessionId\":\"s\",",
            _ => ""
        };
        var json = $$$"""{"oid":"u","notificationType":"{{{type}}}","data":{"accountId":"a",{{{fields}}}"status":"{{{status}}}"}}""";
        Assert.False(NoticeParser.TryCreate(json, out _));
    }

    [Fact]
    public void PreservesProvidedIdAndTypedData()
    {
        var request = """{"oid":"u","notificationType":"CASE_CREATED","id":"existing-123","data":{"accountId":"a","status":"SUCCESS"}}""";
        Assert.True(NoticeParser.TryCreate(request, out var notice));
        var typed = Assert.IsType<CaseCreatedNotice>(notice);
        Assert.Equal("existing-123", typed.Id);
        Assert.Equal("a", typed.Data!.AccountId);
        Assert.Equal(WorkflowStatus.SUCCESS, typed.Data.Status);
    }

    [Fact]
    public void AcceptsIntegralAlertSourceNumber()
    {
        var request = """{"oid":"u","notificationType":"ALERT","data":{"accountId":"a","sourceId":1.0,"clientFeedbackRequestId":"f","markdownPath":"/m","status":"FEEDBACK_REQUIRED"}}""";
        Assert.True(NoticeParser.TryCreate(request, out var notice));
        var alert = Assert.IsType<AlertNotice>(notice);
        Assert.Equal(1, alert.Data!.SourceId!.Value.Number);
    }

    [Fact]
    public async Task PersistsBeforeBroadcasting()
    {
        var operations = new List<string>();
        var store = new FakeStore(operations);
        var broadcaster = new FakeBroadcaster(operations);
        var service = new NoticeService(store, broadcaster);
        Assert.True(NoticeParser.TryCreate("""{"oid":"user-1","notificationType":"CASE_CREATED","id":"notice-1","data":{"accountId":"a","status":"SUCCESS"}}""", out var notice));

        await service.SendAsync(notice!, CancellationToken.None);

        Assert.Equal(["store", "broadcast"], operations);
        Assert.Same(store.Notice, broadcaster.Notice);
        Assert.Equal(store.Json, broadcaster.Json);
        Assert.Equal("USER_user-1", "USER_" + broadcaster.Notice!.Oid);
    }

    [Fact]
    public async Task DoesNotBroadcastWhenPersistenceFails()
    {
        var operations = new List<string>();
        var store = new FakeStore(operations) { Fail = true };
        var broadcaster = new FakeBroadcaster(operations);
        var service = new NoticeService(store, broadcaster);
        Assert.True(NoticeParser.TryCreate("""{"oid":"user-1","notificationType":"CASE_CREATED","data":{"accountId":"a","status":"SUCCESS"}}""", out var notice));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync(notice!, CancellationToken.None));
        Assert.Equal(["store"], operations);
    }

    private static Type ExpectedClass(string type) => type switch
    {
        "DECLARATION" => typeof(DeclarationNotice),
        "ARGUMENT_BUILDER" => typeof(ArgumentBuilderNotice),
        "TIMELINE_BUILDER" => typeof(TimelineBuilderNotice),
        "INGESTION" => typeof(DocumentIngestionNotice),
        "EGESTION" => typeof(DocumentEgestionNotice),
        "CASE_CREATED" => typeof(CaseCreatedNotice),
        "CASE_DELETED" => typeof(CaseDeletedNotice),
        "ALERT" => typeof(AlertNotice),
        _ => typeof(ChatMessageNotice)
    };

    private sealed class FakeStore(List<string> operations) : INoticeStore
    {
        public bool Fail { get; init; }
        public INotice? Notice { get; private set; }
        public string? Json { get; private set; }
        public Task StoreAsync(INotice notice, string json, CancellationToken token)
        {
            operations.Add("store");
            Notice = notice;
            Json = json;
            if (Fail) throw new InvalidOperationException("Cosmos failed");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeBroadcaster(List<string> operations) : INoticeBroadcaster
    {
        public INotice? Notice { get; private set; }
        public string? Json { get; private set; }
        public Task SendAsync(INotice notice, string json, CancellationToken token)
        {
            operations.Add("broadcast");
            Notice = notice;
            Json = json;
            return Task.CompletedTask;
        }
    }
}
