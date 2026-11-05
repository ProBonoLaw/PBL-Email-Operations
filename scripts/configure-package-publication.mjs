import { readFileSync, writeFileSync, appendFileSync } from 'node:fs';

// Applied only in the publishing checkout. Public manifests keep their public identities.
const config = JSON.parse(process.env.PUBLICATION_CONFIG || '{}');
const allowed = ['NPM_PACKAGE_NAME', 'NUGET_PACKAGE_ID', 'NUGET_SOURCE'];
if (!config || Array.isArray(config) || typeof config !== 'object' ||
    Object.entries(config).some(([key, value]) => !allowed.includes(key) ||
      typeof value !== 'string' || /[\x00-\x1f\x7f]/.test(value))) {
  throw new Error('Invalid publication configuration');
}
const packagePath = 'packages/notification-client/package.json';
const manifest = JSON.parse(readFileSync(packagePath, 'utf8'));
const project = readFileSync('src/NotificationService.Client/NotificationService.Client.csproj', 'utf8');
const publicPackageId = project.match(/<PackageId>([^<]+)<\/PackageId>/)[1];
const name = config.NPM_PACKAGE_NAME || manifest.name;
const packageId = config.NUGET_PACKAGE_ID || publicPackageId;
const owner = process.env.GITHUB_REPOSITORY_OWNER;
const repository = process.env.GITHUB_REPOSITORY;
if (!/^@[a-z0-9-]+\/[a-z0-9-]+$/.test(name) || !/^[A-Za-z0-9_.-]+$/.test(packageId) ||
    !/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository || '') ||
    repository.split('/')[0] !== owner) {
  throw new Error('Invalid package identity or GitHub repository');
}
const source = config.NUGET_SOURCE || `https://nuget.pkg.github.com/${owner}/index.json`;
if (!/^https:\/\/nuget\.pkg\.github\.com\/[A-Za-z0-9_.-]+\/index\.json$/.test(source)) {
  throw new Error('NUGET_SOURCE must be a GitHub Packages feed');
}
const repositoryUrl = `https://github.com/${repository}`;
for (const [key, value] of Object.entries({
  NPM_PACKAGE_NAME: name, NUGET_PACKAGE_ID: packageId,
  NUGET_SOURCE: source, PACKAGE_REPOSITORY_URL: repositoryUrl,
})) {
  console.log(`::add-mask::${value.replaceAll('%', '%25')}`);
  appendFileSync(process.env.GITHUB_ENV, `${key}=${value}\n`);
}
for (const path of ['packages/notification-client/README.md', 'src/NotificationService.Client/README.md']) {
  const text = readFileSync(path, 'utf8')
    .replaceAll(manifest.name, name)
    .replaceAll(manifest.name.replace('@', '').replace('/', '-'), name.replace('@', '').replace('/', '-'))
    .replaceAll(manifest.name.split('/')[0] + ':registry', name.split('/')[0] + ':registry')
    .replaceAll(publicPackageId, packageId)
    .replaceAll('https://nuget.pkg.github.com/probonolaw/index.json', source);
  writeFileSync(path, text);
}
manifest.name = name;
manifest.repository.url = `git+${repositoryUrl}.git`;
writeFileSync(packagePath, `${JSON.stringify(manifest, null, 2)}\n`);
const lockPath = 'packages/notification-client/package-lock.json';
const lock = JSON.parse(readFileSync(lockPath, 'utf8'));
lock.name = name;
lock.packages[''].name = name;
writeFileSync(lockPath, `${JSON.stringify(lock, null, 2)}\n`);
