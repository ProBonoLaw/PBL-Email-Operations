#!/usr/bin/env bash
# Copy the Test Function's data settings to local debugging and the AKS Key Vault.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/deployment-settings.sh"

subscription_id=${AZURE_SUBSCRIPTION_ID:?Set AZURE_SUBSCRIPTION_ID}
settings_file=${NOTIFICATION_APPSETTINGS_FILE:-.local/appsettings.notification-aks-test.json}
vault_name=${AKS_KEY_VAULT:?Set AKS_KEY_VAULT}
secret_name=notification-appsettings

umask 077
source_file=$(mktemp)
vault_file=$(mktemp)
trap 'rm -f "$source_file" "$vault_file"' EXIT

az account set --subscription "$subscription_id"
az functionapp config appsettings list -g "${NOTIFICATION_RESOURCE_GROUP:?Set NOTIFICATION_RESOURCE_GROUP}" \
  -n "${NOTIFICATION_FUNCTION_NAME:?Set NOTIFICATION_FUNCTION_NAME}" -o json |
  jq -e 'map({key: .name, value: .value}) | from_entries |
    {COSMOS_ENDPOINT,COSMOS_KEY,NOTIFICATION_DATABASE,NOTIFICATION_CONTAINER,
     WEB_PUBSUB_CONNECTION_STRING,WEB_PUBSUB_HUB} |
    if all([.COSMOS_ENDPOINT,.COSMOS_KEY,.NOTIFICATION_DATABASE,.NOTIFICATION_CONTAINER,
            .WEB_PUBSUB_CONNECTION_STRING,.WEB_PUBSUB_HUB][];
           type == "string" and length > 0)
    then . else error("Notification Function is missing a required setting") end' > "$source_file"

mkdir -p "$(dirname "$settings_file")"
if ! cmp -s "$source_file" "$settings_file"; then
  install -m 600 "$source_file" "$settings_file"
  echo "Refreshed $settings_file"
else
  chmod 600 "$settings_file"
  echo "Local notification settings are current: $settings_file"
fi

if az keyvault secret show --vault-name "$vault_name" -n "$secret_name" \
  --query value -o tsv > "$vault_file" 2>/dev/null &&
  jq -e . "$vault_file" >/dev/null &&
  cmp -s <(jq -S -c . "$source_file") <(jq -S -c . "$vault_file"); then
  echo "Key Vault secret is current: $vault_name/$secret_name"
else
  az keyvault secret set --vault-name "$vault_name" -n "$secret_name" \
    --file "$settings_file" --encoding utf-8 --content-type application/json -o none
  echo "Published Key Vault secret: $vault_name/$secret_name"
fi
