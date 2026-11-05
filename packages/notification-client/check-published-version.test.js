import assert from 'node:assert/strict';
import test from 'node:test';
import { isVersionPublished } from './check-published-version.mjs';

test('checks the package metadata endpoint and finds an existing version', async () => {
  const exists = await isVersionPublished('@probonolaw/notification-client', '1.0.0', 'test-token', async (url, options) => {
    assert.equal(url, 'https://npm.pkg.github.com/@probonolaw%2fnotification-client');
    assert.equal(options.headers.Authorization, 'Bearer test-token');
    return Response.json({ versions: { '1.0.0': {} } });
  });
  assert.equal(exists, true);
});

test('allows publication when the version is absent from package metadata', async () => {
  assert.equal(await isVersionPublished('package', '1.0.0', 'token', async () =>
    Response.json({ versions: { '0.9.0': {} } })), false);
});

test('allows first publication when the package does not exist', async () => {
  assert.equal(await isVersionPublished('package', '1.0.0', 'token', async () =>
    new Response(null, { status: 404 })), false);
});

for (const status of [401, 403, 405, 500]) {
  test(`fails on HTTP ${status} instead of attempting publication`, async () => {
    await assert.rejects(isVersionPublished('package', '1.0.0', 'token', async () =>
      new Response(null, { status })), new RegExp(`HTTP ${status}`));
  });
}

test('fails on malformed registry metadata', async () => {
  await assert.rejects(isVersionPublished('package', '1.0.0', 'token', async () =>
    Response.json({})), /missing a versions object/);
});
