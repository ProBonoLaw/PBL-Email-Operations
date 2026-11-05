#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
package_dir="${1:-$repo_root/artifacts/packages}"
package_dir="$(cd "$package_dir" && pwd)"
nuget_id=${NUGET_PACKAGE_ID:-ProBonoLaw.EmailOperations.NotificationService.Client}
npm_name=$(node -p 'JSON.parse(require("fs").readFileSync(process.argv[1], "utf8")).name' "$repo_root/packages/notification-client/package.json")
shopt -s nullglob
nuget_archives=("$package_dir"/*.nupkg)
npm_archives=("$package_dir"/*.tgz)
if [[ ${#nuget_archives[@]} != 1 || ${#npm_archives[@]} != 1 ]]; then
  echo "Expected exactly one NuGet and one NPM notification package in $package_dir" >&2
  exit 1
fi

consumer_root="$(mktemp -d)"
trap 'rm -rf "$consumer_root"' EXIT
cp -R "$repo_root/tests/NotificationPackages/NuGetConsumer" "$consumer_root/nuget"
cp -R "$repo_root/tests/NotificationPackages/NpmConsumer" "$consumer_root/npm"

# Update only the isolated consumer fixture when validating a private package identity.
node --input-type=module - "$consumer_root/npm/consumer.ts" "$npm_name" <<'JS'
import { readFileSync, writeFileSync } from 'node:fs';
const [path, name] = process.argv.slice(2);
writeFileSync(path, readFileSync(path, 'utf8').replaceAll('@probonolaw/notification-client', name));
JS

# An isolated package cache prevents an earlier local package version hiding a broken archive.
NUGET_PACKAGES="$consumer_root/nuget-cache" dotnet add "$consumer_root/nuget/Consumer.csproj" package \
  "$nuget_id" --source "$package_dir"
NUGET_PACKAGES="$consumer_root/nuget-cache" dotnet run --project "$consumer_root/nuget/Consumer.csproj" \
  --configuration Release --no-restore

# The consumer has no source references and resolves the public entry point and declarations.
npm install --prefix "$consumer_root/npm" --ignore-scripts --no-audit --no-fund "${npm_archives[0]}"
"$repo_root/packages/notification-client/node_modules/.bin/tsc" \
  --strict --module nodenext --target es2022 --outDir "$consumer_root/npm/dist" \
  --typeRoots "$repo_root/packages/notification-client/node_modules/@types" "$consumer_root/npm/consumer.ts"
node "$consumer_root/npm/dist/consumer.js"

mapfile -t nuget_specs < <(find "$consumer_root/nuget-cache" -name openapi.json -type f)
if [[ ${#nuget_specs[@]} != 1 ]]; then
  echo "NuGet archive must contain exactly one bundled openapi.json" >&2
  exit 1
fi
node "$repo_root/packages/notification-client/validate-openapi.mjs" \
  "$repo_root/src/NotificationService/openapi.json" \
  "${nuget_specs[0]}" \
  "$consumer_root/npm/node_modules/$npm_name/openapi.json"
