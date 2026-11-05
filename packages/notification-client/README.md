# NotificationService JavaScript/TypeScript client

`@probonolaw/notification-client` requires Node.js 18+ and uses native fetch with no runtime dependencies. Includes TypeScript declarations and the service OpenAPI document.

```js
import { NotificationClient } from '@probonolaw/notification-client';

const client = new NotificationClient('http://notification-service.email-operations.svc.cluster.local');
await client.send({
  oid: 'client-object-id',
  notificationType: 'INGESTION',
  data: { accountId: 'account-id', filepath: '/file.pdf', status: 'PRELIMINARY' },
});
```

`PRELIMINARY` means general data ingestion completed. `EVIDENCE_EXTRACTION` means evidence extraction completed. TypeScript limits these statuses to ingestion; the service validates payloads at runtime.

Pass `{ signal }` to `send` for cancellation or timeout, or `{ fetch }` to the constructor for a custom transport. Only HTTP 202 is success. `NotificationTransmissionError` exposes `status` and `responseBody`; network errors and cancellation propagate. There are no automatic retries: a backend failure can leave a persisted notice, and resending its ID can conflict. The service generates an ID if omitted and overwrites read/createdAt.

Build from the repository root:

```sh
mkdir -p artifacts/packages
cd packages/notification-client
npm ci
npm test
npm run test:types
npm pack --pack-destination ../../artifacts/packages
```

Install the resulting tarball with `npm install ./artifacts/packages/probonolaw-notification-client-1.0.0.tgz`, adjusting the path for the consuming project.

For GitHub Packages installation, configure the consuming project's `.npmrc`:

```ini
@probonolaw:registry=https://npm.pkg.github.com
//npm.pkg.github.com/:_authToken=${NODE_AUTH_TOKEN}
```

Set `NODE_AUTH_TOKEN` to a GitHub token with `read:packages` access, then run `npm install @probonolaw/notification-client`. GitHub Actions consumers can use their `GITHUB_TOKEN` when granted access to the package.

The repository publishes both clients when package changes reach `main`. You can also run the publication workflow manually on `main`, or publish a GitHub release tagged `notification-v<version>`. Both package versions must match, and a release tag must match that version. Tests and packed consumer checks run before publication; already published versions are skipped. Increment both versions when releasing changed clients.
