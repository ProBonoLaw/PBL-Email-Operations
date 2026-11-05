import { NotificationClient, type Notification } from './index.js';

const client = new NotificationClient('https://example.org');
const ingestion: Notification = {
  oid: 'u', notificationType: 'INGESTION',
  data: { accountId: 'a', filepath: '/file.pdf', status: 'EVIDENCE_EXTRACTION' },
};
const send: () => Promise<void> = () => client.send(ingestion);
void send;
// @ts-expect-error Ingestion statuses cannot be used for egestion.
const invalid: Notification = { oid: 'u', notificationType: 'EGESTION', data: { accountId: 'a', filepath: '/file.pdf', status: 'PRELIMINARY' } };
// @ts-expect-error Ingestion requires a filepath.
const missing: Notification = { oid: 'u', notificationType: 'INGESTION', data: { accountId: 'a', status: 'PRELIMINARY' } };
void invalid;
void missing;
