using EmailOperations;
using EmailOperations.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
Settings.Configure(builder.Configuration);
var role = Settings.Required("ROLE");
builder.Services.AddHttpClient<IGraphClient, GraphClient>();
builder.Services.AddSingleton<IAccountRepository, AccountRepository>();
builder.Services.AddSingleton<GraphFiles>();
if (role == "email-declaration-response")
{
    builder.Services.AddSingleton<IResultStore, BlobResultStore>();
    builder.Services.AddSingleton<IResponseDeliveryStore, CosmosResponseDeliveryStore>();
}
builder.Services.AddSingleton<IEmailWorkflowService, EmailWorkflowService>();
if (role == "email-message-identification")
{
    builder.Services.AddSingleton<IEventPublisher>(_ =>
        new IdentificationPublisher(
            new EventPublisher(Settings.Required("AIMIE_REQUEST_EVENTHUB_NAME")),
            new EventPublisher(Settings.Required("EMAIL_OPERATIONS_EVENTHUB_NAME"))));
    builder.Services.AddHostedService<IdentificationWorker>();
}
else if (role is "email-data-ingestion" or "email-declaration-response" or
         "argument-builder" or "timeline-builder" or "communication-hub")
{
    if (role is "argument-builder" or "timeline-builder")
        builder.Services.AddSingleton<IEventPublisher>(_ =>
            new EventPublisher(Settings.Required("AIMIE_REQUEST_EVENTHUB_NAME")));
    if (role == "communication-hub")
        builder.Services.AddSingleton<IEventHandler, CommunicationHandler>();
    else
        builder.Services.AddSingleton<IEventHandler>(services => new EmailEventHandler(role,
            services.GetRequiredService<IEmailWorkflowService>(), services.GetService<IEventPublisher>()));
    builder.Services.AddHostedService<EventWorker>();
}
else throw new InvalidOperationException($"Unsupported ROLE: {role}");
await builder.Build().RunAsync();
return Environment.ExitCode;
