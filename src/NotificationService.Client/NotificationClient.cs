using System.Net;
using System.Text;

namespace NotificationService;

/// <summary>Sends notices using a caller-owned HttpClient whose BaseAddress is the service root.</summary>
public sealed class NotificationClient(HttpClient httpClient)
{
    public async Task SendAsync(Notice notice, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notice);
        using var content = new StringContent(notice.ToJson(), Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync("api/sendNotice", content, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new NotificationTransmissionException(response.StatusCode, body);
        }
    }
}

public sealed class NotificationTransmissionException(HttpStatusCode statusCode, string responseBody)
    : HttpRequestException($"NotificationService returned HTTP {(int)statusCode}.", null, statusCode)
{
    public string ResponseBody { get; } = responseBody;
}
