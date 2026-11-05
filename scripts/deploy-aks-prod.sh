#!/usr/bin/env bash
set -euo pipefail
export DEPLOYMENT_STAGE=aks-prod
exec bash "$(dirname "${BASH_SOURCE[0]}")/deploy-aks.sh" "$@"
