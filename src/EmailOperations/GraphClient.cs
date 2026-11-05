using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using EmailOperations.Core;

namespace EmailOperations;

public interface IGraphClient
{
    Task<JsonElement> GetAsync(string path, CancellationToken token);
    Task<byte[]> GetBytesAsync(string path, CancellationToken token);
    Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken token);
    Task<JsonElement> PutBytesAsync(string path, byte[] bytes, string contentType, CancellationToken token);
    Task<JsonElement> UploadAsync(string path, byte[] bytes, string contentType, CancellationToken token);
    Task DeleteAsync(string path, CancellationToken token);
    Task<IReadOnlyList<JsonElement>> GetAllAsync(string path, CancellationToken token);
}

public sealed class GraphClient(HttpClient http) : IGraphClient
{
    private TokenCredential? credential;
    private readonly SemaphoreSlim credentialLock = new(1, 1);
    private const string Scope = "https://graph.microsoft.com/.default";

    private async Task<TokenCredential> CredentialAsync(CancellationToken token)
    {
        if (credential is not null) return credential;
        await credentialLock.WaitAsync(token);
        try
        {
            if (credential is not null) return credential;
            var clientId = Settings.Required("GRAPH_CLIENT_ID");
            var tenantId = Settings.Required("GRAPH_TENANT_ID");
            var secret = Settings.Get("GRAPH_CLIENT_SECRET");
            if (string.IsNullOrWhiteSpace(secret))
            {
                var vault = new SecretClient(new Uri(Settings.Required("GRAPH_KEYVAULT_URI")), Settings.Credential);
                secret = (await vault.GetSecretAsync(Settings.Required("GRAPH_SECRET_NAME"),
                    cancellationToken: token)).Value.Value;
            }
            return credential = new ClientSecretCredential(tenantId, clientId, secret);
        }
        finally { credentialLock.Release(); }
    }

    private async Task<HttpResponseMessage> RequestAsync(HttpMethod method, string path,
        HttpContent? body, CancellationToken token)
    {
        var url = new Uri(new Uri("https://graph.microsoft.com/v1.0/"), path.TrimStart('/'));
        if (url.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(url.Host, "graph.microsoft.com", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Graph request URL must use graph.microsoft.com", nameof(path));
        using var request = new HttpRequestMessage(method, url);
        var accessToken = await (await CredentialAsync(token)).GetTokenAsync(
            new TokenRequestContext([Scope]), token);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Content = body;
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(token);
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"Graph {method} {path} returned {status}: {detail}",
                null, response.StatusCode);
        }
        return response;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, CancellationToken token)
    {
        using (response)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(token);
            if (bytes.Length == 0) return default;
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.Clone();
        }
    }

    public async Task<JsonElement> GetAsync(string path, CancellationToken token) =>
        await JsonAsync(await RequestAsync(HttpMethod.Get, path, null, token), token);

    public async Task<byte[]> GetBytesAsync(string path, CancellationToken token)
    {
        using var response = await RequestAsync(HttpMethod.Get, path, null, token);
        return await response.Content.ReadAsByteArrayAsync(token);
    }

    public async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken token)
    {
        using var content = body is null ? null : JsonContent.Create(body);
        return await JsonAsync(await RequestAsync(method, path, content, token), token);
    }

    public async Task<JsonElement> PutBytesAsync(string path, byte[] bytes, string contentType,
        CancellationToken token)
    {
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return await JsonAsync(await RequestAsync(HttpMethod.Put, path, content, token), token);
    }

    public async Task<JsonElement> UploadAsync(string path, byte[] bytes, string contentType,
        CancellationToken token)
    {
        if (bytes.Length <= 4 * 1024 * 1024) return await PutBytesAsync(path, bytes, contentType, token);
        var sessionPath = path.EndsWith("/content", StringComparison.Ordinal)
            ? path[..^"/content".Length] + "/createUploadSession" :
            throw new ArgumentException("Upload path must end in /content", nameof(path));
        var session = await SendAsync(HttpMethod.Post, sessionPath,
            new { item = new Dictionary<string, object> { ["@microsoft.graph.conflictBehavior"] = "replace" } }, token);
        var uploadUrl = session.GetProperty("uploadUrl").GetString()!;
        const int chunkSize = 5 * 1024 * 1024;
        JsonElement result = default;
        for (var offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            var count = Math.Min(chunkSize, bytes.Length - offset);
            using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl);
            request.Content = new ByteArrayContent(bytes, offset, count);
            request.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + count - 1, bytes.Length);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            using var response = await http.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            if (offset + count == bytes.Length)
                result = await JsonAsync(response, token);
        }
        return result;
    }

    public async Task DeleteAsync(string path, CancellationToken token)
    {
        using var response = await RequestAsync(HttpMethod.Delete, path, null, token);
    }

    public async Task<IReadOnlyList<JsonElement>> GetAllAsync(string path, CancellationToken token)
    {
        var result = new List<JsonElement>();
        while (!string.IsNullOrEmpty(path))
        {
            var page = await GetAsync(path, token);
            if (page.TryGetProperty("value", out var values))
                result.AddRange(values.EnumerateArray().Select(value => value.Clone()));
            path = page.TryGetProperty("@odata.nextLink", out var next)
                ? next.GetString()!
                : "";
        }
        return result;
    }
}
