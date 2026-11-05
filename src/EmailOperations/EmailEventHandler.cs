using System.Text.Json;
using EmailOperations.Core;

namespace EmailOperations;

public sealed class EmailEventHandler(string role, IEmailWorkflowService workflows, IEventPublisher? requests) : IEventHandler
{
    public async Task HandleAsync(JsonElement content, string partition, long sequence, CancellationToken token)
    {
        switch (role)
        {
            case "email-data-ingestion":
                if (!Events.Matches(content, "email-data-ingestion") &&
                    Events.String(content, "eventKind") != "EmailProcessing") return;
                Events.Required(content, "accountId");
                Events.Required(content, "messageId");
                await workflows.IngestAsync(content, token);
                break;
            case "email-declaration-response":
                if (Events.String(content, "type") is not ("declaration" or "qa")) return;
                if (Events.String(content, "status") is not ("Generated" or "Failed")) return;
                Events.Required(content, "accountId");
                await workflows.RespondAsync(content, token);
                break;
            case "argument-builder" or "timeline-builder":
                if (!Events.Matches(content, role)) return;
                await PublishBuilderRequestAsync(content, partition, sequence, token);
                break;
            default:
                throw new InvalidOperationException($"Unsupported email role: {role}");
        }
    }

    private async Task PublishBuilderRequestAsync(JsonElement content, string partition, long sequence,
        CancellationToken token)
    {
        var accountId = Events.Required(content, "accountId");
        var messageId = Events.Required(content, "messageId");
        var input = Events.String(content, "question") ?? Events.String(content, "input");
        if (string.IsNullOrWhiteSpace(input))
        {
            input = await workflows.MessageTextAsync(accountId, messageId, token);
        }
        await (requests ?? throw new InvalidOperationException("AIMIE request publisher is missing"))
            .PublishAsync(new
            {
                type = role, accountId, messageId, input,
                sourceEventId = $"{partition}-{sequence}",
                sessionId = Events.String(content, "sessionId"),
                conversationId = Events.String(content, "conversationId"),
                emailSubject = Events.String(content, "emailSubject"),
                oid = Events.String(content, "oid")
            }, token);
    }
}
