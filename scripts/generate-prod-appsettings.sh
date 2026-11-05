#!/usr/bin/env bash
# Copy only the application data settings needed by the production AKS workloads.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/deployment-settings.sh"

subscription_id=${AZURE_SUBSCRIPTION_ID:?Set AZURE_SUBSCRIPTION_ID}
settings_dir=.local
mkdir -p "$settings_dir"
chmod 700 "$settings_dir"
umask 077
temporary_dir=$(mktemp -d "$settings_dir/prod-settings.XXXXXX")
trap 'rm -rf "$temporary_dir"' EXIT

az functionapp config appsettings list --subscription "$subscription_id" \
  -g "${LOGIC_APP_RESOURCE_GROUP:?Set LOGIC_APP_RESOURCE_GROUP}" -n "${LOGIC_APP_NAME:?Set LOGIC_APP_NAME}" -o json > "$temporary_dir/logicapp.json"
az rest --method get --url \
  "https://management.azure.com/subscriptions/$subscription_id/resourceGroups/${LOGIC_APP_RESOURCE_GROUP:?Set LOGIC_APP_RESOURCE_GROUP}/providers/Microsoft.Web/sites/${LOGIC_APP_NAME:?Set LOGIC_APP_NAME}/hostruntime/admin/vfs/site/wwwroot/Email-Message-Identification/workflow.json?api-version=2022-03-01" \
  -o json > "$temporary_dir/identification.json"
conversion_user=$(az resource show --ids \
  "/subscriptions/$subscription_id/resourceGroups/${LOGIC_APP_RESOURCE_GROUP:?Set LOGIC_APP_RESOURCE_GROUP}/providers/Microsoft.Web/connections/sharepointonline" \
  --api-version 2016-06-01 --query properties.authenticatedUser.name -o tsv)
[[ -n "$conversion_user" ]] || { echo 'Production SharePoint connection user is missing' >&2; exit 1; }

email_file="$settings_dir/appsettings.aks-prod.json"
jq -n --slurpfile settings "$temporary_dir/logicapp.json" \
  --slurpfile workflow "$temporary_dir/identification.json" \
  --arg sender "${GRAPH_SENDER_USER_OVERRIDE:-${GRAPH_SENDER_USER:?Set GRAPH_SENDER_USER}}" \
  --arg drive "$conversion_user" --arg sharepoint "${GRAPH_SHAREPOINT_HOST:?Set GRAPH_SHAREPOINT_HOST}" \
  --arg brand "${EMAIL_BRAND_NAME:-ProBono Law}" --arg support "${EMAIL_SUPPORT_ADDRESS:?Set EMAIL_SUPPORT_ADDRESS}" \
    --arg site "${EMAIL_SITE_URL:?Set EMAIL_SITE_URL}" --arg onboarding "${EMAIL_ONBOARDING_URL:?Set EMAIL_ONBOARDING_URL}" \
    --arg logo "${EMAIL_LOGO_URL:?Set EMAIL_LOGO_URL}" '
  ($settings[0] | map({key: .name, value: .value}) | from_entries) as $app |
  $workflow[0].definition.actions as $actions |
  {
    COSMOS_CONNECTION_STRING: $app.AzureCosmosDB_11_connectionString,
    GRAPH_CLIENT_ID: $actions.For_each_Case_Account.actions.Look_up_Service_Account_ID.inputs.authentication.clientId,
    GRAPH_TENANT_ID: $actions.For_each_Case_Account.actions.Look_up_Service_Account_ID.inputs.authentication.tenant,
    GRAPH_KEYVAULT_URI: $app.keyVault_VaultUri,
    GRAPH_SECRET_NAME: $actions.Get_secret.inputs.parameters.secretName,
    GRAPH_SENDER_USER: $sender,
    GRAPH_CONVERSION_DRIVE_USER: $drive,
    GRAPH_SHAREPOINT_HOST: $sharepoint,
    EMAIL_BRAND_NAME: $brand, EMAIL_SUPPORT_ADDRESS: $support,
    EMAIL_SITE_URL: $site, EMAIL_ONBOARDING_URL: $onboarding, EMAIL_LOGO_URL: $logo
  } |
  if all(.[]; type == "string" and length > 0) then . else error("Missing production email setting") end
' > "$temporary_dir/email.json"
mv "$temporary_dir/email.json" "$email_file"

az functionapp config appsettings list --subscription "$subscription_id" \
  -g "${NOTIFICATION_RESOURCE_GROUP:?Set NOTIFICATION_RESOURCE_GROUP}" -n "${NOTIFICATION_FUNCTION_NAME:?Set NOTIFICATION_FUNCTION_NAME}" -o json > "$temporary_dir/notification-function.json"
notification_file="$settings_dir/appsettings.notification-aks-prod.json"
jq 'map({key: .name, value: .value}) | from_entries |
  {COSMOS_ENDPOINT,COSMOS_KEY,NOTIFICATION_DATABASE,NOTIFICATION_CONTAINER,
   WEB_PUBSUB_CONNECTION_STRING,WEB_PUBSUB_HUB} |
  if all(.[]; type == "string" and length > 0) then . else error("Missing production notification setting") end
' "$temporary_dir/notification-function.json" > "$temporary_dir/notification.json"
mv "$temporary_dir/notification.json" "$notification_file"
chmod 600 "$email_file" "$notification_file"
echo 'Generated two private production AKS settings files; no secret values were printed'
