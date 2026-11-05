import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';

const root = resolve(import.meta.dirname, '..');
const settings = {
  AZURE_SUBSCRIPTION_ID: 'example-subscription', AKS_RESOURCE_GROUP: 'example-clusters',
  AKS_NAME: 'example-cluster', IDENTITY_RESOURCE_GROUP: 'example-identities',
  EVENTHUB_RESOURCE_GROUP: 'example-events-group', EVENTHUB_NAMESPACE: 'example-events',
  STORAGE_RESOURCE_GROUP: 'example-storage-group', STORAGE_ACCOUNT: 'examplestorage',
  AKS_KEY_VAULT: 'example-settings-vault', GRAPH_KEY_VAULT: 'example-graph-vault',
  WORKLOAD_IDENTITY_NAME: 'example-workload', CI_IDENTITY_NAME: 'example-ci',
  GITHUB_OIDC_SUBJECT: 'repo:example/project', GRAPH_SECRET_NAME: 'example-graph-secret',
  ACR_NAME: 'exampleregistry',
  COMMUNICATION_EVENTHUB_NAME: 'custom-communication', COMMUNICATION_CONSUMER_GROUP: 'custom-readers',
};

// Record Azure operations and simulate new consumer groups/role assignments.
// All actual az calls are replaced, so these tests cannot change cloud resources.
const fakeAzure = `#!/usr/bin/env node
const { appendFileSync } = require('node:fs');
const args = process.argv.slice(2);
appendFileSync(process.env.AZURE_CALL_LOG, JSON.stringify(args) + '\\n');
const starts = (...prefix) => prefix.every((part, index) => args[index] === part);
const value = option => args[args.indexOf(option) + 1];
if (starts('eventhubs', 'eventhub', 'consumer-group', 'show')) process.exit(1);
if (starts('role', 'assignment', 'list')) { console.log('0'); process.exit(0); }
if (starts('identity', 'show') && args.includes('--query')) {
  console.log(value('-n') + '-' + value('--query')); process.exit(0);
}
if (starts('eventhubs', 'eventhub', 'show')) {
  console.log('/example/eventhubs/' + value('-n')); process.exit(0);
}
if (starts('aks', 'show')) {
  console.log(value('--query') === 'id' ? '/example/cluster' : 'https://issuer.example.org'); process.exit(0);
}
if (starts('storage', 'account', 'show')) { console.log('/example/storage'); process.exit(0); }
if (starts('keyvault', 'show')) { console.log('/example/vaults/' + value('-n')); process.exit(0); }
if (starts('acr', 'show')) { console.log('/example/registry'); process.exit(0); }
if (starts('account', 'show')) { console.log('example-tenant'); process.exit(0); }
if (starts('account', 'set') || starts('identity', 'show') ||
    starts('identity', 'federated-credential', 'show') || starts('keyvault', 'secret', 'show') ||
    starts('storage', 'container', 'create') || starts('rest', '--method', 'put') ||
    starts('eventhubs', 'eventhub', 'consumer-group', 'create') || starts('role', 'assignment', 'create')) {
  process.exit(0);
}
console.error('Unexpected Azure operation: ' + JSON.stringify(args));
process.exit(99);
`;

function bootstrap(t, stage, config) {
  const dir = mkdtempSync(join(tmpdir(), 'email-bootstrap-test-'));
  t.after(() => rmSync(dir, { recursive: true, force: true }));
  const settingsFile = join(dir, 'appsettings.json'), log = join(dir, 'azure-calls.jsonl');
  writeFileSync(join(dir, 'az'), fakeAzure, { mode: 0o700 });
  writeFileSync(log, '');
  writeFileSync(settingsFile, JSON.stringify({
    COSMOS_CONNECTION_STRING: 'example', GRAPH_CLIENT_ID: 'example', GRAPH_TENANT_ID: 'example',
    GRAPH_KEYVAULT_URI: 'https://example-graph-vault.vault.azure.net/', GRAPH_SECRET_NAME: 'example',
    GRAPH_SENDER_USER: 'sender@example.org', GRAPH_CONVERSION_DRIVE_USER: 'convert@example.org',
    GRAPH_SHAREPOINT_HOST: 'sharepoint.example.org',
  }));
  const result = spawnSync('bash', [`scripts/bootstrap-aks-${stage}.sh`], {
    cwd: root, encoding: 'utf8', env: {
      PATH: `${dir}:${process.env.PATH}`, HOME: process.env.HOME,
      DEPLOYMENT_CONFIG: JSON.stringify(config), EMAIL_OPERATIONS_APPSETTINGS_FILE: settingsFile,
      AZURE_CALL_LOG: log,
    },
  });
  const calls = readFileSync(log, 'utf8').split('\n').filter(Boolean).map(line => JSON.parse(line));
  return { ...result, calls };
}

for (const stage of ['test', 'prod']) {
  test(`${stage} provisions the selected communication group and authorizes its hub`, t => {
    const result = bootstrap(t, stage, settings);
    assert.equal(result.status, 0, result.stderr);
    const starts = (args, prefix) => prefix.every((part, index) => args[index] === part);
    const value = (args, option) => args[args.indexOf(option) + 1];
    const groups = result.calls.filter(args => starts(args, ['eventhubs', 'eventhub', 'consumer-group', 'create']));
    assert.ok(groups.some(args => value(args, '--eventhub-name') === 'custom-communication' &&
      value(args, '-n') === 'custom-readers' && value(args, '--namespace-name') === 'example-events'));
    assert.ok(groups.some(args => value(args, '--eventhub-name') === 'email-operations' &&
      value(args, '-n') === 'email-operations-data-ingestion'));
    assert.ok(groups.some(args => value(args, '--eventhub-name') === 'aimie-response-queue' &&
      value(args, '-n') === 'email-operations-declaration-response'));
    const receivers = result.calls.filter(args => starts(args, ['role', 'assignment', 'create']) &&
      value(args, '--role') === 'Azure Event Hubs Data Receiver');
    assert.deepEqual(receivers.map(args => value(args, '--scope')).sort(), [
      '/example/eventhubs/aimie-response-queue', '/example/eventhubs/custom-communication',
      '/example/eventhubs/email-operations',
    ]);
    assert.ok(receivers.every(args => value(args, '--assignee-object-id') === 'example-workload-principalId'));
  });

  test(`${stage} refuses missing communication settings before any Azure operation`, t => {
    for (const key of ['COMMUNICATION_EVENTHUB_NAME', 'COMMUNICATION_CONSUMER_GROUP']) {
      const config = { ...settings };
      delete config[key];
      const result = bootstrap(t, stage, config);
      assert.notEqual(result.status, 0);
      assert.ok(result.stderr.includes(key));
      assert.deepEqual(result.calls, []);
    }
  });

  test(`${stage} authorizes a shared communication hub only once`, t => {
    const result = bootstrap(t, stage, { ...settings, COMMUNICATION_EVENTHUB_NAME: 'email-operations' });
    assert.equal(result.status, 0, result.stderr);
    const receivers = result.calls.filter(args => args.slice(0, 3).join(' ') === 'role assignment create' &&
      args[args.indexOf('--role') + 1] === 'Azure Event Hubs Data Receiver');
    assert.deepEqual(receivers.map(args => args[args.indexOf('--scope') + 1]).sort(), [
      '/example/eventhubs/aimie-response-queue', '/example/eventhubs/email-operations',
    ]);
  });
}
