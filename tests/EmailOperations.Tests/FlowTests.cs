using System.Text.Json;
using EmailOperations;
using EmailOperations.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EmailOperations.Tests;

public class FlowTests
{
    [Fact]
    public async Task Identification_polls_graph_and_publishes_to_the_correct_hubs()
    {
        var workflow = new FakeWorkflows();
        var aimie = new FakePublisher();
        var email = new FakePublisher();
        var worker = new IdentificationWorker(workflow, new IdentificationPublisher(aimie, email),
            new FakeLifetime(), NullLogger<IdentificationWorker>.Instance);
        await worker.RunOnceAsync(default);
        Assert.Equal(1, workflow.IdentifyCalls);
        Assert.Single(aimie.Messages);
        Assert.Single(email.Messages);
    }

    [Fact]
    public async Task Identification_job_polls_once_and_stops_the_host()
    {
        var workflow = new FakeWorkflows();
        var lifetime = new FakeLifetime();
        var worker = new IdentificationWorker(workflow,
            new IdentificationPublisher(new FakePublisher(), new FakePublisher()),
            lifetime, NullLogger<IdentificationWorker>.Instance);

        await worker.StartAsync(default);
        await worker.ExecuteTask!;

        Assert.Equal(1, workflow.IdentifyCalls);
        Assert.True(lifetime.StopCalled);
    }

    [Fact]
    public async Task Identification_job_failure_exits_nonzero_for_kubernetes()
    {
        var originalExitCode = Environment.ExitCode;
        try
        {
            Environment.ExitCode = 0;
            var workflow = new FakeWorkflows { IdentifyFailure = new IOException("Graph unavailable") };
            var lifetime = new FakeLifetime();
            var worker = new IdentificationWorker(workflow,
                new IdentificationPublisher(new FakePublisher(), new FakePublisher()),
                lifetime, NullLogger<IdentificationWorker>.Instance);

            await worker.StartAsync(default);
            await worker.ExecuteTask!;

            Assert.Equal(1, Environment.ExitCode);
            Assert.True(lifetime.StopCalled);
        }
        finally { Environment.ExitCode = originalExitCode; }
    }

    [Fact]
    public async Task Builder_email_content_is_read_with_graph_and_sent_to_aimie()
    {
        var workflow = new FakeWorkflows { Text = "Build an argument" };
        var requests = new FakePublisher();
        var handler = new EmailEventHandler("argument-builder", workflow, requests);
        await handler.HandleAsync(Json("""{"type":"argument-builder","accountId":"a","messageId":"m"}"""),
            "2", 42, default);
        Assert.Equal(1, workflow.TextCalls);
        var sent = JsonSerializer.SerializeToElement(requests.Messages.Single());
        Assert.Equal("Build an argument", Events.String(sent, "input"));
        Assert.Equal("2-42", Events.String(sent, "sourceEventId"));
    }

    [Fact]
    public async Task Test_email_processing_event_invokes_graph_ingestion()
    {
        var workflow = new FakeWorkflows();
        var handler = new EmailEventHandler("email-data-ingestion", workflow, null);
        await handler.HandleAsync(Json("""{"eventKind":"EmailProcessing","accountId":"a","messageId":"m"}"""),
            "0", 1, default);
        Assert.Equal(1, workflow.IngestCalls);
        Assert.Equal("a", workflow.LastAccountId);
    }

    [Fact]
    public async Task Declaration_response_accepts_a_generated_event_without_message_id()
    {
        var workflow = new FakeWorkflows();
        var handler = new EmailEventHandler("email-declaration-response", workflow, null);
        await handler.HandleAsync(Json("""{"type":"declaration","status":"Generated","accountId":"a"}"""),
            "0", 1, default);
        Assert.Equal(1, workflow.ResponseCalls);
    }

    [Fact]
    public async Task Qa_response_reaches_email_workflow()
    {
        var workflow = new FakeWorkflows();
        var handler = new EmailEventHandler("email-declaration-response", workflow, null);
        await handler.HandleAsync(Json("""{"type":"qa","status":"Generated","accountId":"a","responseId":"2-6"}"""),
            "0", 1, default);
        Assert.Equal(1, workflow.ResponseCalls);
    }

    [Fact]
    public async Task Communication_hub_accepts_the_Test_router_event_shape()
    {
        var workflow = new FakeWorkflows();
        var handler = new CommunicationHandler(workflow);
        await handler.HandleAsync(Json("""{"eventKind":"Communication","accountId":"a","status":"ShareRequest"}"""),
            "0", 1, default);
        Assert.Equal(1, workflow.CommunicationCalls);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private sealed class FakeWorkflows : IEmailWorkflowService
    {
        public int IdentifyCalls, IngestCalls, ResponseCalls, CommunicationCalls, TextCalls;
        public Exception? IdentifyFailure { get; set; }
        public string? LastAccountId { get; private set; }
        public string Text { get; set; } = "message text";
        public async Task IdentifyAsync(IEventPublisher publisher, CancellationToken token)
        {
            IdentifyCalls++;
            if (IdentifyFailure is not null) throw IdentifyFailure;
            await publisher.PublishAsync(new { type = "declaration", accountId = "a", messageId = "m", input = "draft" }, token);
            await publisher.PublishAsync(new { type = "email-data-ingestion", accountId = "a", messageId = "m" }, token);
        }
        public Task IngestAsync(JsonElement content, CancellationToken token)
        { IngestCalls++; LastAccountId = Events.String(content, "accountId"); return Task.CompletedTask; }
        public Task RespondAsync(JsonElement content, CancellationToken token) { ResponseCalls++; return Task.CompletedTask; }
        public Task CommunicateAsync(JsonElement content, CancellationToken token) { CommunicationCalls++; return Task.CompletedTask; }
        public Task<string> MessageTextAsync(string accountId, string messageId, CancellationToken token)
        { TextCalls++; return Task.FromResult(Text); }
    }

    private sealed class FakePublisher : IEventPublisher
    {
        public List<object> Messages { get; } = [];
        public Task PublishAsync(object content, CancellationToken token)
        { Messages.Add(content); return Task.CompletedTask; }
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public bool StopCalled { get; private set; }
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => StopCalled = true;
    }
}
