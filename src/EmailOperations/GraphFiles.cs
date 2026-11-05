using System.Net;
using System.Text;
using System.Text.Json;
using EmailOperations.Core;
using Markdig;

namespace EmailOperations;

public sealed class GraphFiles(IGraphClient graph, string? conversionUser = null,
    string? sharePointHost = null)
{
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .DisableHtml().UseAdvancedExtensions().Build();
    private static string Segment(string value) => Uri.EscapeDataString(value);
    private static string Path(IEnumerable<string> parts) => string.Join('/', parts.Select(Segment));

    public string SharePointHost
    {
        get
        {
            var host = sharePointHost ?? Settings.Required("GRAPH_SHAREPOINT_HOST");
            if (Uri.CheckHostName(host) != UriHostNameType.Dns)
                throw new InvalidOperationException("GRAPH_SHAREPOINT_HOST must be a DNS hostname");
            return host;
        }
    }

    private async Task<string> SiteIdAsync(string accountId, CancellationToken token)
    {
        var host = SharePointHost;
        var site = await graph.GetAsync($"sites/{host}:/sites/{Segment(accountId)}?$select=id", token);
        return site.GetProperty("id").GetString()!;
    }

    private async Task<string> DocumentsDriveAsync(string accountId, CancellationToken token)
    {
        var siteId = await SiteIdAsync(accountId, token);
        var drives = await graph.GetAllAsync($"sites/{Segment(siteId)}/drives?$select=id,name", token);
        var drive = drives.SingleOrDefault(item =>
            string.Equals(item.GetProperty("name").GetString(), "Documents", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.GetProperty("name").GetString(), "Shared Documents", StringComparison.OrdinalIgnoreCase));
        return drive.ValueKind == JsonValueKind.Object ? drive.GetProperty("id").GetString()! :
            throw new InvalidDataException($"Documents drive is missing from SharePoint site {accountId}");
    }

    private async Task<string> ConversionDriveAsync(CancellationToken token)
    {
        var user = conversionUser ?? Settings.Required("GRAPH_CONVERSION_DRIVE_USER");
        var drive = await graph.GetAsync($"users/{Segment(user)}/drive?$select=id", token);
        return drive.GetProperty("id").GetString()!;
    }

    private async Task EnsureFolderAsync(IGraphClient target, string driveId, string path, CancellationToken token)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            var current = Path(segments.Take(index + 1));
            try { await target.GetAsync($"drives/{Segment(driveId)}/root:/{current}?$select=id", token); }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
            {
                var parent = index == 0 ? $"drives/{Segment(driveId)}/root/children" :
                    $"drives/{Segment(driveId)}/root:/{Path(segments.Take(index))}:/children";
                try
                {
                    await target.SendAsync(HttpMethod.Post, parent, new Dictionary<string, object>
                    {
                        ["name"] = segments[index], ["folder"] = new { },
                        ["@microsoft.graph.conflictBehavior"] = "fail"
                    }, token);
                }
                catch (HttpRequestException conflict) when (conflict.StatusCode == HttpStatusCode.Conflict) { }
            }
        }
    }

    public async Task<string> StoreMessageAsync(string accountId, string folderName,
        string html, CancellationToken token)
    {
        var documents = await DocumentsDriveAsync(accountId, token);
        var folder = $"Emails/{folderName}";
        await EnsureFolderAsync(graph, documents, folder, token);
        var pdf = await ConvertAsync($"{accountId}-{folderName}", "_OriginalMessage.html",
            Encoding.UTF8.GetBytes(html), "text/html", token);
        await graph.UploadAsync($"drives/{Segment(documents)}/root:/{Path([.. folder.Split('/'), "_OriginalMessage.pdf"])}:/content",
            pdf, "application/pdf", token);
        return documents;
    }

    public async Task StoreAttachmentAsync(string accountId, string documentsDrive, string folderName,
        string name, string contentType, byte[] bytes, CancellationToken token)
    {
        var supported = contentType is "application/pdf" or
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" or
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" or
            "image/jpeg" or "image/png" or "image/gif";
        var uploadName = name;
        if (!supported)
        {
            bytes = await ConvertAsync($"{accountId}-{folderName}", $"Temp_{name}", bytes, contentType, token);
            uploadName += ".pdf";
            contentType = "application/pdf";
        }
        var folder = $"Emails/{folderName}";
        await graph.UploadAsync($"drives/{Segment(documentsDrive)}/root:/{Path([.. folder.Split('/'), uploadName])}:/content",
            bytes, contentType, token);
    }

    private async Task<byte[]> ConvertAsync(string folderName, string name, byte[] bytes,
        string contentType, CancellationToken token)
    {
        var driveId = await ConversionDriveAsync(token);
        var folder = $"Attachments/{folderName}";
        await EnsureFolderAsync(graph, driveId, folder, token);
        var item = await graph.UploadAsync($"drives/{Segment(driveId)}/root:/{Path([.. folder.Split('/'), name])}:/content",
            bytes, contentType, token);
        var itemId = item.GetProperty("id").GetString()!;
        try { return await graph.GetBytesAsync($"drives/{Segment(driveId)}/items/{Segment(itemId)}/content?format=pdf", token); }
        finally { await graph.DeleteAsync($"drives/{Segment(driveId)}/items/{Segment(itemId)}", token); }
    }

    public Task<string> StoreGeneratedPdfAsync(string accountId, string type,
        string responseId, string markdown, CancellationToken token) =>
        StoreMarkdownPdfAsync(accountId, type, responseId, $"{type}-{responseId}.pdf", markdown, token);

    public Task<string> StoreExhibitPdfAsync(string accountId, string type,
        string responseId, int index, string markdown, CancellationToken token) =>
        StoreMarkdownPdfAsync(accountId, type, responseId,
            $"Exhibit-{index}-{responseId}.pdf", markdown, token);

    private async Task<string> StoreMarkdownPdfAsync(string accountId, string type,
        string responseId, string fileName, string markdown, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            throw new InvalidDataException("Generated result has no document");
        var html = new StringBuilder("<html><meta charset=\"utf-8\"><body>");
        html.Append(Markdown.ToHtml(markdown, MarkdownPipeline));
        html.Append("</body></html>");
        var driveId = await DocumentsDriveAsync(accountId, token);
        var folder = $"_GeneratedDocuments/{type}";
        await EnsureFolderAsync(graph, driveId, folder, token);
        var pdf = await ConvertAsync($"{accountId}-{type}-{responseId}",
            $"{fileName}.html", Encoding.UTF8.GetBytes(html.ToString()), "text/html", token);
        var item = await graph.UploadAsync(
            $"drives/{Segment(driveId)}/root:/{Path([.. folder.Split('/'), fileName])}:/content",
            pdf, "application/pdf", token);
        var itemId = item.GetProperty("id").GetString()!;
        var link = await graph.SendAsync(HttpMethod.Post,
            $"drives/{Segment(driveId)}/items/{Segment(itemId)}/createLink",
            new { type = "view", scope = "anonymous" }, token);
        return link.GetProperty("link").GetProperty("webUrl").GetString()!;
    }

    public async Task<string> CreateEditLinkAsync(string accountId, string fileId, CancellationToken token)
    {
        var siteId = await SiteIdAsync(accountId, token);
        var lists = await graph.GetAllAsync($"sites/{Segment(siteId)}/lists?$select=id,displayName", token);
        var list = lists.SingleOrDefault(item =>
            string.Equals(item.GetProperty("displayName").GetString(), "Documents", StringComparison.OrdinalIgnoreCase));
        if (list.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Documents list is missing");
        var listId = list.GetProperty("id").GetString()!;
        var link = await graph.SendAsync(HttpMethod.Post,
            $"sites/{Segment(siteId)}/lists/{Segment(listId)}/items/{Segment(fileId)}/driveItem/createLink",
            new { type = "edit", scope = "anonymous" }, token);
        return link.GetProperty("link").GetProperty("webUrl").GetString()!;
    }
}
