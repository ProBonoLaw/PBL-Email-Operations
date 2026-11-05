using Azure.Identity;
using Azure.Messaging.EventHubs;
using Microsoft.Extensions.Configuration;

namespace EmailOperations.Core;

public static class Settings
{
    private static IConfiguration? configuration;

    public static void Configure(IConfigurationManager manager)
    {
        var localFile = Environment.GetEnvironmentVariable("APPSETTINGS_FILE");
        manager.AddJsonFile(localFile ?? "/mnt/keyvault/appsettings.json",
            optional: localFile is null, reloadOnChange: true);
        manager.AddEnvironmentVariables();
        configuration = manager;
    }

    public static string? Get(string key) => configuration?[key] ?? Environment.GetEnvironmentVariable(key);

    public static string Required(string key) => !string.IsNullOrWhiteSpace(Get(key))
        ? Get(key)! : throw new InvalidOperationException($"Missing required setting {key}");

    public static Azure.Core.TokenCredential Credential { get; } = new DefaultAzureCredential();

    public static EventHubConnectionOptions ConnectionOptions()
    {
        var configured = Get("EVENTHUB_TRANSPORT_TYPE");
        if (string.IsNullOrWhiteSpace(configured)) return new EventHubConnectionOptions();
        if (!Enum.TryParse<EventHubsTransportType>(configured, true, out var transport) ||
            !Enum.IsDefined(transport))
            throw new InvalidOperationException("EVENTHUB_TRANSPORT_TYPE must be AmqpTcp or AmqpWebSockets");
        return new EventHubConnectionOptions { TransportType = transport };
    }
}
