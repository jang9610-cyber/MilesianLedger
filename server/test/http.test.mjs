import test from 'node:test';
import assert from 'node:assert/strict';
import { request } from 'node:http';
import { once } from 'node:events';
import { createProxy } from '../src/proxy.mjs';
import { createHttpServer } from '../src/server.mjs';

const ITEM = '가는 실뭉치';

function send(port, path, { method = 'GET', headers = {}, body = '' } = {}) {
  return new Promise((resolve, reject) => {
    const req = request({ hostname: '127.0.0.1', port, path, method, headers }, response => {
      const chunks = [];
      response.on('data', chunk => chunks.push(chunk));
      response.on('end', () => resolve({ status: response.statusCode, headers: response.headers, text: Buffer.concat(chunks).toString('utf8') }));
    });
    req.on('error', reject);
    req.end(body);
  });
}

async function withServer(handler, action) {
  const server = createHttpServer(handler);
  try {
    server.listen(0, '127.0.0.1');
    await once(server, 'listening');
    await action(server.address().port);
  } finally {
    server.closeAllConnections();
    await new Promise(resolve => server.close(resolve));
  }
}

test('local HTTP contract: health, success, body rejection, methods and header isolation', async () => {
  const calls = [];
  const handler = createProxy({ apiKey: 'server-test-key', allowedNames: [ITEM],
    budget: { reserve: () => ({ allowed: true }) },
    fetchImpl: async (url, init) => {
      calls.push({ url, init });
      return new Response(JSON.stringify({ auction_item: [{ item_name: ITEM, auction_price_per_unit: 500, item_count: 10 }], next_cursor: null }));
    },
  });
  await withServer(handler, async port => {
    const health = await send(port, '/health');
    assert.equal(health.status, 200);
    assert.deepEqual(JSON.parse(health.text), { status: 'ok' });
    assert.equal(calls.length, 0);
    const path = '/v1/auction/list?' + new URLSearchParams({ item_name: ITEM, cursor: '' });
    const response = await send(port, path, { headers: {
      Cookie: 'private-cookie', 'x-nxopen-api-key': 'caller-key', 'X-Forwarded-For': 'fake-client', Host: 'evil.invalid',
    } });
    assert.equal(response.status, 200);
    assert.match(response.headers['content-type'], /application\/json/);
    assert.equal(response.headers['cache-control'], 'no-store');
    assert.equal(response.headers['access-control-allow-origin'], undefined);
    assert.equal(response.headers['set-cookie'], undefined);
    assert.equal(calls[0].url.origin, 'https://open.api.nexon.com');
    assert.deepEqual(calls[0].init.headers, { 'x-nxopen-api-key': 'server-test-key', Accept: 'application/json' });
    assert.equal(response.text.includes('server-test-key'), false);
    assert.equal(JSON.parse(response.text).auction_item[0].item_name, ITEM);
    assert.equal((await send(port, path, { method: 'POST' })).status, 405);
    const withBody = await send(port, path, { headers: { 'Content-Length': '1' }, body: 'x' });
    assert.equal(withBody.status, 400);
    assert.equal(withBody.headers.connection, 'close');
    assert.equal((await send(port, '/not-found')).status, 404);
    assert.equal(calls.length, 1);
  });
});

test('forwarded headers cannot bypass socket IP limits; adapter exceptions are sanitized', async () => {
  const handler = createProxy({ clientRequestsPerMinute: 1 });
  await withServer(handler, async port => {
    assert.equal((await send(port, '/health', { headers: { 'X-Forwarded-For': 'one' } })).status, 200);
    const blocked = await send(port, '/health', { headers: { 'X-Forwarded-For': 'two' } });
    assert.equal(blocked.status, 429);
    assert.equal(blocked.headers['retry-after'], '60');
  });
  await withServer(() => { throw new Error('must-not-leak'); }, async port => {
    const response = await send(port, '/health');
    assert.equal(response.status, 503);
    assert.equal(JSON.parse(response.text).error.code, 'PROXY_BUSY');
    assert.equal(response.text.includes('must-not-leak'), false);
  });
});
