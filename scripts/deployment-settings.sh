#!/usr/bin/env bash
# Source this file before using deployment configuration, or pass --github-env in Actions.
set -euo pipefail

deployment_keys='AZURE_SUBSCRIPTION_ID AZURE_TENANT_ID AZURE_CLIENT_ID AZURE_WORKLOAD_IDENTITY_CLIENT_ID AKS_RESOURCE_GROUP AKS_NAME IDENTITY_RESOURCE_GROUP EVENTHUB_RESOURCE_GROUP EVENTHUB_NAMESPACE STORAGE_RESOURCE_GROUP STORAGE_ACCOUNT AKS_KEY_VAULT GRAPH_KEY_VAULT WORKLOAD_IDENTITY_NAME CI_IDENTITY_NAME GITHUB_OIDC_SUBJECT ACR_NAME ACR_LOGIN_SERVER LOGIC_APP_RESOURCE_GROUP LOGIC_APP_NAME NOTIFICATION_RESOURCE_GROUP NOTIFICATION_FUNCTION_NAME NOTIFICATION_PLAN_NAME NOTIFICATION_STORAGE_ACCOUNT NOTIFICATION_PRIVATE_ENDPOINTS GRAPH_SENDER_USER GRAPH_SHAREPOINT_HOST GRAPH_SECRET_NAME EMAIL_BRAND_NAME EMAIL_SUPPORT_ADDRESS EMAIL_SITE_URL EMAIL_ONBOARDING_URL EMAIL_LOGO_URL COMMUNICATION_EVENTHUB_NAME COMMUNICATION_CONSUMER_GROUP'

require_deployment_settings() {
  local key
  for key in "$@"; do
    [[ -n "${!key:-}" ]] || { echo "Missing deployment setting: $key" >&2; return 1; }
  done
}

load_deployment_settings() {
  local configuration=${DEPLOYMENT_CONFIG:-} key value
  if [[ -n "${DEPLOYMENT_CONFIG_FILE:-}" ]]; then
    configuration=$(cat "$DEPLOYMENT_CONFIG_FILE")
  fi
  if [[ -n "$configuration" ]]; then
    # Restrict exported keys and reject multiline values before writing Actions environment files.
    jq -e --arg allowed "$deployment_keys" '
      type == "object" and
      all(to_entries[]; .key as $key |
        ($allowed | split(" ") | index($key)) != null and
        (.value | type == "string" and (test("[\\x00-\\x1f\\x7f]") | not)))
    ' <<< "$configuration" >/dev/null || {
      echo 'Invalid deployment configuration: use supported keys and single-line string values' >&2
      return 1
    }
    while IFS= read -r key && IFS= read -r value; do
      # Explicit process environment values take precedence over the JSON file.
      if [[ ! -v "$key" ]]; then export "$key=$value"; fi
    done < <(jq -r 'to_entries[] | .key, .value' <<< "$configuration")
  fi
  if [[ "${1:-}" == --github-env ]]; then
    : "${GITHUB_ENV:?This option requires GitHub Actions}"
    for key in $deployment_keys; do
      value=${!key:-}
      [[ -n "$value" ]] || continue
      [[ "$value" != *$'\n'* && "$value" != *$'\r'* ]] || return 1
      printf '::add-mask::%s\n' "${value//%/%25}"
      printf '%s=%s\n' "$key" "$value" >> "$GITHUB_ENV"
    done
  fi
}

load_deployment_settings "$@"
