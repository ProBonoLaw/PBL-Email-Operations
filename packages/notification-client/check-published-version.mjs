import { readFileSync, appendFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

export async function isVersionPublished(name, version, token, fetchImpl = fetch) {
  // GitHub's NPM registry supports package metadata, not the /package/version endpoint.
  const response = await fetchImpl(`https://npm.pkg.github.com/${name.replace('/', '%2f')}`, {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (response.status === 404) return false;
  if (response.status !== 200) {
    throw new Error(`Checking published NPM version failed: HTTP ${response.status}`);
  }
  const metadata = await response.json();
  if (!metadata.versions || typeof metadata.versions !== 'object' || Array.isArray(metadata.versions)) {
    throw new Error('NPM registry response is missing a versions object');
  }
  return Object.hasOwn(metadata.versions, version);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const { name, version } = JSON.parse(readFileSync(new URL('./package.json', import.meta.url), 'utf8'));
  const exists = await isVersionPublished(name, version, process.env.NODE_AUTH_TOKEN);
  appendFileSync(process.env.GITHUB_OUTPUT, `exists=${exists}\n`);
  console.log(exists ? `${name}@${version} already published; skipping.` : `${name}@${version} will be published.`);
}
