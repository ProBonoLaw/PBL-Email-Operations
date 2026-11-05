# AKS test deployment

Set the `DEPLOYMENT_CONFIG` secret in the `aks-test` GitHub Environment and the repository variable `DEPLOYMENT_ENABLED=true`. See [configuration](deployment-configuration.md) for the required keys. Actual resource names and identity IDs remain private.

Prepare prerequisites from a workstation authenticated to the intended subscription:

```sh
export DEPLOYMENT_CONFIG_FILE=.local/deployment.aks-test.json
bash scripts/generate-test-appsettings.sh
bash scripts/bootstrap-aks-test.sh
```

Bootstrap prepares identities, OIDC federation, scoped roles, consumer groups, private Blob containers and Key Vault settings. It does not apply Kubernetes resources. Configure `GITHUB_OIDC_SUBJECT` to match the private repository's issued token subject. Changing ownership requires reviewing that trust. The Graph tenant is read from the Logic App and may differ from the subscription tenant. Existing application credentials remain in Key Vault; use `UPDATE_APPSETTINGS=1` or `UPDATE_NOTIFICATION_APPSETTINGS=1` to intentionally refresh them.

Render without Azure calls:

```sh
export DEPLOYMENT_CONFIG_FILE=.local/deployment.aks-test.json
IMAGE_TAG=sha-0000000000000000000000000000000000000000 \
  DRY_RUN=1 bash scripts/deploy-aks-test.sh
```

The private result is `.local/email-operations-aks-test.yaml`. Actual deployments require the SHA of an image present in the configured registry. Deployment rejects a different active subscription, invalid resource names, unresolved placeholders, and mutable image tags. `ACTIVATE=0` keeps Deployments at zero replicas and suspends identification; `ACTIVATE=1` starts workers, removes any previous identification Deployment, unsuspends the one-minute CronJob, and waits for rollout.

A successful main build tests and packages clients, pushes both images using the `test` Environment configuration, then deploys and activates through `aks-test`. The manual deployment workflow can redeploy a built image or apply inactive resources. Public repositories can run tests and build pull-request images without enabling Azure jobs.

Verify real ingestion, response delivery, tenant sharing policies and result payloads before cutover. Avoid overlapping Logic App consumers. Event processing is at least once: a crash between sending mail and saving its delivery marker can duplicate delivery. The notification API remains a ClusterIP service. Builder adapters still need a confirmed producer and response route for the selected environment.
