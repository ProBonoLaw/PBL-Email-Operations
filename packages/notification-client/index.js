export class NotificationTransmissionError extends Error {
  constructor(status, responseBody) {
    super(`NotificationService returned HTTP ${status}.`);
    this.name = 'NotificationTransmissionError';
    this.status = status;
    this.responseBody = responseBody;
  }
}

export class NotificationClient {
  constructor(baseUrl, { fetch: fetchImpl = globalThis.fetch } = {}) {
    this.url = new URL('api/sendNotice', `${baseUrl.replace(/\/+$/, '')}/`);
    this.fetch = fetchImpl;
  }

  async send(notice, { signal } = {}) {
    const response = await this.fetch(this.url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(notice),
      signal,
    });
    if (response.status !== 202) {
      throw new NotificationTransmissionError(response.status, await response.text());
    }
  }
}
