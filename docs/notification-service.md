# Internal notification API

The .NET 10 API in `src/NotificationService` replaces the Test Notification Service Function. It validates the same nine notice types, saves each notice to the existing Cosmos DB `db_conversation_history/notices` container, and broadcasts the saved JSON to the existing Web PubSub `notification` hub group `USER_<oid>`. The Cosmos container has the hierarchical partition key `/oid`, `/notificationType`.

The contracts in `src/NotificationService.Client/Notices.cs` use concrete notice classes and typed payloads and are shared with the NuGet client. `CHAT_MESSAGE` retains an open JSON payload because the upstream schema leaves that data unconstrained. The API serializes a validated notice once and uses the same JSON for Cosmos and Web PubSub.

## Calling it from AKS

Use `POST http://notification-service.email-operations.svc.cluster.local/api/sendNotice` with `Content-Type: application/json`. The Service is `ClusterIP` in the `email-operations` namespace and has no public ingress. The short address `http://notification-service/api/sendNotice` works from the same namespace. The Function URL and Function key are no longer needed after callers switch.

```json
{
  "oid": "client-object-id",
  "notificationType": "CASE_CREATED",
  "id": "optional-existing-notice-id",
  "data": {
    "accountId": "account-id",
    "status": "SUCCESS"
  }
}
```

`id` is optional; the API generates a UUID if omitted. It adds `read: false` and `createdAt`. A successful Cosmos write and Web PubSub send returns 202 with no body. Invalid JSON or invalid notice data returns 400. Backend failures return 500. A failure after the Cosmos write but before broadcast leaves a persisted notice; retrying with the same `id` may conflict with the existing item, as it did in the Function. Send once per client OID for accounts with several clients.

Supported types are `DECLARATION`, `ARGUMENT_BUILDER`, `TIMELINE_BUILDER`, `INGESTION`, `EGESTION`, `CASE_CREATED`, `CASE_DELETED`, `ALERT`, and `CHAT_MESSAGE`. The payload rules retain the upstream Zod schemas with two additional `INGESTION` statuses: `PRELIMINARY` (general data ingestion completed) and `EVIDENCE_EXTRACTION` (evidence extraction completed). Ingestion continues to accept `STARTED`, `SUCCESS`, and `FAILED`. Other workflows retain their existing status sets. `GET /healthz` reports process readiness for Kubernetes probes.

## OpenAPI and transmission packages

The complete [OpenAPI 3.1 document](../src/NotificationService/openapi.json) describes all notice types, payload rules and responses. The API serves this same embedded document at `GET /openapi/v1.json` in every environment. It is also included in both client packages.

- NuGet: `ProBonoLaw.EmailOperations.NotificationService.Client`, with shared typed notice models, `NotificationClient.SendAsync`, cancellation and HTTP error details. See the [.NET client README](../src/NotificationService.Client/README.md).
- NPM: `@probonolaw/notification-client`, with `NotificationClient.send`, TypeScript contracts, cancellation and HTTP error details. See the [JavaScript/TypeScript client README](../packages/notification-client/README.md).

Both clients send HTTP JSON to `/api/sendNotice`, require HTTP 202 for success, and perform no automatic retries. Build commands and installation from local archives or GitHub Packages are documented in the package READMEs. CI tests and packs both packages, installs them in clean consumers, compiles and runs transmission checks, and validates the source and bundled OpenAPI 3.1 documents before uploading the artifacts.

The independent `notification-client-packages` CI job uploads the validated NuGet and NPM archives as the `notification-client-packages` artifact. It runs even when the solution test job fails; image builds and deployment require both jobs to succeed. Client tests and package validation must pass before upload. Missing artifact files fail the upload step.

The `Publish Notification clients to GitHub Packages` workflow publishes both archives when package or publication-workflow changes reach `main`. It also supports **Run workflow** from Actions on `main` and GitHub releases tagged `notification-v<version>` (initial tag: `notification-v1.0.0`). Set the same version in the NuGet project and NPM manifest/lockfile before releasing. The workflow checks version agreement, runs the solution and client tests, and validates both packed consumers and specifications before publishing. It uses the workflow `GITHUB_TOKEN` with `packages: write`; no separate publishing secret is required. Already published versions are skipped to support reruns. Other releases and pull requests do not publish packages.

NuGet publishes to `https://nuget.pkg.github.com/probonolaw/index.json`; NPM publishes `@probonolaw/notification-client` to `https://npm.pkg.github.com`. Public packages use the ProBonoLaw identities. Private repositories can retain existing package names and feeds through the `PUBLICATION_CONFIG` repository secret; see [deployment configuration](deployment-configuration.md). Both packages include repository metadata linking them to the publishing repository. Consumers need package read access, and other repositories' workflows must be granted access in the package settings. GitHub Packages controls package visibility through those settings.

After building both archives and running `npm ci` in `packages/notification-client`, run `bash scripts/check-notification-packages.sh` to repeat these release checks locally. An optional argument selects the directory containing the two archives. The check uses temporary consumer projects and an isolated NuGet cache, requires the NPM entry point and TypeScript declarations to resolve from the installed archive, and requires both bundled specifications to match the source document.

## Settings and cutover

Run `scripts/sync-notification-settings-test.sh` while the Test Function is available. It copies the current six data settings into `.local/appsettings.notification-aks-test.json` with mode `0600` and publishes `notification-appsettings` to the configured `AKS_KEY_VAULT` when values change. The `.local` file is ignored by Git. Select `Notification Service API (Test)` in VS Code to load it through `APPSETTINGS_FILE`; the local API binds to `127.0.0.1:5080`. A real POST from this profile writes to Test Cosmos and sends through Test Web PubSub.

`scripts/bootstrap-aks-test.sh` creates the vault secret if it is missing and grants the AKS workload identity `Key Vault Secrets User` access to it. It uses the private local file when available or reads the six data settings from the Function. The pod mounts the secret through the Key Vault CSI driver. Function host settings and `AzureWebJobsStorage` are excluded. Normal bootstrap reruns use the existing vault secret and do not need the retired Function. Use `scripts/sync-notification-settings-test.sh` to refresh the local file and vault secret together while the Function exists. After retirement, pass `NOTIFICATION_APPSETTINGS_FILE` and set `UPDATE_NOTIFICATION_APPSETTINGS=1` on the bootstrap script to publish a new version. The API also accepts these values as environment variables, or from `APPSETTINGS_FILE` for local development.

The build workflow publishes `notification-service:sha-<commit>` alongside the email image. The AKS deploy script renders both images and starts the notification Deployment when `ACTIVATE=1`. Apply the bootstrap first, deploy, then send a real notice from an AKS caller and confirm its Cosmos item and Web PubSub delivery. Update callers from the Function URL to the ClusterIP URL. Once they are switched and a live notice succeeds, `RETIRE_NOTIFICATION_FUNCTION_TEST=1 bash scripts/retire-notification-function-test.sh` deletes the old Function site, Flex plan, dedicated `notificationsvc2hizt5uyi` storage account, and their private endpoints. The script checks that the AKS Deployment is ready and the Service is ClusterIP before deleting. It retains Web PubSub, its private endpoint, and shared Cosmos. The old Function and storage must remain until that cutover succeeds.
