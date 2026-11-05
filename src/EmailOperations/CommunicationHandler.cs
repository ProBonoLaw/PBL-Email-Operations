using System.Text.Json;
using EmailOperations.Core;

namespace EmailOperations;

public sealed class CommunicationHandler(IEmailWorkflowService workflows) : IEventHandler
{
    public async Task HandleAsync(JsonElement content, string partition, long sequence, CancellationToken token)
    {
        if (!Events.Matches(content, "communication") &&
            Events.String(content, "eventKind") != "Communication") return;
        Events.Required(content, "accountId");
        await workflows.CommunicateAsync(content, token);
    }
}
