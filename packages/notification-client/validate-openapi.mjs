import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import SwaggerParser from '@apidevtools/swagger-parser';

const paths = process.argv.slice(2);
assert.ok(paths.length > 0, 'Provide at least one OpenAPI document');
let canonical;
for (const path of paths) {
  const document = JSON.parse(await readFile(path, 'utf8'));
  assert.equal(document.openapi, '3.1.0');
  // Validate the OpenAPI structure and resolve every reference without network access.
  await SwaggerParser.validate(path, { resolve: { external: false } });
  canonical ??= document;
  assert.deepEqual(document, canonical, `Bundled specification differs from source: ${path}`);
  console.log(`Validated OpenAPI 3.1: ${path}`);
}
