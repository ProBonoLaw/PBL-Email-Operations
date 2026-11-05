# Private deployment configuration

Enable Azure jobs only in deployment repositories with the repository variable `DEPLOYMENT_ENABLED=true`. Store a `DEPLOYMENT_CONFIG` secret in each GitHub Environment:

| Environment | Purpose |
| --- | --- |
| `test` | Build/push test images; `AZURE_CLIENT_ID` is the build identity. |
| `aks-test` | Deploy test workloads; `AZURE_CLIENT_ID` is the deployment identity. |
| `aks-prod` | Build/deploy a manually released production version. |

The secret is a JSON object with string values. Identity IDs and infrastructure names are secrets here because they are private operational identifiers even when they are not credentials. `scripts/deployment-settings.sh` masks each field before exporting it to subsequent Actions steps. Cosmos, Graph and Web PubSub credentials continue to reside in Key Vault and private application settings.

| Keys | Purpose |
| --- | --- |
| `AZURE_SUBSCRIPTION_ID`, `AZURE_TENANT_ID`, `AZURE_CLIENT_ID` | Azure login and subscription guard. |
| `AZURE_WORKLOAD_IDENTITY_CLIENT_ID` | Pod workload identity. |
| `ACR_NAME`, `ACR_LOGIN_SERVER` | Registry name and full hostname. |
| `AKS_RESOURCE_GROUP`, `AKS_NAME` | Cluster selection. |
| `AKS_KEY_VAULT`, `EVENTHUB_NAMESPACE`, `STORAGE_ACCOUNT` | Manifest rendering; namespace/account names without DNS suffixes. |
| `COMMUNICATION_EVENTHUB_NAME`, `COMMUNICATION_CONSUMER_GROUP` | Environment-specific communication routing. Bootstrap creates the selected consumer group on the existing hub and grants receiver access on that hub. |
| `EMAIL_BRAND_NAME`, `EMAIL_SUPPORT_ADDRESS`, `GRAPH_SENDER_USER` | Communication branding, support mailbox and sender mailbox, rendered into the cluster's `email-branding` ConfigMap as worker environment variables. |
| `EMAIL_SITE_URL`, `EMAIL_ONBOARDING_URL`, `EMAIL_LOGO_URL` | Full site, account creation and logo URLs used in communication emails. Also rendered as worker environment variables. |
| `IDENTITY_RESOURCE_GROUP`, `WORKLOAD_IDENTITY_NAME`, `CI_IDENTITY_NAME`, `GITHUB_OIDC_SUBJECT` | Bootstrap identities and federation. The subject excludes the appended `:environment:<name>` suffix. |
| `EVENTHUB_RESOURCE_GROUP`, `STORAGE_RESOURCE_GROUP`, `GRAPH_KEY_VAULT`, `GRAPH_SECRET_NAME` | Bootstrap resources and credential scopes. |
| `LOGIC_APP_RESOURCE_GROUP`, `LOGIC_APP_NAME`, `GRAPH_SENDER_USER`, `GRAPH_SHAREPOINT_HOST` | Generate worker settings. The SharePoint DNS hostname is required and restricts source links in generated exhibits. |
| `NOTIFICATION_RESOURCE_GROUP`, `NOTIFICATION_FUNCTION_NAME` | Import notification service settings. |
| `NOTIFICATION_PLAN_NAME`, `NOTIFICATION_STORAGE_ACCOUNT`, `NOTIFICATION_PRIVATE_ENDPOINTS` | Retire the previous notification Function after verified cutover. Endpoints are space-separated. |

Deployment jobs need the login, workload, registry, cluster, manifest, routing and branding keys. Other keys are used only by their maintenance scripts. The loader rejects unsupported keys and control characters and does not evaluate shell code. Explicit process environment values override JSON entries.

Communication URLs must be absolute HTTP or HTTPS URLs without embedded credentials. There are no built-in site URLs. Set all three URL variables for sharing emails; ingestion emails use `EMAIL_SITE_URL`. The rendered HTML escapes their values, including query strings. The sender and support email address are independently configurable; per-account recipient and ingestion addresses continue to come from account data.

For example, supply these runtime environment variables locally (or the same keys in private deployment configuration):

```sh
export GRAPH_SENDER_USER='sender@example.org'
export EMAIL_SUPPORT_ADDRESS='support@example.org'
export EMAIL_SITE_URL='https://portal.example.org'
export EMAIL_ONBOARDING_URL='https://portal.example.org/onboarding'
export EMAIL_LOGO_URL='https://cdn.example.org/logo.png'
```

For local use, create a mode `0600` file under `.local/` and set `DEPLOYMENT_CONFIG_FILE` to its path. Scripts load it directly. Runtime settings use `APPSETTINGS_FILE` or the Key Vault mount and then process environment overrides. Local `config.toml` is ignored by Git; `config.example.toml` provides a starting point. Existing clones that track the file must back it up before updating and restore it afterward, as described below.

The communication Deployment includes a checksum of its rendered email configuration in the pod template. Applying changed branding, sender/support addresses, or URLs therefore restarts that worker even when `IMAGE_TAG` stays the same. Reapplying identical settings leaves the checksum unchanged.

Configure GitHub with an authenticated CLI, supplying values through standard input:

```sh
gh secret set DEPLOYMENT_CONFIG --env test < .local/deployment.test.json
gh secret set DEPLOYMENT_CONFIG --env aks-test < .local/deployment.aks-test.json
gh secret set DEPLOYMENT_CONFIG --env aks-prod < .local/deployment.aks-prod.json
gh variable set DEPLOYMENT_ENABLED --body true
```

Public packages use `@probonolaw/notification-client` and `ProBonoLaw.EmailOperations.NotificationService.Client`. Set `PACKAGE_PUBLISHING_ENABLED=true` in publishing repositories. An optional repository secret `PUBLICATION_CONFIG` preserves private package identities:

```json
{
  "NPM_PACKAGE_NAME": "@your-organization/notification-client",
  "NUGET_PACKAGE_ID": "YourOrganization.EmailOperations.NotificationService.Client",
  "NUGET_SOURCE": "https://nuget.pkg.github.com/your-organization/index.json"
}
```

```sh
gh secret set PUBLICATION_CONFIG < .local/publishing.json
gh variable set PACKAGE_PUBLISHING_ENABLED --body true
```

Overrides apply only in the publishing checkout. Package metadata uses the publishing repository, and validation exercises the effective identities. Configure package access for that repository. Existing versions are immutable; increment versions when releasing changed clients under an existing identity.

Moving values out of files does not remove old commits, author emails, tags or release artifacts. Publish a reviewed export with fresh history or sanitize all retained history before making it public. Resolve license/copyright attribution separately. Keep synchronization credentials in the private repository and prevent private Git history from being merged into public code.

## Preserving an existing local tooling configuration

Git can remove an unchanged tracked `config.toml` when you update to the commit that stops tracking it. Adding the path to `.gitignore` does not protect that tracked copy. Before pulling or switching to the updated branch, back it up in a private directory outside the checkout:

```sh
config_backup_dir=$(mktemp -d "${TMPDIR:-/tmp}/email-operations-config.XXXXXX")
install -m 600 config.toml "$config_backup_dir/config.toml"
printf 'Local configuration backup: %s\n' "$config_backup_dir/config.toml"
```

Keep that path, update your branch using your normal Git workflow, and restore the file from the backup:

```sh
install -m 600 "$config_backup_dir/config.toml" config.toml
git check-ignore config.toml
```

If you use a new shell for restoration, set `config_backup_dir` to the directory printed during backup. Keep the backup until you have verified your local tooling settings.
