# Email Operations

This repo contains .NET 10 email workers and an internal .NET 10 notification API. The workers run the email side of the Test Logic App workflows in VS Code or Kubernetes. They read account records from Cosmos DB, perform Outlook, OneDrive, and SharePoint actions through Microsoft Graph, and send all AIMIE generation requests to AIMIE Operations through `aimie-request-queue`. They consume declaration results from `aimie-response-queue`.

| Worker role | Input | Work |
| --- | --- | --- |
| `email-message-identification` | one-minute Kubernetes CronJob | Finds unread account mail, reads the message, publishes declaration or QA requests to AIMIE or an ingestion event to `email-operations`, then marks the mail read |
| `email-data-ingestion` | `email-operations`, `eventKind=EmailProcessing` | Reads the message and attachments, converts the original and unsupported files to PDF in OneDrive, stores files in the account's SharePoint site, replies to the sender |
| `email-declaration-response` | `aimie-response-queue`, terminal declaration or QA result | Reads indexed Markdown and exhibit content from AIMIE Blob Storage, creates a PDF for each in the account's SharePoint site, and replies or sends mail with the links through Graph. Also links earlier SharePoint attachments |
| `communication-hub` | Test: `state-events`; production: `email-operations`, `eventKind=Communication` | Sends onboarding, ingestion availability, and sharing notices through Graph |
| `argument-builder`, `timeline-builder` | `email-operations` | Reads missing email text through Graph and forwards requests to AIMIE Operations |

The Test Logic App definitions and connection mapping are described in [workflow mapping](docs/workflow-mapping.md). The Test workflows use Office 365, OneDrive, and SharePoint managed connector actions. This worker implements their mail and file operations with Graph; it does not invoke those connectors or the Logic App's AIMIE Functions.

## VS Code local run

Install the .NET 10 SDK and Node.js 22, then run:

```sh
dotnet test EmailOperations.slnx --configuration Release
cd packages/notification-client
npm ci
npm test
npm run test:types
```

Tests use fake services and require no private Azure access. The development container can start without an environment file. For your local AI tooling, copy `config.example.toml` to `config.toml` and set your Azure endpoint and project trust entries. The local file is ignored by Git. Supply its API key through your local environment.

If your existing clone still tracks `config.toml`, back it up **before updating**: Git can remove the tracked copy when applying this change, even though the new ignore rule lists it. Copy it to a private location outside the checkout, update your branch, then restore it as the ignored local `config.toml`. See the [migration commands](docs/deployment-configuration.md#preserving-an-existing-local-tooling-configuration).

To run workers against your own Azure environment, see [deployment configuration](docs/deployment-configuration.md). Put deployment settings in an ignored, restricted `.local/deployment.aks-test.json` file, authenticate with `az login`, then run:

```sh
export DEPLOYMENT_CONFIG_FILE=.local/deployment.aks-test.json
bash scripts/generate-test-appsettings.sh
bash scripts/provision-local-debug.sh
```

The generator reads the configured Logic App and connected resources and writes private `.local/appsettings.<role>.json` files with mode `0600`. The Graph app secret stays in Key Vault and is read at runtime. `GRAPH_SENDER_USER`, `GRAPH_SHAREPOINT_HOST`, and the conversion account must belong to an accessible Graph tenant. The Graph tenant may differ from the Azure subscription tenant. `GRAPH_SENDER_USER_OVERRIDE` can override the sender during generation.

Choose a launch profile in [launch.json](.vscode/launch.json). Event workers use each event's `accountId`; identification polls configured active accounts once and exits. Local workers can publish events, mark mail as read, send mail, and upload documents. Use isolated Event Hubs consumer groups and Blob checkpoints when sharing a test environment. Communication emails use `GRAPH_SENDER_USER`, `EMAIL_SUPPORT_ADDRESS`, `EMAIL_SITE_URL`, `EMAIL_ONBOARDING_URL`, `EMAIL_LOGO_URL`, and `EMAIL_BRAND_NAME` (default: ProBono Law). Supply these through appsettings or process environment variables; see [configuration examples](docs/deployment-configuration.md). Site URLs have no built-in defaults.

For notification API debugging, run `scripts/sync-notification-settings-test.sh` and select `Notification Service API (Test)`. The script writes the ignored `.local/appsettings.notification-aks-test.json` and synchronizes its Key Vault secret. A valid API call persists data and broadcasts through the configured services.

## Settings and deployment

The notification API replaces the notification-service Azure Function. It preserves its notice types, JSON request shape, Cosmos `notices` write, and Web PubSub delivery to `USER_<oid>`. AKS callers POST to `http://notification-service.email-operations.svc.cluster.local/api/sendNotice`. The Kubernetes Service is `ClusterIP`; there is no ingress or external load balancer. `GET /healthz` is used by the pod probes. A valid notice returns 202 after both persistence and broadcast; invalid input returns 400. See the [notification API guide](docs/notification-service.md) for the payload and cutover.

The worker reads `/mnt/keyvault/appsettings.json` in Kubernetes or `APPSETTINGS_FILE` locally, then environment variables. Common keys are `COSMOS_CONNECTION_STRING`, `GRAPH_CLIENT_ID`, `GRAPH_TENANT_ID`, `GRAPH_KEYVAULT_URI`, `GRAPH_SECRET_NAME`, `GRAPH_SENDER_USER`, `GRAPH_CONVERSION_DRIVE_USER`, `GRAPH_SHAREPOINT_HOST`, `EVENTHUB_NAMESPACE`, and the role's hub and checkpoint settings. The response role also needs `OUTPUT_BLOB_CONTAINER_URI` for AIMIE's private results container and Cosmos write access to the `requests` container for delivery markers. `GRAPH_CLIENT_SECRET` can be supplied directly instead of reading the workflow app secret from Key Vault. The Test generator gets the Cosmos connection string from the `AzureCosmosDB_11_connectionString` app setting and the Graph credential reference from `Email-Message-Identification`.

Kubernetes manifests are in [k8s/email-operations](k8s/email-operations). The [AKS test deployment guide](docs/aks-test-deployment.md) covers the opt-in automatic Test rollout; the [AKS production guide](docs/aks-prod-deployment.md) covers the separate production subscription, resources, and manual release. The workload identity needs Event Hubs receiver/sender access, Blob checkpoint access, read access to AIMIE results, and Key Vault secret read access. The Cosmos connection string and Graph configuration are in the mounted private appsettings secret. This repository has no Azure Functions CI/CD workflow.

The identification CronJob uses `concurrencyPolicy: Forbid`, so a run still active at the next minute prevents overlap. Each Event Hubs worker processes one event at a time across all partitions, and its Deployment uses one replica with a `Recreate` rollout. Different worker roles run independently and can process separate requests at the same time. Failed identification runs exit nonzero; the next scheduled Job retries the poll. The Test manifests are inactive by default; activation removes an earlier identification Deployment if present.

Build the images locally with:

```sh
docker build -f Dockerfile.email -t email-operations:local .
docker build -f Dockerfile.notification -t notification-service:local .
```

Event Hubs processing is at least once. Upload paths for a message, generated PDF, and exhibit PDFs are deterministic and existing files are replaced on replay. Response delivery markers keyed by `responseId` suppress redelivery after a successful send; a crash between Graph accepting a send and saving its marker can still duplicate that email. Identification can likewise republish a request on replay. The builder email source and response delivery were not present in the four exported Test workflows, so those two adapters are ready for events but have no proven Test producer or reply route yet. The Test AIMIE results container was empty when reviewed, so actual exhibit metadata, source file links, and live response email delivery still need an end-to-end Test run. The new response uses PDFs; the previous Logic App created DOCX files.
