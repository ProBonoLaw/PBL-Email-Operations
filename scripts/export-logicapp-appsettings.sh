#!/usr/bin/env bash
set -euo pipefail

if (( $# != 4 )); then
    echo "Usage: $0 <subscription-id> <resource-group> <logic-app-name> <output-json>" >&2
    exit 2
fi

subscription_id=$1
resource_group=$2
logic_app_name=$3
output_json=$4

command -v az >/dev/null || { echo "Azure CLI is required" >&2; exit 1; }
command -v jq >/dev/null || { echo "jq is required" >&2; exit 1; }

umask 077
mkdir -p "$(dirname "$output_json")"
temporary_json=$(mktemp "${output_json}.XXXXXX")
trap 'rm -f "$temporary_json"' EXIT

az webapp config appsettings list \
    --subscription "$subscription_id" \
    --resource-group "$resource_group" \
    --name "$logic_app_name" \
    --output json |
    jq -e 'reduce .[] as $setting ({}; .[$setting.name] = $setting.value) | select(length > 0)' > "$temporary_json"

mv "$temporary_json" "$output_json"
echo "Exported Logic App settings to $output_json (private file)"
