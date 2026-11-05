using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EmailOperations.Core;

namespace EmailOperations;

public interface IEmailWorkflowService
{
    Task IdentifyAsync(IEventPublisher publisher, CancellationToken token);
    Task IngestAsync(JsonElement content, CancellationToken token);
    Task RespondAsync(JsonElement content, CancellationToken token);
    Task CommunicateAsync(JsonElement content, CancellationToken token);
    Task<string> MessageTextAsync(string accountId, string messageId, CancellationToken token);
}

public sealed class EmailWorkflowService(IAccountRepository accounts, IGraphClient graph,
    GraphFiles files, string? senderUser = null, IResultStore? results = null,
    IResponseDeliveryStore? deliveries = null) : IEmailWorkflowService
{
    private static string Segment(string value) => Uri.EscapeDataString(value);
    private static string Escape(string? value) => WebUtility.HtmlEncode(value ?? "");
    private static string ConfiguredUrl(string key)
    {
        var value = Settings.Required(key);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) ||
            (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp) ||
            string.IsNullOrWhiteSpace(url.Host) || !string.IsNullOrEmpty(url.UserInfo))
            throw new InvalidOperationException($"{key} must be an absolute HTTP or HTTPS URL without credentials");
        return Escape(url.AbsoluteUri);
    }
    private static string RenderTemplate(string fileName, IReadOnlyDictionary<string, string> values)
    {
        using var stream = typeof(EmailWorkflowService).Assembly.GetManifestResourceStream(
            $"EmailOperations.Templates.{fileName}") ?? throw new FileNotFoundException(fileName);
        using var reader = new StreamReader(stream);
        var html = reader.ReadToEnd();
        foreach (var (key, value) in values) html = html.Replace(key, value, StringComparison.Ordinal);
        if (Regex.IsMatch(html, @"@\{[^}]*\}"))
            throw new InvalidDataException($"Unresolved Logic App expression in {fileName}");
        return html;
    }
    private static string Property(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private async Task<string> MailboxIdAsync(AccountDetails account, CancellationToken token)
    {
        var email = account.IngestionServiceAccount;
        if (string.IsNullOrWhiteSpace(email))
            throw new InvalidDataException($"Account {account.AccountId} has no ingestion mailbox");
        var filter = Uri.EscapeDataString($"mail eq '{email.Replace("'", "''")}'");
        var users = await graph.GetAllAsync($"users?$filter={filter}&$select=id", token);
        return users.Count == 1 ? users[0].GetProperty("id").GetString()! :
            throw new InvalidDataException($"Expected one Graph mailbox for {email}; found {users.Count}");
    }

    private async Task<JsonElement> MessageAsync(AccountDetails account, string messageId,
        CancellationToken token)
    {
        var mailboxId = await MailboxIdAsync(account, token);
        return await graph.GetAsync($"users/{Segment(mailboxId)}/messages/{Segment(messageId)}", token);
    }

    public async Task<string> MessageTextAsync(string accountId, string messageId, CancellationToken token)
    {
        var message = await MessageAsync(await accounts.GetAsync(accountId, token), messageId, token);
        var body = message.GetProperty("body");
        var text = Property(body, "content");
        if (string.Equals(Property(body, "contentType"), "html", StringComparison.OrdinalIgnoreCase))
            text = WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]+>", " "));
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(text) ? throw new InvalidDataException("Email body is empty") : text;
    }

    public async Task IdentifyAsync(IEventPublisher publisher, CancellationToken token)
    {
        var active = await accounts.GetActiveAsync(token);
        foreach (var account in active)
        {
            var accountId = account.AccountId;
            if (string.IsNullOrWhiteSpace(accountId)) continue;
            var mailboxId = await MailboxIdAsync(account, token);
            var mailboxPath = $"users/{Segment(mailboxId)}/messages";
            var messages = await graph.GetAllAsync($"{mailboxPath}?$filter={Uri.EscapeDataString("isRead eq false")}&$select=id,toRecipients", token);
            var prefix = account.IngestionServiceAccount!.Split('@')[0];
            var domain = account.IngestionServiceAccount[(account.IngestionServiceAccount.IndexOf('@') + 1)..];
            foreach (var message in messages)
            {
                var messageId = message.GetProperty("id").GetString()!;
                var recipients = message.GetProperty("toRecipients").EnumerateArray()
                    .Select(item => Property(item.GetProperty("emailAddress"), "address"));
                var matched = recipients.FirstOrDefault(address =>
                    address.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                if (matched is null) continue;
                var postfix = matched[prefix.Length..].ToLowerInvariant();
                var type = postfix.Equals($"+declaration@{domain}", StringComparison.OrdinalIgnoreCase) ? "declaration" :
                    postfix.Equals($"+qa@{domain}", StringComparison.OrdinalIgnoreCase) ? "qa" : "email-data-ingestion";
                if (type == "email-data-ingestion")
                    await publisher.PublishAsync(new { type, accountId, messageId }, token);
                else
                {
                    var input = await MessageTextAsync(accountId, messageId, token);
                    await publisher.PublishAsync(new { type, accountId, messageId, input }, token);
                }
                await graph.SendAsync(HttpMethod.Patch, $"{mailboxPath}/{Segment(messageId)}",
                    new { isRead = true }, token);
            }
        }
    }

    public async Task IngestAsync(JsonElement content, CancellationToken token)
    {
        var accountId = Events.Required(content, "accountId");
        var messageId = Events.Required(content, "messageId");
        var account = await accounts.GetAsync(accountId, token);
        var mailboxId = await MailboxIdAsync(account, token);
        var messagePath = $"users/{Segment(mailboxId)}/messages/{Segment(messageId)}";
        var message = await graph.GetAsync(messagePath, token);
        var attachments = await graph.GetAllAsync($"{messagePath}/attachments", token);
        var received = DateTimeOffset.TryParse(Property(message, "receivedDateTime"), out var date)
            ? date : DateTimeOffset.UtcNow;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(messageId)))[..10];
        var folderName = $"Email {received.UtcDateTime:yyyy-MM-ddTHH.mm.ssZ}-{hash}";
        var html = Property(message.GetProperty("body"), "content");
        var driveId = await files.StoreMessageAsync(accountId, folderName, html, token);
        foreach (var attachment in attachments)
        {
            var name = Property(attachment, "name");
            var contentType = Property(attachment, "contentType");
            var encoded = Property(attachment, "contentBytes");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(encoded))
                throw new InvalidDataException("Graph attachment has no name or contentBytes");
            await files.StoreAttachmentAsync(accountId, driveId, folderName, name,
                string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
                Convert.FromBase64String(encoded), token);
        }
        await graph.SendAsync(HttpMethod.Post, $"{messagePath}/reply",
            new { comment = "Your request has been processed and the content added to your repository." }, token);
    }

    public async Task RespondAsync(JsonElement content, CancellationToken token)
    {
        var type = Events.String(content, "type");
        if (type is not ("declaration" or "qa")) return;
        var status = Events.String(content, "status");
        if (status is not ("Generated" or "Failed")) return;
        var accountId = Events.Required(content, "accountId");
        var responseId = Events.String(content, "responseId");
        if (responseId is not null && (responseId.Contains('/') || responseId.Contains('\\')))
            throw new InvalidEventException("Invalid responseId");
        if (!string.IsNullOrWhiteSpace(responseId) && deliveries is not null &&
            await deliveries.WasDeliveredAsync(accountId, type, responseId, token)) return;
        var account = await accounts.GetAsync(accountId, token);
        var mailboxId = await MailboxIdAsync(account, token);
        var mailboxPath = $"users/{Segment(mailboxId)}";
        var messageId = Events.String(content, "messageId");
        var subject = Events.String(content, "emailSubject");
        if (string.IsNullOrWhiteSpace(subject))
            subject = Events.String(content, "sessionId") ??
                (type == "declaration" ? "Declaration" : "Question and answer");
        if (status == "Failed")
        {
            var failure = RenderTemplate("declaration-failed.html", new Dictionary<string, string>());
            if (!string.IsNullOrWhiteSpace(messageId))
                await ReplyHtmlAsync(mailboxPath, messageId, failure, token);
            else
                await SendMailAsync(graph, mailboxPath, ResponseAddresses(content, account), [],
                    subject, failure, token);
            if (!string.IsNullOrWhiteSpace(responseId) && deliveries is not null)
                await deliveries.MarkDeliveredAsync(accountId, type, responseId, token);
            return;
        }

        var links = new List<string>();
        var outputUrl = Events.String(content, "outputUrl");
        if (!string.IsNullOrWhiteSpace(outputUrl))
        {
            if (string.IsNullOrWhiteSpace(responseId))
                throw new InvalidEventException("Generated result is missing responseId");
            var result = await (results ?? throw new InvalidOperationException("Result store is missing"))
                .ReadAsync(type, accountId, responseId, outputUrl, token);
            var markdown = Events.Required(result, "document");
            var exhibits = IndexedExhibits(result);
            var pdfLink = await files.StoreGeneratedPdfAsync(accountId, type, responseId,
                markdown, token);
            links.Add($"<a href=\"{Escape(pdfLink)}\">{Escape(type == "declaration" ? "Generated declaration.pdf" : "Generated answer.pdf")}</a>");
            foreach (var (index, exhibitMarkdown) in exhibits)
            {
                var exhibitLink = await files.StoreExhibitPdfAsync(accountId, type, responseId,
                    index, exhibitMarkdown, token);
                links.Add($"<a href=\"{Escape(exhibitLink)}\">Exhibit {index}.pdf</a>");
            }
        }
        if (content.TryGetProperty("attachments", out var attachments) && attachments.ValueKind == JsonValueKind.Array)
            foreach (var attachment in attachments.EnumerateArray())
            {
                var fileId = Events.Required(attachment, "fileId");
                var name = Events.Required(attachment, "name");
                var url = await files.CreateEditLinkAsync(accountId, fileId, token);
                links.Add($"<a href=\"{Escape(url)}\">{Escape(name)}</a>");
            }
        if (links.Count == 0)
            throw new InvalidEventException("Generated response has no deliverable document");
        var html = RenderTemplate(type == "declaration" ? "declaration-generated.html" : "qa-generated.html", new Dictionary<string, string>
        {
            ["@{body('Parse_Request')?['input']}"] = Escape(Events.String(content, "input")),
            ["@{join(variables('Doc List'), '</li><li>')}"] = string.Join("</li><li>", links)
        });
        if (!string.IsNullOrWhiteSpace(messageId))
        {
            await ReplyHtmlAsync(mailboxPath, messageId, html, token);
        }
        else
            await SendMailAsync(graph, mailboxPath, ResponseAddresses(content, account), [],
                subject, html, token);
        if (!string.IsNullOrWhiteSpace(responseId) && deliveries is not null)
            await deliveries.MarkDeliveredAsync(accountId, type, responseId, token);
    }

    private IReadOnlyList<(int Index, string Markdown)> IndexedExhibits(JsonElement result)
    {
        if (!result.TryGetProperty("exhibits", out var exhibits) ||
            exhibits.ValueKind == JsonValueKind.Null) return [];
        if (exhibits.ValueKind != JsonValueKind.Array)
            throw new InvalidEventException("Generated exhibits must be an array");
        var indexed = new List<(int, string)>();
        var seen = new HashSet<int>();
        foreach (var exhibit in exhibits.EnumerateArray())
        {
            if (exhibit.ValueKind != JsonValueKind.Object ||
                !exhibit.TryGetProperty("firstIndex", out var number) ||
                number.ValueKind != JsonValueKind.Number || !number.TryGetInt32(out var index) ||
                !seen.Add(index) ||
                !exhibit.TryGetProperty("doc", out var doc) || doc.ValueKind != JsonValueKind.Object)
                throw new InvalidEventException("Generated exhibit has no unique firstIndex or doc");
            var title = Property(doc, "title");
            if (string.IsNullOrWhiteSpace(title)) title = "N/A";
            title = Regex.Replace(title, @"\s+", " ").Trim();
            var pages = doc.TryGetProperty("pages", out var pageList) && pageList.ValueKind == JsonValueKind.Array
                ? string.Join(", ", pageList.EnumerateArray().Select(page => page.ToString())) : "N/A";
            var body = Events.Required(doc, "pageContent");
            var markdown = $"# Exhibit {index}\n\n**Title:** {title}\n\n**Pages:** {pages}\n\n## Exhibit Content\n\n{body}";
            var source = Property(doc, "sourceUrl");
            if (Uri.TryCreate(source, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps &&
                url.Host.Equals(files.SharePointHost,
                    StringComparison.OrdinalIgnoreCase))
                markdown += $"\n\n**Source:** <{url.AbsoluteUri}>";
            indexed.Add((index, markdown));
        }
        return indexed;
    }

    private static List<string> ResponseAddresses(JsonElement content, AccountDetails account)
    {
        var addresses = content.TryGetProperty("emailAddresses", out var provided) &&
            provided.ValueKind == JsonValueKind.Array
            ? provided.EnumerateArray().Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item)).Cast<string>().ToList()
            : [];
        if (addresses.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(account.PersonInformation?.EmailAddress))
                addresses.Add(account.PersonInformation.EmailAddress);
            addresses.AddRange(account.AttorneyInformation?.Select(item => item.EmailAddress)
                .Where(item => !string.IsNullOrWhiteSpace(item)).Cast<string>() ?? []);
        }
        return addresses;
    }

    private Task ReplyHtmlAsync(string mailboxPath, string messageId, string html, CancellationToken token) =>
        graph.SendAsync(HttpMethod.Post, $"{mailboxPath}/messages/{Segment(messageId)}/reply",
            new { message = new { body = new { content = html, contentType = "HTML" } } }, token);

    private async Task SendMailAsync(IGraphClient target, string mailboxPath,
        IEnumerable<string> to, IEnumerable<string> cc,
        string subject, string html, CancellationToken token)
    {
        var recipients = to.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(item => new { emailAddress = new { address = item } }).ToArray();
        if (recipients.Length == 0) throw new InvalidDataException("Mail has no recipients");
        await target.SendAsync(HttpMethod.Post, $"{mailboxPath}/sendMail", new
        {
            message = new
            {
                subject, body = new { contentType = "HTML", content = html },
                toRecipients = recipients,
                ccRecipients = cc.Where(item => !string.IsNullOrWhiteSpace(item))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(item => new { emailAddress = new { address = item } }).ToArray()
            },
            saveToSentItems = true
        }, token);
    }

    public async Task CommunicateAsync(JsonElement content, CancellationToken token)
    {
        var status = Events.String(content, "status");
        if (status is not ("EmailIngestionComplete" or "OnboardingComplete" or "ShareRequest")) return;
        var accountId = Events.Required(content, "accountId");
        var account = await accounts.GetAsync(accountId, token);
        var person = account.PersonInformation?.EmailAddress;
        var attorney = account.AttorneyInformation?.LastOrDefault()?.EmailAddress;
        var preferredName = account.PersonInformation?.PreferredName ?? account.PersonInformation?.FirstName ?? "there";
        var sender = senderUser ?? Settings.Required("GRAPH_SENDER_USER");
        var mailboxPath = $"users/{Segment(sender)}";
        var brand = Settings.Get("EMAIL_BRAND_NAME") ?? "ProBono Law";
        if (status == "EmailIngestionComplete")
        {
            if (string.IsNullOrWhiteSpace(account.IngestionServiceAccount)) return;
            var service = account.IngestionServiceAccount;
            var at = service.IndexOf('@');
            var prefix = service[..at];
            var domain = service[at..];
            var html = RenderTemplate("email-ingestion-complete.html", new Dictionary<string, string>
            {
                ["@{outputs('Preferred_Name')}"] = Escape(preferredName),
                ["@{Email_Brand_Name}"] = Escape(brand),
                ["@{Email_Site_Url}"] = ConfiguredUrl("EMAIL_SITE_URL"),
                ["@{body('Parse_Entry')?['accountId']}"] = Escape(accountId),
                ["@{body('Parse_Entry')?['ingestionServiceAccount']}"] = Escape(service),
                ["@{outputs('Get_Ingestion_Service_Account_Prefix')}"] = Escape(prefix),
                ["@{outputs('Get_IngestionServiceAccount_postfix')}"] = Escape(domain)
            });
            await SendMailAsync(graph, mailboxPath, [person ?? ""], [attorney ?? ""],
                $"Great news for your Account {accountId}", html, token);
        }
        else if (status == "OnboardingComplete")
        {
            var billing = (await accounts.GetBillingCodeAsync(accountId, token))?.ToUpperInvariant();
            var notice = billing switch
            {
                "F" => "<br><br>Since this case is a Pro Bono case, it will be free of charge.",
                "D" => "<br><br>Since this case is a Demo case, the client will not be billed.",
                "" or null => "",
                _ => "<br><br>The client will be billed on a monthly basis after onboarding is completed."
            };
            var html = RenderTemplate("onboarding-complete.html", new Dictionary<string, string>
            {
                ["@{outputs('Preferred_Name')}"] = Escape(preferredName),
                ["@{body('Parse_Entry')?['accountId']}"] = Escape(accountId),
                ["@{outputs('Monthly_Billing_Notice')}"] = notice
            });
            await SendMailAsync(graph, mailboxPath, [person ?? ""], [attorney ?? ""], $"Welcome to {brand}", html, token);
        }
        else
        {
            var recipient = Events.Required(content, "emailAddress");
            var html = "<p class=\"editor-paragraph\">" + RenderTemplate("share-request.html",
                new Dictionary<string, string>
                {
                    ["@{outputs('Preferred_Name')}"] = Escape(preferredName),
                    ["@{Email_Brand_Name}"] = Escape(brand),
                    ["@{Email_Site_Url}"] = ConfiguredUrl("EMAIL_SITE_URL"),
                    ["@{Email_Onboarding_Url}"] = ConfiguredUrl("EMAIL_ONBOARDING_URL"),
                    ["@{Email_Logo_Url}"] = ConfiguredUrl("EMAIL_LOGO_URL"),
                    ["@{Email_Support_Address}"] = Escape(Settings.Required("EMAIL_SUPPORT_ADDRESS")),
                    ["@{Email_Support_Mailto}"] = Escape(Uri.EscapeDataString(Settings.Required("EMAIL_SUPPORT_ADDRESS")))
                }) + "</p>";
            await SendMailAsync(graph, mailboxPath, [recipient], [person ?? "", attorney ?? ""],
                $"Welcome to {brand}", html, token);
        }
    }
}
