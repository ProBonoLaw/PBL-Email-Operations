import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, writeFileSync, rmSync, statSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import test from 'node:test';

const root = resolve(import.meta.dirname, '..');
const baseEnv = { PATH: process.env.PATH, HOME: process.env.HOME };
const guid = '1'.repeat(8) + '-' + ['1'.repeat(4), '1'.repeat(4), '1'.repeat(4), '1'.repeat(12)].join('-');
const settings = {
  AZURE_SUBSCRIPTION_ID: guid, AZURE_TENANT_ID: guid, AZURE_WORKLOAD_IDENTITY_CLIENT_ID: guid,
  AKS_RESOURCE_GROUP: 'example-group', AKS_NAME: 'example-cluster', ACR_NAME: 'exampleregistry',
  ACR_LOGIN_SERVER: 'registry.example.org', AKS_KEY_VAULT: 'example-vault',
  EVENTHUB_NAMESPACE: 'example-events', STORAGE_ACCOUNT: 'examplestorage',
  COMMUNICATION_EVENTHUB_NAME: 'example-communication', COMMUNICATION_CONSUMER_GROUP: 'example-consumer',
  EMAIL_BRAND_NAME: 'Example "Legal" & Team', EMAIL_SUPPORT_ADDRESS: 'support@example.org',
  GRAPH_SENDER_USER: 'sender@example.org', EMAIL_SITE_URL: 'https://portal.example.org/',
  EMAIL_ONBOARDING_URL: 'https://accounts.example.org/create?type=client&lang=en',
  EMAIL_LOGO_URL: 'https://cdn.example.org/logo.png',
};
function workspace(t) {
  const path = mkdtempSync(join(tmpdir(), 'email-settings-test-'));
  t.after(() => rmSync(path, { recursive: true, force: true }));
  return path;
}
function run(script, env, args = []) {
  return spawnSync('bash', [script, ...args], { cwd: root, env: { ...baseEnv, ...env }, encoding: 'utf8' });
}

test('exports values literally, masks them, and honors process overrides', t => {
  const dir = workspace(t), envFile = join(dir, 'environment');
  writeFileSync(envFile, '');
  const literal = 'Example $(touch SHOULD_NOT_EXIST) `whoami`';
  const result = run('scripts/deployment-settings.sh', {
    DEPLOYMENT_CONFIG: JSON.stringify({ EMAIL_BRAND_NAME: literal, ACR_NAME: 'file-registry' }),
    ACR_NAME: 'override-registry', GITHUB_ENV: envFile,
  }, ['--github-env']);
  assert.equal(result.status, 0, result.stderr);
  assert.equal(readFileSync(envFile, 'utf8'), `ACR_NAME=override-registry\nEMAIL_BRAND_NAME=${literal}\n`);
  assert.ok(result.stdout.split('\n').filter(Boolean).every(line => line.startsWith('::add-mask::')));
});

for (const config of [{ PATH: '/untrusted' }, { EMAIL_BRAND_NAME: 'first\nINJECTED=yes' },
  { EMAIL_BRAND_NAME: 'first\rINJECTED=yes' }, { EMAIL_BRAND_NAME: 1 }, []]) {
  test(`rejects unsafe configuration ${JSON.stringify(config)}`, t => {
    const dir = workspace(t), envFile = join(dir, 'environment');
    writeFileSync(envFile, '');
    const result = run('scripts/deployment-settings.sh', {
      DEPLOYMENT_CONFIG: JSON.stringify(config), GITHUB_ENV: envFile,
    }, ['--github-env']);
    assert.notEqual(result.status, 0);
    assert.equal(readFileSync(envFile, 'utf8'), '');
  });
}

for (const stage of ['test', 'prod']) {
  for (const activate of ['0', '1']) {
    test(`${stage} renders configured resources and activation=${activate} without Azure calls`, t => {
      const dir = workspace(t), output = join(dir, 'bundle.yaml');
      // A dry run must not use the configured Azure executable.
      writeFileSync(join(dir, 'az'), '#!/bin/sh\nexit 99\n', { mode: 0o700 });
      const config = join(dir, 'settings.json');
      writeFileSync(config, JSON.stringify(settings));
      const result = run(`scripts/deploy-aks-${stage}.sh`, {
        PATH: `${dir}:${baseEnv.PATH}`, DEPLOYMENT_CONFIG_FILE: config,
        IMAGE_TAG: `sha-${'a'.repeat(40)}`, DRY_RUN: '1', ACTIVATE: activate, RENDER_OUTPUT: output,
      });
      assert.equal(result.status, 0, result.stderr);
      const yaml = readFileSync(output, 'utf8');
      assert.ok(yaml.includes(`registry.example.org/email-operations:sha-${'a'.repeat(40)}`));
      assert.ok(yaml.includes('value: "example-communication"'));
      assert.ok(yaml.includes('value: "example-consumer"'));
      assert.equal((yaml.match(new RegExp(`^  replicas: ${activate}$`, 'gm')) || []).length, 6);
      assert.ok(yaml.includes(`suspend: ${activate === '1' ? 'false' : 'true'}`));
      const branding = JSON.parse(yaml.split('\n---\n').find(doc => doc.startsWith('{')));
      assert.equal(branding.data.EMAIL_BRAND_NAME, settings.EMAIL_BRAND_NAME);
      for (const key of ['GRAPH_SENDER_USER', 'EMAIL_SUPPORT_ADDRESS', 'EMAIL_SITE_URL', 'EMAIL_ONBOARDING_URL', 'EMAIL_LOGO_URL']) {
        assert.equal(branding.data[key], settings[key]);
      }
      assert.ok(yaml.includes('name: email-branding'));
      assert.equal(statSync(output).mode & 0o777, 0o600);
    });
  }
}

for (const stage of ['test', 'prod']) {
  test(`${stage} changes the communication pod template only when email settings change`, t => {
    const dir = workspace(t), output = join(dir, 'bundle.yaml');
    const render = config => {
      const result = run(`scripts/deploy-aks-${stage}.sh`, {
        DEPLOYMENT_CONFIG: JSON.stringify(config), IMAGE_TAG: `sha-${'a'.repeat(40)}`,
        DRY_RUN: '1', ACTIVATE: '1', RENDER_OUTPUT: output,
      });
      assert.equal(result.status, 0, result.stderr);
      const yaml = readFileSync(output, 'utf8');
      const hash = yaml.match(/checksum\/email-branding: "([a-f0-9]{64})"/);
      assert.ok(hash, 'The checksum must be in the communication pod template');
      const documents = yaml.split('\n---\n');
      const communication = documents.find(doc => /^  name: communication-hub$/m.test(doc));
      const brandingIndex = documents.findIndex(doc => doc.startsWith('{') && JSON.parse(doc).metadata.name === 'email-branding');
      assert.ok(brandingIndex >= 0 && brandingIndex < documents.indexOf(communication),
        'Apply the new ConfigMap before rolling out the worker');
      assert.match(communication, /  template:\n    metadata:[\s\S]*      annotations:\n        checksum\/email-branding:/);
      return { hash: hash[1], documents, communication };
    };
    const baseline = render(settings);
    const reordered = render(Object.fromEntries(Object.entries(settings).reverse()));
    assert.deepEqual(reordered, baseline, 'Equivalent settings must not cause an unnecessary rollout');
    for (const key of ['EMAIL_BRAND_NAME', 'EMAIL_SUPPORT_ADDRESS', 'GRAPH_SENDER_USER',
      'EMAIL_SITE_URL', 'EMAIL_ONBOARDING_URL', 'EMAIL_LOGO_URL']) {
      const changed = render({ ...settings, [key]: `${settings[key]}-changed` });
      assert.notEqual(changed.hash, baseline.hash, key);
      assert.equal(changed.communication.replace(changed.hash, baseline.hash), baseline.communication,
        'The same image and routing are retained while the pod checksum changes');
      const otherWorkloads = result => result.documents.filter(doc =>
        /^kind: (Deployment|CronJob)$/m.test(doc) && !/^  name: communication-hub$/m.test(doc));
      assert.deepEqual(otherWorkloads(changed), otherWorkloads(baseline));
    }
  });
}

test('refuses a wrong active subscription before invoking deployment', t => {
  const dir = workspace(t);
  writeFileSync(join(dir, 'az'), '#!/bin/sh\nif [ "$1 $2" = "account show" ]; then echo wrong-subscription; else exit 99; fi\n', { mode: 0o700 });
  const result = run('scripts/deploy-aks-prod.sh', {
    PATH: `${dir}:${baseEnv.PATH}`, DEPLOYMENT_CONFIG: JSON.stringify(settings), IMAGE_TAG: `sha-${'a'.repeat(40)}`,
  });
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /configured subscription/);
});

test('refuses missing settings and resource strings that could alter a manifest', () => {
  for (const config of [{}, { ...settings, ACR_NAME: 'name&injected' }]) {
    const result = run('scripts/deploy-aks-test.sh', {
      DEPLOYMENT_CONFIG: JSON.stringify(config), DRY_RUN: '1', IMAGE_TAG: `sha-${'a'.repeat(40)}`,
    });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /Missing deployment setting|Invalid resource name/);
  }
});
