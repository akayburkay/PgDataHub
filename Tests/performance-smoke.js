import http from 'k6/http';
import { check, sleep } from 'k6';
import { Trend } from 'k6/metrics';

const serverAuth = new Trend('server_auth_ms', true);
const serverMetadata = new Trend('server_metadata_ms', true);
const serverAuthorize = new Trend('server_authorize_ms', true);
const serverQueryBuild = new Trend('server_query_build_ms', true);
const serverDb = new Trend('server_db_ms', true);
const serverApp = new Trend('server_app_to_headers_ms', true);

const baseUrl = __ENV.BASE_URL || 'http://localhost:5270';
const apiKey = __ENV.API_KEY;
const collectionId = __ENV.COLLECTION_ID || 'public.osm_addresses';

if (!apiKey) throw new Error('API_KEY environment variable is required.');

export const options = {
  stages: [
    { duration: __ENV.RAMP || '5s', target: Number(__ENV.VUS || 10) },
    { duration: __ENV.DURATION || '20s', target: Number(__ENV.VUS || 10) },
    { duration: '5s', target: 0 },
  ],
  thresholds: {
    http_req_failed: ['rate<0.01'],
    'http_req_duration{endpoint:metadata}': ['p(95)<1000'],
    'http_req_duration{endpoint:items}': ['p(95)<500'],
    checks: ['rate>0.99'],
  },
};

const params = {
  headers: { 'X-API-Key': apiKey, Accept: 'application/json' },
};

function recordServerTiming(response, endpoint) {
  const header = response.headers['Server-Timing'];
  if (!header) return;

  const values = {};
  for (const part of header.split(',')) {
    const match = part.trim().match(/^([\w-]+);dur=([\d.]+)$/);
    if (match) values[match[1]] = Number(match[2]);
  }

  const tags = { endpoint };
  if (values.auth !== undefined) serverAuth.add(values.auth, tags);
  if (values.metadata !== undefined) serverMetadata.add(values.metadata, tags);
  if (values.authorize !== undefined) serverAuthorize.add(values.authorize, tags);
  if (values.query_build !== undefined) serverQueryBuild.add(values.query_build, tags);
  if (values.db !== undefined) serverDb.add(values.db, tags);
  if (values.app !== undefined) serverApp.add(values.app, tags);
}

export default function () {
  const metadata = http.get(`${baseUrl}/metadata`, {
    ...params,
    tags: { endpoint: 'metadata' },
  });
  recordServerTiming(metadata, 'metadata');
  check(metadata, {
    'metadata returns 200': (response) => response.status === 200,
  });

  const items = http.get(
    `${baseUrl}/collections/${collectionId}/items?limit=25`,
    { ...params, tags: { endpoint: 'items' } },
  );
  recordServerTiming(items, 'items');
  check(items, {
    'items return 200': (response) => response.status === 200,
    'items are JSON': (response) =>
      (response.headers['Content-Type'] || '').includes('json'),
  });

  sleep(0.2);
}
