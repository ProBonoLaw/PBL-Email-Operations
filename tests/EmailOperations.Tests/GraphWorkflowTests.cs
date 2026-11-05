using System.Text.Json;
using Azure.Storage.Blobs;
using EmailOperations;
using EmailOperations.Core;
using Xunit;

namespace EmailOperations.Tests;

[CollectionDefinition("Process environment", DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection { }

[Collection("Process environment")]
public class GraphWorkflowTests
{
    [Fact]
    public async Task Unread_declaration_is_sent_to_aimie_before_graph_marks_it_read()
    {
        var graph = new FakeGraph();
        var accounts = new FakeAccounts();
        var workflow = new EmailWorkflowService(accounts, graph, new GraphFiles(graph, sharePointHost: "sharepoint.example.com"));
        var publisher = new RecordingPublisher(graph);

        await workflow.IdentifyAsync(publisher, default);

        var sent = JsonSerializer.SerializeToElement(publisher.Events.Single());
        Assert.Equal("declaration", Events.String(sent, "type"));
        Assert.Equal("case-a", Events.String(sent, "accountId"));
        Assert.Equal("Draft a declaration", Events.String(sent, "input"));
        Assert.Equal("publish", graph.Calls[^2]);
        Assert.StartsWith("PATCH users/mailbox-id/messages/message-id", graph.Calls[^1]);
    }

    [Theory]
    [InlineData("sharepoint.example.com", true)]
    [InlineData("other.example.com", false)]
    public async Task Generated_declaration_without_message_id_uses_send_mail(string sourceHost, bool includeSource)
    {
        var graph = new FakeFileGraph();
        var results = new FakeResults(sourceHost);
        var deliveries = new FakeDeliveries();
        var workflow = new EmailWorkflowService(new FakeAccounts(), graph, new GraphFiles(graph, "info@example.com", "sharepoint.example.com"),
            results: results, deliveries: deliveries);
        var response = Json("""{"type":"declaration","status":"Generated","accountId":"case-a","input":"Draft","sessionId":"session-1","responseId":"2-15","outputUrl":"https://example.com/result.json"}""");
        await workflow.RespondAsync(response, default);
        await workflow.RespondAsync(response, default);

        Assert.Contains(graph.Calls, call => call == "POST users/mailbox-id/sendMail");
        Assert.Equal(1, graph.Calls.Count(call => call == "POST users/mailbox-id/sendMail"));
        Assert.Equal(1, results.Reads);
        Assert.Contains(graph.Uploads, path => path.Contains("_GeneratedDocuments/declaration/declaration-2-15.pdf:/content"));
        Assert.Contains(graph.HtmlUploads, html => html.Contains("Indexed declaration"));
        Assert.Contains(graph.HtmlUploads, html => html.Contains("Evidence body"));
        Assert.Equal(includeSource, graph.HtmlUploads.Any(html => html.Contains("https://" + sourceHost + "/sites/")));
        Assert.Contains(graph.Uploads, path => path.Contains("Exhibit-1-2-15.pdf:/content"));
        var mail = JsonSerializer.SerializeToElement(graph.LastBody);
        Assert.Equal("session-1", mail.GetProperty("message").GetProperty("subject").GetString());
        Assert.Equal("client@example.com", mail.GetProperty("message").GetProperty("toRecipients")[0]
            .GetProperty("emailAddress").GetProperty("address").GetString());
        var html = mail.GetProperty("message").GetProperty("body").GetProperty("content").GetString();
        Assert.Contains("Draft", html);
        Assert.Contains("https://example.com/generated.pdf", html);
        Assert.Contains("https://example.com/exhibit.pdf", html);
        Assert.DoesNotContain("@{", html);
    }

    [Fact]
    public async Task Qa_result_replies_with_a_generated_pdf()
    {
        var graph = new FakeFileGraph();
        var workflow = new EmailWorkflowService(new FakeAccounts(), graph, new GraphFiles(graph, "info@example.com", "sharepoint.example.com"),
            results: new FakeResults());

        await workflow.RespondAsync(Json("""{"type":"qa","status":"Generated","accountId":"case-a","messageId":"message-id","input":"What happened?","responseId":"0-9","outputUrl":"https://example.com/result.json"}"""), default);

        Assert.Contains(graph.Uploads, path => path.Contains("_GeneratedDocuments/qa/qa-0-9.pdf:/content"));
        Assert.Equal("POST users/mailbox-id/messages/message-id/reply", graph.Calls[^1]);
        var reply = JsonSerializer.SerializeToElement(graph.LastBody);
        Assert.Contains("What happened?", reply.GetProperty("message").GetProperty("body")
            .GetProperty("content").GetString());
    }

    [Fact]
    public async Task Failed_response_without_message_id_sends_to_explicit_recipient_once()
    {
        var graph = new FakeGraph();
        var deliveries = new FakeDeliveries();
        var workflow = new EmailWorkflowService(new FakeAccounts(), graph, new GraphFiles(graph, sharePointHost: "sharepoint.example.com"),
            deliveries: deliveries);
        var response = Json("""{"type":"declaration","status":"Failed","accountId":"case-a","responseId":"1-8","emailAddresses":["other@example.com"],"emailSubject":"Declaration request"}""");

        await workflow.RespondAsync(response, default);
        await workflow.RespondAsync(response, default);

        Assert.Equal(1, graph.Calls.Count(call => call == "POST users/mailbox-id/sendMail"));
        var mail = JsonSerializer.SerializeToElement(graph.LastBody);
        Assert.Equal("other@example.com", mail.GetProperty("message").GetProperty("toRecipients")[0]
            .GetProperty("emailAddress").GetProperty("address").GetString());
        Assert.Equal("Declaration request", mail.GetProperty("message").GetProperty("subject").GetString());
    }

    [Fact]
    public async Task Result_reader_rejects_a_url_outside_the_configured_container()
    {
        var reader = new BlobResultStore(new BlobContainerClient(
            new Uri("https://example.blob.core.windows.net/aimie-operations-results")));

        await Assert.ThrowsAsync<InvalidEventException>(() => reader.ReadAsync(
            "declaration", "case-a", "2-15", "https://other.example/result.json", default));
    }

    [Fact]
    public async Task Original_message_and_unsupported_attachment_are_converted_and_stored()
    {
        var graph = new FakeFileGraph();
        var files = new GraphFiles(graph, "info@example.com", "sharepoint.example.com");

        var drive = await files.StoreMessageAsync("case-a", "Email 2026-10-03", "<p>Hello</p>", default);
        await files.StoreAttachmentAsync("case-a", drive, "Email 2026-10-03", "notes.txt",
            "text/plain", [1, 2, 3], default);

        Assert.Equal("documents-drive", drive);
        Assert.Equal(2, graph.Downloads.Count);
        Assert.Equal(2, graph.Deletions.Count);
        Assert.Contains(graph.Uploads, path => path.Contains("_OriginalMessage.pdf:/content"));
        Assert.Contains(graph.Uploads, path => path.Contains("notes.txt.pdf:/content"));
    }

    [Fact]
    public async Task Generated_declaration_replies_with_a_sharepoint_edit_link()
    {
        var graph = new FakeGraph();
        var workflow = new EmailWorkflowService(new FakeAccounts(), graph, new GraphFiles(graph, sharePointHost: "sharepoint.example.com"));
        await workflow.RespondAsync(Json("""{"type":"declaration","status":"Generated","accountId":"case-a","messageId":"message-id","input":"Draft","attachments":[{"fileId":"file-1","name":"declaration.docx"}]}"""), default);

        Assert.Contains(graph.Calls, call => call.Contains("/driveItem/createLink"));
        Assert.Contains("POST users/mailbox-id/messages/message-id/reply", graph.Calls);
        var reply = JsonSerializer.SerializeToElement(graph.LastBody);
        var html = reply.GetProperty("message").GetProperty("body").GetProperty("content").GetString();
        Assert.Contains("https://example.com/edit", html);
        Assert.Contains("declaration.docx", html);
    }

    [Fact]
    public async Task Ingestion_stores_message_and_attachment_before_replying()
    {
        var graph = new FakeFileGraph();
        var workflow = new EmailWorkflowService(new FakeAccounts(), graph,
            new GraphFiles(graph, "info@example.com", "sharepoint.example.com"));

        await workflow.IngestAsync(Json("""{"accountId":"case-a","messageId":"message-id"}"""), default);

        Assert.Contains(graph.Uploads, path => path.Contains("_OriginalMessage.pdf:/content"));
        Assert.Contains(graph.Uploads, path => path.Contains("evidence.pdf:/content"));
        Assert.Equal("POST users/mailbox-id/messages/message-id/reply", graph.Calls[^1]);
    }

    [Fact]
    public async Task Communication_mail_uses_the_selected_sender_in_workflow_tenant()
    {
        var graph = new FakeGraph();
        var workflow = new EmailWorkflowService(new FakeAccounts(), graph,
            new GraphFiles(graph, sharePointHost: "sharepoint.example.com"), "info@example.org");

        await workflow.CommunicateAsync(Json("""{"eventKind":"Communication","status":"OnboardingComplete","accountId":"case-a"}"""), default);

        Assert.Contains("POST users/info%40example.org/sendMail", graph.Calls);
        var mail = JsonSerializer.SerializeToElement(graph.LastBody);
        var html = mail.GetProperty("message").GetProperty("body").GetProperty("content").GetString();
        Assert.Contains("Your site is ready", html);
        Assert.Contains("Your Account ID is case-a", html);
        Assert.DoesNotContain("@{", html);
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData("ShareRequest")]
    [InlineData("EmailIngestionComplete")]
    public async Task Communication_uses_environment_addresses_and_site_urls(string status)
    {
        using var settings = new CommunicationEnvironment();
        var graph = new FakeGraph();
        var workflow = new EmailWorkflowService(new FakeAccounts(), graph,
            new GraphFiles(graph, sharePointHost: "sharepoint.example.com"));

        await workflow.CommunicateAsync(JsonSerializer.SerializeToElement(new
        {
            eventKind = "Communication", status, accountId = "case-a", emailAddress = "recipient@example.org"
        }), default);

        Assert.Contains("POST users/sender%40example.net/sendMail", graph.Calls);
        var mail = JsonSerializer.SerializeToElement(graph.LastBody);
        var html = mail.GetProperty("message").GetProperty("body").GetProperty("content").GetString();
        Assert.Contains("https://portal.example.net/app?audience=client&amp;mode=shared", html);
        Assert.Contains("Example &amp; Team", html);
        Assert.DoesNotContain("@{", html);
        if (status == "ShareRequest")
        {
            Assert.Contains("href=\"https://accounts.example.net/create?type=client&amp;lang=en\"", html);
            Assert.Contains("src=\"https://cdn.example.net/logo.png?v=1&amp;format=png\"", html);
            Assert.Contains("help+sharing@example.net", html);
            Assert.Contains("mailto:help%2Bsharing%40example.net", html);
        }
    }

    [Theory]
    [InlineData("EMAIL_SITE_URL", "javascript:alert(1)")]
    [InlineData("EMAIL_ONBOARDING_URL", "/onboarding")]
    [InlineData("EMAIL_LOGO_URL", "https://user:password@cdn.example.org/logo.png")]
    [InlineData("EMAIL_SITE_URL", "")]
    public async Task Communication_rejects_invalid_or_missing_urls_before_sending(string key, string value)
    {
        using var settings = new CommunicationEnvironment();
        Environment.SetEnvironmentVariable(key, value);
        var graph = new FakeGraph();
        var workflow = new EmailWorkflowService(new FakeAccounts(), graph,
            new GraphFiles(graph, sharePointHost: "sharepoint.example.com"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.CommunicateAsync(
            Json("""{"status":"ShareRequest","accountId":"case-a","emailAddress":"recipient@example.org"}"""), default));

        Assert.Contains(key, error.Message);
        Assert.DoesNotContain(graph.Calls, call => call.EndsWith("/sendMail"));
    }

    private sealed class CommunicationEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> previous = [];

        public CommunicationEnvironment()
        {
            var values = new Dictionary<string, string>
            {
                ["GRAPH_SENDER_USER"] = "sender@example.net",
                ["EMAIL_SUPPORT_ADDRESS"] = "help+sharing@example.net",
                ["EMAIL_BRAND_NAME"] = "Example & Team",
                ["EMAIL_SITE_URL"] = "https://portal.example.net/app?audience=client&mode=shared",
                ["EMAIL_ONBOARDING_URL"] = "https://accounts.example.net/create?type=client&lang=en",
                ["EMAIL_LOGO_URL"] = "https://cdn.example.net/logo.png?v=1&format=png"
            };
            foreach (var (key, value) in values)
            {
                previous[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, value);
            }
        }

        public void Dispose()
        {
            foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value);
        }
    }

    private sealed class FakeResults(string sourceHost = "sharepoint.example.com") : IResultStore
    {
        public int Reads { get; private set; }
        public Task<JsonElement> ReadAsync(string type, string accountId, string responseId,
            string outputUrl, CancellationToken token)
        {
            Reads++;
            Assert.Equal("case-a", accountId);
            Assert.Equal("https://example.com/result.json", outputUrl);
            return Task.FromResult(Json("""{"document":"# Indexed declaration","exhibits":[{"firstIndex":1,"doc":{"title":"Exhibit A","pages":[2,3],"pageContent":"Evidence body","sourceUrl":"https://SOURCE_HOST/sites/case-a/Shared%20Documents/evidence.pdf"}}]}""".Replace("SOURCE_HOST", sourceHost)));
        }
    }

    private sealed class FakeDeliveries : IResponseDeliveryStore
    {
        private readonly HashSet<string> sent = [];
        public Task<bool> WasDeliveredAsync(string accountId, string type, string responseId,
            CancellationToken token) => Task.FromResult(sent.Contains($"{accountId}/{type}/{responseId}"));
        public Task MarkDeliveredAsync(string accountId, string type, string responseId,
            CancellationToken token)
        {
            sent.Add($"{accountId}/{type}/{responseId}");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAccounts : IAccountRepository
    {
        private static readonly AccountDetails Account = new()
        {
            AccountId = "case-a", IngestionServiceAccount = "case-a@example.org",
            PersonInformation = new PersonDetails { EmailAddress = "client@example.com" },
            AttorneyInformation = [new AttorneyDetails { EmailAddress = "lawyer@example.com" }]
        };
        public Task<IReadOnlyList<AccountDetails>> GetActiveAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<AccountDetails>>([Account]);
        public Task<AccountDetails> GetAsync(string accountId, CancellationToken token) => Task.FromResult(Account);
        public Task<string?> GetBillingCodeAsync(string accountId, CancellationToken token) => Task.FromResult<string?>(null);
    }

    private sealed class FakeGraph : IGraphClient
    {
        public List<string> Calls { get; } = [];
        public object? LastBody { get; private set; }

        public Task<JsonElement> GetAsync(string path, CancellationToken token)
        {
            Calls.Add("GET " + path);
            if (path.Contains("/messages/message-id", StringComparison.Ordinal))
                return Task.FromResult(Json("""{"body":{"contentType":"html","content":"<p>Draft a declaration</p>"}}"""));
            if (path.StartsWith("sites/", StringComparison.Ordinal))
                return Task.FromResult(Json("""{"id":"site-id"}"""));
            throw new InvalidOperationException(path);
        }
        public Task<IReadOnlyList<JsonElement>> GetAllAsync(string path, CancellationToken token)
        {
            Calls.Add("GETALL " + path);
            if (path.StartsWith("users?", StringComparison.Ordinal))
                return Task.FromResult<IReadOnlyList<JsonElement>>([Json("""{"id":"mailbox-id"}""")]);
            if (path.Contains("/messages?", StringComparison.Ordinal))
                return Task.FromResult<IReadOnlyList<JsonElement>>([Json("""{"id":"message-id","toRecipients":[{"emailAddress":{"address":"case-a+declaration@example.org"}}]}""")]);
            if (path.Contains("/lists?", StringComparison.Ordinal))
                return Task.FromResult<IReadOnlyList<JsonElement>>([Json("""{"id":"list-id","displayName":"Documents"}""")]);
            throw new InvalidOperationException(path);
        }
        public Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken token)
        {
            Calls.Add($"{method.Method} {path}");
            LastBody = body;
            if (path.EndsWith("/driveItem/createLink", StringComparison.Ordinal))
                return Task.FromResult(Json("""{"link":{"webUrl":"https://example.com/edit"}}"""));
            return Task.FromResult(default(JsonElement));
        }
        public Task<byte[]> GetBytesAsync(string path, CancellationToken token) => throw new NotImplementedException();
        public Task<JsonElement> PutBytesAsync(string path, byte[] bytes, string contentType, CancellationToken token) => throw new NotImplementedException();
        public Task<JsonElement> UploadAsync(string path, byte[] bytes, string contentType, CancellationToken token) => throw new NotImplementedException();
        public Task DeleteAsync(string path, CancellationToken token) => throw new NotImplementedException();
    }

    private sealed class RecordingPublisher(FakeGraph graph) : IEventPublisher
    {
        public List<object> Events { get; } = [];
        public Task PublishAsync(object content, CancellationToken token)
        {
            graph.Calls.Add("publish");
            Events.Add(content);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeFileGraph : IGraphClient
    {
        public List<string> Calls { get; } = [];
        public List<string> Uploads { get; } = [];
        public List<string> Downloads { get; } = [];
        public List<string> Deletions { get; } = [];
        public List<string> HtmlUploads { get; } = [];
        public object? LastBody { get; private set; }
        public Task<JsonElement> GetAsync(string path, CancellationToken token)
        {
            Calls.Add("GET " + path);
            if (path.StartsWith("sites/", StringComparison.Ordinal)) return Task.FromResult(Json("""{"id":"site-id"}"""));
            if (path.Contains("/messages/", StringComparison.Ordinal))
                return Task.FromResult(Json("""{"receivedDateTime":"2026-10-03T12:00:00Z","body":{"content":"<p>Hello</p>"}}"""));
            if (path.StartsWith("users/", StringComparison.Ordinal)) return Task.FromResult(Json("""{"id":"conversion-drive"}"""));
            if (path.Contains("/root:/", StringComparison.Ordinal)) return Task.FromResult(Json("""{"id":"folder-id"}"""));
            throw new InvalidOperationException(path);
        }
        public Task<IReadOnlyList<JsonElement>> GetAllAsync(string path, CancellationToken token)
        {
            Calls.Add("GETALL " + path);
            if (path.StartsWith("users?", StringComparison.Ordinal))
                return Task.FromResult<IReadOnlyList<JsonElement>>([Json("""{"id":"mailbox-id"}""")]);
            if (path.EndsWith("/attachments", StringComparison.Ordinal))
                return Task.FromResult<IReadOnlyList<JsonElement>>([Json("""{"name":"evidence.pdf","contentType":"application/pdf","contentBytes":"AQID"}""")]);
            return Task.FromResult<IReadOnlyList<JsonElement>>([Json("""{"id":"documents-drive","name":"Documents"}""")]);
        }
        public Task<JsonElement> UploadAsync(string path, byte[] bytes, string contentType, CancellationToken token)
        {
            Uploads.Add(path);
            Calls.Add("UPLOAD " + path);
            if (path.EndsWith(".html:/content", StringComparison.Ordinal))
                HtmlUploads.Add(System.Text.Encoding.UTF8.GetString(bytes));
            return Task.FromResult(path.Contains("Exhibit-", StringComparison.Ordinal) &&
                path.EndsWith(".pdf:/content", StringComparison.Ordinal)
                ? Json("""{"id":"exhibit-item"}""") : Json("""{"id":"temporary-item"}"""));
        }
        public Task<byte[]> GetBytesAsync(string path, CancellationToken token)
        {
            Downloads.Add(path);
            Calls.Add("DOWNLOAD " + path);
            return Task.FromResult<byte[]>([37, 80, 68, 70]);
        }
        public Task DeleteAsync(string path, CancellationToken token)
        {
            Deletions.Add(path);
            Calls.Add("DELETE " + path);
            return Task.CompletedTask;
        }
        public Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken token)
        {
            Calls.Add($"{method.Method} {path}");
            LastBody = body;
            if (path.EndsWith("/createLink", StringComparison.Ordinal))
                return Task.FromResult(path.Contains("exhibit-item", StringComparison.Ordinal)
                    ? Json("""{"link":{"webUrl":"https://example.com/exhibit.pdf"}}""")
                    : Json("""{"link":{"webUrl":"https://example.com/generated.pdf"}}"""));
            return Task.FromResult(default(JsonElement));
        }
        public Task<JsonElement> PutBytesAsync(string path, byte[] bytes, string contentType, CancellationToken token) => throw new NotImplementedException();
    }
}
