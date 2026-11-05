import assert from 'node:assert/strict';
import test from 'node:test';
import { NotificationClient, NotificationTransmissionError } from './index.js';

for (const status of ['PRELIMINARY', 'EVIDENCE_EXTRACTION']) {
  test(`sends ${status} with JSON content type and cancellation signal`, async () => {
    const signal = new AbortController().signal;
    const notice = { oid: 'user-1', id: 'notice-1', notificationType: 'INGESTION',
      data: { accountId: 'a', filepath: '/file.pdf', status } };
    const client = new NotificationClient('https://example.org/', { fetch: async (url, options) => {
      assert.equal(url.href, 'https://example.org/api/sendNotice');
      assert.equal(options.method, 'POST');
      assert.equal(options.headers['Content-Type'], 'application/json');
      assert.equal(options.signal, signal);
      assert.deepEqual(JSON.parse(options.body), notice);
      return new Response(null, { status: 202 });
    } });
    await client.send(notice, { signal });
  });
}

for (const status of [400, 500, 200]) {
  test(`exposes HTTP ${status} without retrying`, async () => {
    let calls = 0;
    const client = new NotificationClient('https://example.org', { fetch: async () => {
      calls++;
      return new Response('backend response', { status });
    } });
    await assert.rejects(client.send({}), error => {
      assert.ok(error instanceof NotificationTransmissionError);
      assert.equal(error.status, status);
      assert.equal(error.responseBody, 'backend response');
      return true;
    });
    assert.equal(calls, 1);
  });
}

test('propagates network and cancellation failures without retrying', async () => {
  let calls = 0;
  const failure = new DOMException('Cancelled', 'AbortError');
  const client = new NotificationClient('https://example.org', { fetch: async () => {
    calls++;
    throw failure;
  } });
  await assert.rejects(client.send({}), error => error === failure);
  assert.equal(calls, 1);
});
