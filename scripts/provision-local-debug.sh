#!/usr/bin/env bash
# Prepare isolated Test Event Hubs groups and Blob containers for VS Code.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/deployment-settings.sh"

subscription_id=${AZURE_SUBSCRIPTION_ID:?Set AZURE_SUBSCRIPTION_ID}
eventhubs_group=${EVENTHUB_RESOURCE_GROUP:?Set EVENTHUB_RESOURCE_GROUP}
namespace_name=${EVENTHUB_NAMESPACE:?Set EVENTHUB_NAMESPACE}
storage_name=${STORAGE_ACCOUNT:?Set STORAGE_ACCOUNT}

for hub_and_group in \
  state-events:email-operations-local-communication-hub \
  email-operations:email-operations-local-data-ingestion \
  aimie-response-queue:email-operations-local-declaration-response \
  email-operations:email-operations-local-argument-builder \
  email-operations:email-operations-local-timeline-builder; do
  hub_name=${hub_and_group%%:*}
  group_name=${hub_and_group#*:}
  az eventhubs eventhub consumer-group create --subscription "$subscription_id" \
    --resource-group "$eventhubs_group" --namespace-name "$namespace_name" \
    --eventhub-name "$hub_name" --name "$group_name" --output none
done

for container_name in email-operations-local-checkpoints email-operations-local-failed-events; do
  az storage container create --subscription "$subscription_id" --account-name "$storage_name" \
    --auth-mode login --name "$container_name" --output none
done

echo "Local Test consumer groups and Blob containers are ready"
