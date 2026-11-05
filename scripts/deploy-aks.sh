#!/usr/bin/env bash
set -euo pipefail

source "$(dirname "${BASH_SOURCE[0]}")/deployment-settings.sh"
require_deployment_settings AZURE_SUBSCRIPTION_ID AZURE_TENANT_ID AZURE_WORKLOAD_IDENTITY_CLIENT_ID \
  AKS_RESOURCE_GROUP AKS_NAME ACR_NAME ACR_LOGIN_SERVER AKS_KEY_VAULT EVENTHUB_NAMESPACE STORAGE_ACCOUNT \
  COMMUNICATION_EVENTHUB_NAME COMMUNICATION_CONSUMER_GROUP EMAIL_BRAND_NAME EMAIL_SUPPORT_ADDRESS \
  GRAPH_SENDER_USER EMAIL_SITE_URL EMAIL_ONBOARDING_URL EMAIL_LOGO_URL
umask 077
: "${IMAGE_TAG:?Set IMAGE_TAG}"

guid='^[[:xdigit:]]{8}(-[[:xdigit:]]{4}){3}-[[:xdigit:]]{12}$'
[[ "$AZURE_SUBSCRIPTION_ID" =~ $guid && "$AZURE_TENANT_ID" =~ $guid && "$AZURE_WORKLOAD_IDENTITY_CLIENT_ID" =~ $guid ]] || {
  echo 'Subscription, tenant and workload identity IDs must be GUIDs' >&2; exit 1;
}
[[ "$IMAGE_TAG" =~ ^sha-[[:xdigit:]]{40}$ ]] || {
  echo 'IMAGE_TAG must be sha- followed by a 40-character commit SHA' >&2; exit 1;
}
if [[ "${DRY_RUN:-0}" != 1 ]]; then
  current_subscription=$(az account show --query id -o tsv)
  [[ "$current_subscription" == "$AZURE_SUBSCRIPTION_ID" ]] || {
    echo 'Azure CLI must be signed into the configured subscription' >&2; exit 1;
  }
fi

for key in AKS_NAME AKS_RESOURCE_GROUP ACR_NAME ACR_LOGIN_SERVER AKS_KEY_VAULT EVENTHUB_NAMESPACE STORAGE_ACCOUNT COMMUNICATION_EVENTHUB_NAME COMMUNICATION_CONSUMER_GROUP; do
  [[ "${!key}" =~ ^[a-zA-Z0-9][a-zA-Z0-9._-]*$ ]] || { echo "Invalid resource name: $key" >&2; exit 1; }
done

activate=${ACTIVATE:-0}
[[ "$activate" == 0 || "$activate" == 1 ]] || { echo 'ACTIVATE must be 0 or 1' >&2; exit 1; }

rendered_dir=$(mktemp -d)
trap 'rm -rf "$rendered_dir"' EXIT
# Hash the same canonical configuration that is applied so an unchanged image
# rolls out when its environment changes, while identical settings stay stable.
branding="$rendered_dir/email-branding.json"
jq -n -S -c --arg brand "$EMAIL_BRAND_NAME" --arg support "$EMAIL_SUPPORT_ADDRESS" \
  --arg sender "$GRAPH_SENDER_USER" --arg site "$EMAIL_SITE_URL" \
  --arg onboarding "$EMAIL_ONBOARDING_URL" --arg logo "$EMAIL_LOGO_URL" \
  '{apiVersion:"v1",kind:"ConfigMap",metadata:{name:"email-branding",namespace:"email-operations"},
    data:{EMAIL_BRAND_NAME:$brand,EMAIL_SUPPORT_ADDRESS:$support,GRAPH_SENDER_USER:$sender,
      EMAIL_SITE_URL:$site,EMAIL_ONBOARDING_URL:$onboarding,EMAIL_LOGO_URL:$logo}}' > "$branding"
branding_checksum=$(sha256sum "$branding")
branding_checksum=${branding_checksum%% *}

bundle="$rendered_dir/email-operations.yaml"
cat k8s/email-operations/base.yaml > "$bundle"
# Apply settings before the Deployment rolls out pods that consume them.
printf '\n---\n' >> "$bundle"
cat "$branding" >> "$bundle"
printf '\n---\n' >> "$bundle"
cat k8s/email-operations/deployments.yaml >> "$bundle"
printf '\n---\n' >> "$bundle"
cat k8s/email-operations/identification-cronjob.yaml >> "$bundle"
printf '\n---\n' >> "$bundle"
cat k8s/email-operations/notification.yaml >> "$bundle"

sed -i \
  -e "s@<email-branding-checksum>@${branding_checksum}@g" \
  -e "s@<email-workload-identity-client-id>@${AZURE_WORKLOAD_IDENTITY_CLIENT_ID}@g" \
  -e "s@<key-vault-name>@${AKS_KEY_VAULT}@g" \
  -e "s@<eventhub-namespace>@${EVENTHUB_NAMESPACE}@g" \
  -e "s@<storage-account>@${STORAGE_ACCOUNT}@g" \
  -e "s@<communication-eventhub-name>@${COMMUNICATION_EVENTHUB_NAME}@g" \
  -e "s@<communication-consumer-group>@${COMMUNICATION_CONSUMER_GROUP}@g" \
  -e "s@<azure-tenant-id>@${AZURE_TENANT_ID}@g" \
  -e "s@<registry>/email-operations:latest@${ACR_LOGIN_SERVER}/email-operations:${IMAGE_TAG}@g" \
  -e "s@<registry>/notification-service:latest@${ACR_LOGIN_SERVER}/notification-service:${IMAGE_TAG}@g" \
  "$bundle"
if [[ "$activate" == 1 ]]; then
  sed -i -e 's/^  replicas: 0$/  replicas: 1/' \
    -e 's/^  suspend: true$/  suspend: false/' "$bundle"
fi
if grep -qE '<[^>]+>|:latest' "$bundle"; then
  echo 'Rendered manifest contains an unresolved placeholder or mutable image' >&2
  exit 1
fi

if [[ "${DRY_RUN:-0}" == 1 ]]; then
  output=${RENDER_OUTPUT:-.local/email-operations-${DEPLOYMENT_STAGE:-aks}.yaml}
  mkdir -p "$(dirname "$output")"
  install -m 600 "$bundle" "$output"
  printf 'Rendered %s (ACTIVATE=%s); no cluster changes\n' "$output" "$activate"
  exit 0
fi

for repository in email-operations notification-service; do
  if ! az acr repository show-tags --name "$ACR_NAME" --repository "$repository" \
    -o json | jq -e --arg tag "$IMAGE_TAG" 'index($tag) != null' >/dev/null; then
    echo "Image ${ACR_LOGIN_SERVER}/$repository:$IMAGE_TAG is not in ACR" >&2
    exit 1
  fi
done

remote_command='kubectl apply -f email-operations.yaml && kubectl get deployment,cronjob -n email-operations'
if [[ "$activate" == 1 ]]; then
  remote_command='if kubectl get namespace email-operations >/dev/null 2>&1; then kubectl delete deployment email-message-identification -n email-operations --ignore-not-found || exit 1; fi; '
  remote_command+='kubectl apply -f email-operations.yaml && kubectl get deployment,cronjob -n email-operations'
  remote_command+=' && for name in communication-hub email-data-ingestion email-declaration-response email-argument-builder email-timeline-builder notification-service; do kubectl rollout status deployment/$name -n email-operations --timeout=5m || exit 1; done'
fi
result=$(az aks command invoke --subscription "$AZURE_SUBSCRIPTION_ID" \
  --resource-group "$AKS_RESOURCE_GROUP" --name "$AKS_NAME" \
  --command "$remote_command" --file "$bundle" --query '{exitCode:exitCode,logs:logs}' -o json)
printf '%s\n' "$result" | jq -r '.logs // empty'
exit_code=$(printf '%s\n' "$result" | jq -r '.exitCode // 1')
if [[ "$exit_code" != 0 ]]; then
  echo "AKS apply failed with exit code $exit_code" >&2
  exit 1
fi
