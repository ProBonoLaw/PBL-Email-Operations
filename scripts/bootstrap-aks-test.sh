#!/usr/bin/env bash
# Idempotent Azure prerequisites for the AKS Test workload. Does not deploy pods.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/deployment-settings.sh"
require_deployment_settings COMMUNICATION_EVENTHUB_NAME COMMUNICATION_CONSUMER_GROUP

subscription_id=${AZURE_SUBSCRIPTION_ID:?Set AZURE_SUBSCRIPTION_ID}
aks_group=${AKS_RESOURCE_GROUP:?Set AKS_RESOURCE_GROUP}
aks_name=${AKS_NAME:?Set AKS_NAME}
identity_group=${IDENTITY_RESOURCE_GROUP:?Set IDENTITY_RESOURCE_GROUP}
eventhub_group=${EVENTHUB_RESOURCE_GROUP:?Set EVENTHUB_RESOURCE_GROUP}
eventhub_namespace=${EVENTHUB_NAMESPACE:?Set EVENTHUB_NAMESPACE}
storage_group=${STORAGE_RESOURCE_GROUP:?Set STORAGE_RESOURCE_GROUP}
storage_account=${STORAGE_ACCOUNT:?Set STORAGE_ACCOUNT}
aks_vault=${AKS_KEY_VAULT:?Set AKS_KEY_VAULT}
graph_vault=${GRAPH_KEY_VAULT:?Set GRAPH_KEY_VAULT}
workload_identity=${WORKLOAD_IDENTITY_NAME:?Set WORKLOAD_IDENTITY_NAME}
ci_identity=${CI_IDENTITY_NAME:?Set CI_IDENTITY_NAME}
repo_subject=${GITHUB_OIDC_SUBJECT:?Set GITHUB_OIDC_SUBJECT}
settings_file=${EMAIL_OPERATIONS_APPSETTINGS_FILE:-.local/appsettings.aks-test.json}
notification_settings_file=${NOTIFICATION_APPSETTINGS_FILE:-.local/appsettings.notification-aks-test.json}

az account set --subscription "$subscription_id"

if [[ ! -f "$settings_file" ]]; then
  source_file=.local/appsettings.email-data-ingestion.json
  [[ -f "$source_file" ]] || { echo "Missing $source_file; run scripts/generate-test-appsettings.sh first" >&2; exit 1; }
  mkdir -p .local
  umask 077
  jq '{COSMOS_CONNECTION_STRING,GRAPH_CLIENT_ID,GRAPH_TENANT_ID,GRAPH_KEYVAULT_URI,
      GRAPH_SECRET_NAME,GRAPH_SENDER_USER,GRAPH_CONVERSION_DRIVE_USER,GRAPH_SHAREPOINT_HOST}' \
    "$source_file" > "$settings_file"
fi
jq -e 'all([.COSMOS_CONNECTION_STRING,.GRAPH_CLIENT_ID,.GRAPH_TENANT_ID,.GRAPH_KEYVAULT_URI,
    .GRAPH_SECRET_NAME,.GRAPH_SENDER_USER,.GRAPH_CONVERSION_DRIVE_USER,.GRAPH_SHAREPOINT_HOST][];
    type == "string" and length > 0)' "$settings_file" >/dev/null || {
  echo 'AKS appsettings is missing a required value' >&2; exit 1;
}
chmod 600 "$settings_file"

notification_secret_exists=0
if az keyvault secret show --vault-name "$aks_vault" -n notification-appsettings -o none 2>/dev/null; then
  notification_secret_exists=1
fi
if [[ "$notification_secret_exists" == 0 || "${UPDATE_NOTIFICATION_APPSETTINGS:-0}" == 1 ]]; then
  if [[ ! -f "$notification_settings_file" ]]; then
    if ! az functionapp show -g "${NOTIFICATION_RESOURCE_GROUP:?Set NOTIFICATION_RESOURCE_GROUP}" -n "${NOTIFICATION_FUNCTION_NAME:?Set NOTIFICATION_FUNCTION_NAME}" -o none 2>/dev/null; then
      echo "Missing $notification_settings_file; provide NOTIFICATION_APPSETTINGS_FILE after retiring the Function" >&2
      exit 1
    fi
    mkdir -p "$(dirname "$notification_settings_file")"
    umask 077
    az functionapp config appsettings list -g "${NOTIFICATION_RESOURCE_GROUP:?Set NOTIFICATION_RESOURCE_GROUP}" \
      -n "${NOTIFICATION_FUNCTION_NAME:?Set NOTIFICATION_FUNCTION_NAME}" -o json |
      jq 'map({key: .name, value: .value}) | from_entries |
        {COSMOS_ENDPOINT,COSMOS_KEY,NOTIFICATION_DATABASE,NOTIFICATION_CONTAINER,
         WEB_PUBSUB_CONNECTION_STRING,WEB_PUBSUB_HUB}' > "$notification_settings_file"
  fi
  jq -e 'all([.COSMOS_ENDPOINT,.COSMOS_KEY,.NOTIFICATION_DATABASE,.NOTIFICATION_CONTAINER,
      .WEB_PUBSUB_CONNECTION_STRING,.WEB_PUBSUB_HUB][];
      type == "string" and length > 0)' "$notification_settings_file" >/dev/null || {
    echo 'Notification appsettings is missing a required value' >&2; exit 1;
  }
  chmod 600 "$notification_settings_file"
fi

ensure_identity() {
  local name=$1
  if ! az identity show -g "$identity_group" -n "$name" -o none 2>/dev/null; then
    az identity create -g "$identity_group" -n "$name" -o none
  fi
}

ensure_federation() {
  local identity=$1 name=$2 issuer=$3 subject=$4
  if ! az identity federated-credential show -g "$identity_group" --identity-name "$identity" \
    -n "$name" -o none 2>/dev/null; then
    az identity federated-credential create -g "$identity_group" --identity-name "$identity" \
      -n "$name" --issuer "$issuer" --subject "$subject" \
      --audiences api://AzureADTokenExchange -o none
  fi
}

ensure_role() {
  local principal=$1 role=$2 scope=$3 count
  count=$(az role assignment list --assignee-object-id "$principal" \
    --scope "$scope" --role "$role" --query 'length(@)' -o tsv)
  if [[ "$count" == 0 ]]; then
    az role assignment create --assignee-object-id "$principal" \
      --assignee-principal-type ServicePrincipal --role "$role" --scope "$scope" -o none
  fi
}

ensure_group() {
  local hub=$1 group=$2
  if ! az eventhubs eventhub consumer-group show -g "$eventhub_group" \
    --namespace-name "$eventhub_namespace" --eventhub-name "$hub" -n "$group" \
    -o none 2>/dev/null; then
    az eventhubs eventhub consumer-group create -g "$eventhub_group" \
      --namespace-name "$eventhub_namespace" --eventhub-name "$hub" -n "$group" -o none
  fi
}

ensure_identity "$workload_identity"
ensure_identity "$ci_identity"
workload_client=$(az identity show -g "$identity_group" -n "$workload_identity" --query clientId -o tsv)
workload_principal=$(az identity show -g "$identity_group" -n "$workload_identity" --query principalId -o tsv)
ci_client=$(az identity show -g "$identity_group" -n "$ci_identity" --query clientId -o tsv)
ci_principal=$(az identity show -g "$identity_group" -n "$ci_identity" --query principalId -o tsv)
aks_issuer=$(az aks show -g "$aks_group" -n "$aks_name" --query oidcIssuerProfile.issuerUrl -o tsv)

ensure_federation "$workload_identity" aks-test-northcentral "$aks_issuer" \
  system:serviceaccount:email-operations:email-operations
ensure_federation "$ci_identity" github-email-operations-test \
  https://token.actions.githubusercontent.com "$repo_subject:environment:test"
ensure_federation "$ci_identity" github-email-operations-aks-test \
  https://token.actions.githubusercontent.com "$repo_subject:environment:aks-test"

ensure_group "$COMMUNICATION_EVENTHUB_NAME" "$COMMUNICATION_CONSUMER_GROUP"
ensure_group email-operations email-operations-data-ingestion
ensure_group email-operations email-operations-argument-builder
ensure_group email-operations email-operations-timeline-builder
ensure_group aimie-response-queue email-operations-declaration-response

for container in email-operations-checkpoints email-operations-failed-events; do
  az storage container create --account-name "$storage_account" --auth-mode login \
    -n "$container" -o none
done
if [[ "${UPDATE_APPSETTINGS:-0}" == 1 ]] || ! az keyvault secret show \
  --vault-name "$aks_vault" -n email-operations-appsettings -o none 2>/dev/null; then
  az keyvault secret set --vault-name "$aks_vault" -n email-operations-appsettings \
    --file "$settings_file" --encoding utf-8 --content-type application/json -o none
fi
if [[ "${UPDATE_NOTIFICATION_APPSETTINGS:-0}" == 1 || "$notification_secret_exists" == 0 ]]; then
  az keyvault secret set --vault-name "$aks_vault" -n notification-appsettings \
    --file "$notification_settings_file" --encoding utf-8 --content-type application/json -o none
fi

hub_scope() {
  az eventhubs eventhub show -g "$eventhub_group" --namespace-name "$eventhub_namespace" \
    -n "$1" --query id -o tsv
}
mapfile -t receiver_hubs < <(printf '%s\n' "$COMMUNICATION_EVENTHUB_NAME" email-operations aimie-response-queue | sort -u)
for hub in "${receiver_hubs[@]}"; do
  ensure_role "$workload_principal" 'Azure Event Hubs Data Receiver' "$(hub_scope "$hub")"
done
for hub in email-operations aimie-request-queue; do
  ensure_role "$workload_principal" 'Azure Event Hubs Data Sender' "$(hub_scope "$hub")"
done

storage_id=$(az storage account show -g "$storage_group" -n "$storage_account" --query id -o tsv)
for container in email-operations-checkpoints email-operations-failed-events; do
  ensure_role "$workload_principal" 'Storage Blob Data Contributor' \
    "$storage_id/blobServices/default/containers/$container"
done
ensure_role "$workload_principal" 'Storage Blob Data Reader' \
  "$storage_id/blobServices/default/containers/aimie-operations-results"
aks_vault_id=$(az keyvault show -n "$aks_vault" --query id -o tsv)
graph_vault_id=$(az keyvault show -n "$graph_vault" --query id -o tsv)
ensure_role "$workload_principal" 'Key Vault Secrets User' \
  "$aks_vault_id/secrets/email-operations-appsettings"
ensure_role "$workload_principal" 'Key Vault Secrets User' \
  "$aks_vault_id/secrets/notification-appsettings"
ensure_role "$workload_principal" 'Key Vault Secrets User' \
  "$graph_vault_id/secrets/${GRAPH_SECRET_NAME:?Set GRAPH_SECRET_NAME}"

registry_id=$(az acr show -n "${ACR_NAME:?Set ACR_NAME}" --query id -o tsv)
cluster_id=$(az aks show -g "$aks_group" -n "$aks_name" --query id -o tsv)
ensure_role "$ci_principal" AcrPush "$registry_id"
ensure_role "$ci_principal" Reader "$registry_id"
ensure_role "$ci_principal" 'Azure Kubernetes Service Contributor Role' "$cluster_id"
ensure_role "$ci_principal" 'Azure Kubernetes Service RBAC Cluster Admin' "$cluster_id"

printf 'Workload identity client ID: %s\n' "$workload_client"
printf 'GitHub Actions client ID: %s\n' "$ci_client"
printf 'Tenant ID: %s\n' "$(az account show --query tenantId -o tsv)"
printf 'Subscription ID: %s\n' "$subscription_id"
printf 'Azure prerequisites are ready; no Kubernetes workload was started.\n'
