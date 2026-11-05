# NotificationService .NET client

`ProBonoLaw.EmailOperations.NotificationService.Client` targets .NET 10 and later and includes the typed contracts used by the service and its OpenAPI document.

```csharp
using NotificationService;

using var http = new HttpClient
{
    BaseAddress = new Uri("http://notification-service.email-operations.svc.cluster.local/")
};
await new NotificationClient(http).SendAsync(new DocumentIngestionNotice
{
    Oid = "client-object-id",
    Data = new IngestionData
    {
        AccountId = "account-id", Filepath = "/file.pdf",
        Status = IngestionStatus.PRELIMINARY
    }
});
```

`PRELIMINARY` means general data ingestion completed. `EVIDENCE_EXTRACTION` means evidence extraction completed. These statuses apply only to ingestion.

Use a service root BaseAddress ending in `/`. The caller owns the HttpClient and can configure timeouts, handlers and headers. `SendAsync` accepts a cancellation token. Only HTTP 202 is success; `NotificationTransmissionException` exposes `StatusCode` and `ResponseBody`. Network errors and cancellation propagate. There are no automatic retries: a backend failure can leave a persisted notice, and resending its ID can conflict. The service generates an ID if omitted and overwrites read/createdAt.

Build from the repository root:

```sh
dotnet pack src/NotificationService.Client/NotificationService.Client.csproj -c Release -o artifacts/packages
dotnet add package ProBonoLaw.EmailOperations.NotificationService.Client --version 1.0.0 --source artifacts/packages
```

Run the install command from the consuming project, adjusting the source path.

Published versions are available from GitHub Packages at `https://nuget.pkg.github.com/probonolaw/index.json`. Configure that source in the consuming project using a GitHub username and a token with `read:packages` access, then install `ProBonoLaw.EmailOperations.NotificationService.Client`. GitHub Actions consumers can use their `GITHUB_TOKEN` when granted access to the package.

The repository publishes both clients when package changes reach `main`. You can also run the publication workflow manually on `main`, or publish a GitHub release tagged `notification-v<version>`. Both package versions must match, and a release tag must match that version. Tests and packed consumer checks run before publication; already published versions are skipped. Increment both versions when releasing changed clients.
