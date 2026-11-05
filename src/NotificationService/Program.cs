using Azure.Messaging.WebPubSub;
using Microsoft.Azure.Cosmos;
using NotificationService;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("/mnt/keyvault/notification-appsettings.json", optional: true, reloadOnChange: false);
if (Environment.GetEnvironmentVariable("APPSETTINGS_FILE") is { Length: > 0 } settingsFile)
    builder.Configuration.AddJsonFile(settingsFile, optional: false, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();

string Required(string key) => builder.Configuration[key] is { Length: > 0 } value
    ? value : throw new InvalidOperationException($"Missing required setting {key}");

var endpoint = Required("COSMOS_ENDPOINT");
var cosmosKey = Required("COSMOS_KEY");
var pubSubConnection = Required("WEB_PUBSUB_CONNECTION_STRING");
var database = builder.Configuration["NOTIFICATION_DATABASE"] ?? "NotificationsDB";
var container = builder.Configuration["NOTIFICATION_CONTAINER"] ?? "notices";
var hub = builder.Configuration["WEB_PUBSUB_HUB"] ?? "notifications";

builder.Services.AddSingleton(new CosmosClient(endpoint, cosmosKey));
builder.Services.AddSingleton(new WebPubSubServiceClient(pubSubConnection, hub));
builder.Services.AddSingleton<INoticeStore>(services =>
    new CosmosNoticeStore(services.GetRequiredService<CosmosClient>(), database, container));
builder.Services.AddSingleton<INoticeBroadcaster, WebPubSubNoticeBroadcaster>();
builder.Services.AddSingleton<NoticeService>();

var app = builder.Build();
app.MapGet("/healthz", () => Results.Ok());
app.MapGet("/openapi/v1.json", () => Results.Stream(
    typeof(NoticeEndpoints).Assembly.GetManifestResourceStream("NotificationService.openapi.json")!,
    "application/json"));
app.MapPost("/api/sendNotice", NoticeEndpoints.SendNotice);
app.Run();

public partial class Program;
