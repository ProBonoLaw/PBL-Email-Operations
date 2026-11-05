#!/usr/bin/env bash
# Run after callers have switched to the AKS notification-service and a live notice succeeds.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/deployment-settings.sh"

[[ "${RETIRE_NOTIFICATION_FUNCTION_TEST:-0}" == 1 ]] || {
  echo 'Set RETIRE_NOTIFICATION_FUNCTION_TEST=1 after the AKS cutover' >&2
  exit 1
}

subscription_id=${AZURE_SUBSCRIPTION_ID:?Set AZURE_SUBSCRIPTION_ID}
aks_group=${AKS_RESOURCE_GROUP:?Set AKS_RESOURCE_GROUP}
aks_name=${AKS_NAME:?Set AKS_NAME}
resource_group=${NOTIFICATION_RESOURCE_GROUP:?Set NOTIFICATION_RESOURCE_GROUP}
az account set --subscription "$subscription_id"

result=$(az aks command invoke --subscription "$subscription_id" \
  --resource-group "$aks_group" --name "$aks_name" \
  --command 'kubectl rollout status deployment/notification-service -n email-operations --timeout=60s && test "$(kubectl get svc notification-service -n email-operations -o jsonpath={.spec.type})" = ClusterIP' \
  --query '{exitCode:exitCode,logs:logs}' -o json)
if [[ "$(jq -r '.exitCode // 1' <<< "$result")" != 0 ]]; then
  jq -r '.logs // empty' <<< "$result" >&2
  echo 'AKS notification-service is not ready as a ClusterIP Service' >&2
  exit 1
fi

delete_resource() {
  local provider=$1 name=$2
  local id="/subscriptions/$subscription_id/resourceGroups/$resource_group/providers/$provider/$name"
  if az resource show --ids "$id" -o none 2>/dev/null; then
    az resource delete --ids "$id" -o none
    printf 'Deleted %s\n' "$id"
  fi
}

require_deployment_settings NOTIFICATION_FUNCTION_NAME NOTIFICATION_PLAN_NAME NOTIFICATION_STORAGE_ACCOUNT NOTIFICATION_PRIVATE_ENDPOINTS
read -r -a private_endpoints <<< "$NOTIFICATION_PRIVATE_ENDPOINTS"
delete_resource Microsoft.Web/sites "$NOTIFICATION_FUNCTION_NAME"
delete_resource Microsoft.Web/serverFarms "$NOTIFICATION_PLAN_NAME"
for endpoint in "${private_endpoints[@]}"; do
  delete_resource Microsoft.Network/privateEndpoints "$endpoint"
done
delete_resource Microsoft.Storage/storageAccounts "$NOTIFICATION_STORAGE_ACCOUNT"

echo 'The Web PubSub resource and its private endpoint were retained.'
