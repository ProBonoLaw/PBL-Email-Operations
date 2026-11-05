#!/usr/bin/env bash
# Generate private .NET worker settings from the Test Standard Logic App.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/deployment-settings.sh"

subscription_id=${AZURE_SUBSCRIPTION_ID:?Set AZURE_SUBSCRIPTION_ID}
resource_group=${LOGIC_APP_RESOURCE_GROUP:?Set LOGIC_APP_RESOURCE_GROUP}
logic_app=${LOGIC_APP_NAME:?Set LOGIC_APP_NAME}
workspace_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
settings_dir="$workspace_root/.local"
workflow_api="https://management.azure.com/subscriptions/${subscription_id}/resourceGroups/${resource_group}/providers/Microsoft.Web/sites/${logic_app}/hostruntime/admin/vfs/site/wwwroot"

umask 077
mkdir -p "$settings_dir"
chmod 700 "$settings_dir"

"$workspace_root/scripts/export-logicapp-appsettings.sh" \
  "$subscription_id" "$resource_group" "$logic_app" \
  "$settings_dir/appsettings.logicapps-test.json"

workflow_json() {
  az rest --method get --url "${workflow_api}/$1/workflow.json?api-version=2022-03-01" \
    --output json --only-show-errors
}

export_runtime_file() {
  local source_name=$1 destination="$settings_dir/logicapp.$1.Test.json"
  local temporary_json
  temporary_json=$(mktemp "${destination}.XXXXXX")
  az rest --method get --url "${workflow_api}/${source_name}.json?api-version=2022-03-01" \
    --output json --only-show-errors > "$temporary_json"
  jq -e 'type == "object"' "$temporary_json" >/dev/null
  mv "$temporary_json" "$destination"
}

export_runtime_file parameters
export_runtime_file connections

# Managed connector URLs in parameters.json resolve through connections.json to
# app settings. Keep both source files beside the generated .NET role settings.
jq -e '.managedApiConnections.office365.connectionRuntimeUrl == "@parameters('\''office365-ConnectionRuntimeUrl'\'')" and
       .managedApiConnections.onedriveforbusiness.connectionRuntimeUrl == "@parameters('\''onedriveforbusiness-ConnectionRuntimeUrl'\'')" and
       .managedApiConnections.sharepointonline.connectionRuntimeUrl == "@parameters('\''sharepointonline-ConnectionRuntimeUrl'\'')"' \
  "$settings_dir/logicapp.connections.Test.json" >/dev/null
jq -e '.["office365-Authentication"].value.type == "ManagedServiceIdentity" and
       .["onedriveforbusiness-Authentication"].value.type == "ManagedServiceIdentity" and
       .["sharepointonline-Authentication"].value.type == "ManagedServiceIdentity"' \
  "$settings_dir/logicapp.parameters.Test.json" >/dev/null

connection_runtime_url() {
  local connection_name=$1
  local connection_id="/subscriptions/${subscription_id}/resourceGroups/${resource_group}/providers/Microsoft.Web/connections/${connection_name}"
  local connection_json runtime_url site_url
  connection_json=$(az resource show --ids "$connection_id" --api-version 2016-06-01 \
    --output json --only-show-errors)
  jq -e '[.properties.statuses[]?.status] | index("Connected") != null' \
    >/dev/null <<< "$connection_json"
  runtime_url=$(jq -er '.properties.connectionRuntimeUrl' <<< "$connection_json")
  site_url=$(jq -r --arg key "${connection_name}-ConnectionRuntimeUrl" '.[$key] // ""' \
    "$settings_dir/appsettings.logicapps-test.json")
  if [[ "$site_url" != "$runtime_url" ]]; then
    echo "Test app setting for ${connection_name} differs from its connected resource; using the resource URL locally" >&2
  fi
  printf '%s' "$runtime_url"
}

office365_url=$(connection_runtime_url office365)
onedrive_url=$(connection_runtime_url onedriveforbusiness)
sharepoint_url=$(connection_runtime_url sharepointonline)
resolved_file="$settings_dir/appsettings.logicapps-resolved-test.json"
resolved_temp=$(mktemp "${resolved_file}.XXXXXX")
jq --arg office365 "$office365_url" --arg onedrive "$onedrive_url" \
  --arg sharepoint "$sharepoint_url" \
  '.["office365-ConnectionRuntimeUrl"] = $office365 |
   .["onedriveforbusiness-ConnectionRuntimeUrl"] = $onedrive |
   .["sharepointonline-ConnectionRuntimeUrl"] = $sharepoint' \
  "$settings_dir/appsettings.logicapps-test.json" > "$resolved_temp"
mv "$resolved_temp" "$resolved_file"

state_router=$(workflow_json State-Events-Router)
email_router=$(workflow_json Email-Operations-Router)
response_workflow=$(workflow_json Email-Declaration-Response)
aimie_router=$(workflow_json AIMIE-Request-Router)
identification_workflow=$(workflow_json Email-Message-Identification)

jq -e '.definition.actions.Route_Event.cases.Communication.actions.Call_Communication.inputs.host.workflow.id == "Communication-Hub"' \
  >/dev/null <<< "$state_router"
jq -e '.definition.actions.Route_Event.cases.Email_Processing.actions.Call_Email_Processing.inputs.host.workflow.id == "Email-Data-Ingestion"' \
  >/dev/null <<< "$email_router"

state_hub=$(jq -er '.definition.triggers.When_events_are_available_in_Event_hub.inputs.parameters.eventHubName' <<< "$state_router")
email_hub=$(jq -er '.definition.triggers.When_events_are_available_in_Event_hub.inputs.parameters.eventHubName' <<< "$email_router")
response_hub=$(jq -er '.definition.triggers.When_events_are_available_in_Event_hub.inputs.parameters.eventHubName' <<< "$response_workflow")
aimie_hub=$(jq -er '.definition.triggers.When_events_are_available_in_Event_hub.inputs.parameters.eventHubName' <<< "$aimie_router")
namespace_name=$(jq -er '.eventHub_consolidated_fullyQualifiedNamespace' "$settings_dir/appsettings.logicapps-test.json")
storage_name=$(jq -er '.AzureWebJobsStorage | capture("AccountName=(?<name>[^;]+)").name' "$settings_dir/appsettings.logicapps-test.json")
checkpoint_uri="https://${storage_name}.blob.core.windows.net/email-operations-local-checkpoints"
failed_uri="https://${storage_name}.blob.core.windows.net/email-operations-local-failed-events"
result_uri="https://${storage_name}.blob.core.windows.net/aimie-operations-results"
graph_client_id=$(jq -er '.definition.actions.For_each_Case_Account.actions.Look_up_Service_Account_ID.inputs.authentication.clientId' <<< "$identification_workflow")
graph_tenant_id=$(jq -er '.definition.actions.For_each_Case_Account.actions.Look_up_Service_Account_ID.inputs.authentication.tenant' <<< "$identification_workflow")
graph_secret_name=$(jq -er '.definition.actions.Get_secret.inputs.parameters.secretName' <<< "$identification_workflow")
graph_vault_uri=$(jq -er '.keyVault_VaultUri' "$settings_dir/appsettings.logicapps-test.json")
cosmos_connection=$(jq -er '.AzureCosmosDB_11_connectionString' "$settings_dir/appsettings.logicapps-test.json")
office365_sender=$(az resource show --ids "/subscriptions/${subscription_id}/resourceGroups/${resource_group}/providers/Microsoft.Web/connections/office365" \
  --api-version 2016-06-01 --query properties.authenticatedUser.name --output tsv --only-show-errors)
conversion_user=$(az resource show --ids "/subscriptions/${subscription_id}/resourceGroups/${resource_group}/providers/Microsoft.Web/connections/sharepointonline" \
  --api-version 2016-06-01 --query properties.authenticatedUser.name --output tsv --only-show-errors)
[[ -n "$office365_sender" && -n "$conversion_user" ]] || { echo "Test connector users are missing" >&2; exit 1; }
sender_user=${GRAPH_SENDER_USER_OVERRIDE:-${GRAPH_SENDER_USER:?Set GRAPH_SENDER_USER}}
if [[ "$sender_user" != "$office365_sender" ]]; then
  echo "Graph sender differs from the Test Office 365 connection user; using the selected sender" >&2
fi

write_role() {
  local role_name=$1 hub_name=$2 group_name=$3
  local destination="$settings_dir/appsettings.${role_name}.json"
  local temporary_json
  temporary_json=$(mktemp "${destination}.XXXXXX")
  jq -n --arg namespace "$namespace_name" --arg hub "$hub_name" \
    --arg group "$group_name" --arg checkpoints "$checkpoint_uri" \
    --arg failed "$failed_uri" --arg results "$result_uri" --arg aimie "$aimie_hub" --arg role "$role_name" \
    --arg cosmos "$cosmos_connection" --arg client "$graph_client_id" \
    --arg tenant "$graph_tenant_id" --arg vault "$graph_vault_uri" \
    --arg secret "$graph_secret_name" --arg sender "$sender_user" \
    --arg driveUser "$conversion_user" --arg sharepoint "${GRAPH_SHAREPOINT_HOST:?Set GRAPH_SHAREPOINT_HOST}" \
    --arg brand "${EMAIL_BRAND_NAME:-ProBono Law}" --arg support "${EMAIL_SUPPORT_ADDRESS:?Set EMAIL_SUPPORT_ADDRESS}" \
    --arg site "${EMAIL_SITE_URL:?Set EMAIL_SITE_URL}" --arg onboarding "${EMAIL_ONBOARDING_URL:?Set EMAIL_ONBOARDING_URL}" \
    --arg logo "${EMAIL_LOGO_URL:?Set EMAIL_LOGO_URL}" \
    '{EVENTHUB_NAMESPACE:$namespace,EVENTHUB_NAME:$hub,EVENTHUB_CONSUMER_GROUP:$group,CHECKPOINT_BLOB_CONTAINER_URI:$checkpoints,FAILED_EVENTS_BLOB_CONTAINER_URI:$failed,OUTPUT_BLOB_CONTAINER_URI:$results} +
     {COSMOS_CONNECTION_STRING:$cosmos,GRAPH_CLIENT_ID:$client,GRAPH_TENANT_ID:$tenant,GRAPH_KEYVAULT_URI:$vault,GRAPH_SECRET_NAME:$secret,GRAPH_SENDER_USER:$sender,GRAPH_CONVERSION_DRIVE_USER:$driveUser,GRAPH_SHAREPOINT_HOST:$sharepoint,EMAIL_BRAND_NAME:$brand,EMAIL_SUPPORT_ADDRESS:$support,EMAIL_SITE_URL:$site,EMAIL_ONBOARDING_URL:$onboarding,EMAIL_LOGO_URL:$logo} +
     (if $role == "argument-builder" or $role == "timeline-builder" then
       {AIMIE_REQUEST_EVENTHUB_NAME:$aimie} else {} end)' > "$temporary_json"
  mv "$temporary_json" "$destination"
}

write_role communication-hub "$state_hub" email-operations-local-communication-hub
write_role email-data-ingestion "$email_hub" email-operations-local-data-ingestion
write_role email-declaration-response "$response_hub" email-operations-local-declaration-response

# The Test Logic App has no separate builder email trigger. Use the existing email
# input hub for these adapters; they ignore events with other types.
write_role argument-builder "$email_hub" email-operations-local-argument-builder
write_role timeline-builder "$email_hub" email-operations-local-timeline-builder

identification_file="$settings_dir/appsettings.email-message-identification.json"
identification_temp=$(mktemp "${identification_file}.XXXXXX")
jq -n --arg namespace "$namespace_name" --arg email "$email_hub" --arg aimie "$aimie_hub" \
  --arg cosmos "$cosmos_connection" --arg client "$graph_client_id" \
  --arg tenant "$graph_tenant_id" --arg vault "$graph_vault_uri" \
  --arg secret "$graph_secret_name" --arg sender "$sender_user" \
  --arg driveUser "$conversion_user" --arg sharepoint "${GRAPH_SHAREPOINT_HOST:?Set GRAPH_SHAREPOINT_HOST}" \
    --arg brand "${EMAIL_BRAND_NAME:-ProBono Law}" --arg support "${EMAIL_SUPPORT_ADDRESS:?Set EMAIL_SUPPORT_ADDRESS}" \
    --arg site "${EMAIL_SITE_URL:?Set EMAIL_SITE_URL}" --arg onboarding "${EMAIL_ONBOARDING_URL:?Set EMAIL_ONBOARDING_URL}" \
    --arg logo "${EMAIL_LOGO_URL:?Set EMAIL_LOGO_URL}" \
  '{EVENTHUB_NAMESPACE:$namespace,EMAIL_OPERATIONS_EVENTHUB_NAME:$email,AIMIE_REQUEST_EVENTHUB_NAME:$aimie,
    COSMOS_CONNECTION_STRING:$cosmos,GRAPH_CLIENT_ID:$client,GRAPH_TENANT_ID:$tenant,
    GRAPH_KEYVAULT_URI:$vault,GRAPH_SECRET_NAME:$secret,GRAPH_SENDER_USER:$sender,
    GRAPH_CONVERSION_DRIVE_USER:$driveUser,GRAPH_SHAREPOINT_HOST:$sharepoint,EMAIL_BRAND_NAME:$brand,EMAIL_SUPPORT_ADDRESS:$support,EMAIL_SITE_URL:$site,EMAIL_ONBOARDING_URL:$onboarding,EMAIL_LOGO_URL:$logo}' \
  > "$identification_temp"
mv "$identification_temp" "$identification_file"

echo "Generated six private role settings files in $settings_dir; no secret values were printed"
