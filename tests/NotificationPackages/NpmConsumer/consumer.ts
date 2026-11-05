import assert from 'node:assert/strict';
import { NotificationClient, NotificationTransmissionError, type Notification } from '@probonolaw/notification-client';

const client = new NotificationClient('https://notification.invalid', {
  fetch: async (url, options) => {
    assert.equal(String(url), 'https://notification.invalid/api/sendNotice');
    assert.equal(options?.method, 'POST');
    assert.deepEqual(options.headers, { 'Content-Type': 'application/json' });
    const payload = JSON.parse(String(options.body));
    assert.equal(payload.notificationType, 'INGESTION');
    assert.ok(['PRELIMINARY', 'EVIDENCE_EXTRACTION'].includes(payload.data.status));
    return new Response(null, { status: 202 });
  },
});
for (const status of ['PRELIMINARY', 'EVIDENCE_EXTRACTION'] as const) {
  const notice: Notification = { oid: 'consumer-user', notificationType: 'INGESTION',
    data: { accountId: 'consumer-account', filepath: '/file.pdf', status } };
  await client.send(notice);
}
// @ts-expect-error The installed declarations must limit the new statuses to ingestion.
const invalid: Notification = { oid: 'u', notificationType: 'EGESTION', data: { accountId: 'a', filepath: '/file.pdf', status: 'PRELIMINARY' } };
void invalid;
assert.equal(new NotificationTransmissionError(500, 'failure').responseBody, 'failure');
console.log('Installed NPM entry point and declarations transmitted both ingestion completion statuses.');
