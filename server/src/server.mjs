import { createServer } from 'node:http';
import { readFileSync } from 'node:fs';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { resolve } from 'node:path';
import { createProxy } from './proxy.mjs';
import { createFileBudget } from './budget.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));

export function configFromEnv(env = process.env) {
  const integer = (name, fallback, min, max) => {
    const text = env[name];
    if (text === undefined || text === '') return fallback;
    if (!/^\d+$/.test(text)) throw new Error('Invalid server configuration');
    const value = Number(text);
    if (!Number.isSafeInteger(value) || value < min || value > max) throw new Error('Invalid server configuration');
    return value;
  };
  const host = env.HOST || '127.0.0.1';
  if (!['127.0.0.1', '0.0.0.0', '::1', '::'].includes(host)) throw new Error('Invalid server configuration');
  return {
    host,
    port: integer('PORT', 8787, 1, 65535),
    apiKey: env.NEXON_API_KEY || '',
    dailyBudget: integer('UPSTREAM_REQUESTS_PER_24H', 500, 1, 10000),
    clientRequestsPerMinute: integer('CLIENT_REQUESTS_PER_MINUTE', 600, 1, 10000),
    upstreamRequestsPerSecond: integer('UPSTREAM_REQUESTS_PER_SECOND', 5, 1, 100),
    cacheTtlMs: integer('CACHE_TTL_SECONDS', 60, 0, 300) * 1000,
    runtimeDirectory: resolve(root, env.RUNTIME_DIRECTORY || '.runtime'),
  };
}

export function createHttpServer(handler) {
  const server = createServer({ maxHeaderSize: 8192, requestTimeout: 15000, headersTimeout: 10000, keepAliveTimeout: 5000 }, async (request, response) => {
    const hasBody = request.headers['transfer-encoding'] !== undefined ||
      (request.headers['content-length'] !== undefined && request.headers['content-length'] !== '0');
    // Reply without reading any request body; do not reuse such connections.
    if (hasBody) response.setHeader('Connection', 'close');
    try {
      const result = await handler({ method: request.method, url: request.url,
        clientAddress: request.socket.remoteAddress || 'unknown', hasBody });
      if (response.destroyed) return;
      response.writeHead(result.status, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store',
        'X-Content-Type-Options': 'nosniff', ...result.headers });
      response.end(JSON.stringify(result.body));
    } catch {
      if (response.destroyed) return;
      response.writeHead(503, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store' });
      response.end(JSON.stringify({ error: { code: 'PROXY_BUSY', message: '경매장 서비스에 잠시 연결할 수 없습니다.' } }));
    }
  });
  server.maxRequestsPerSocket = 600;
  server.maxConnections = 128;
  // Keep malformed requests and their raw bytes out of logs.
  server.on('clientError', (_error, socket) => {
    if (socket.writable) socket.end('HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n');
  });
  return server;
}

export function start(env = process.env) {
  const config = configFromEnv(env);
  let names;
  try {
    names = JSON.parse(readFileSync(resolve(root, 'item-names.json'), 'utf8'));
    if (!Array.isArray(names) || names.length === 0) throw new Error();
  } catch { throw new Error('Item allowlist is missing or invalid'); }
  const budget = createFileBudget(resolve(config.runtimeDirectory, 'upstream-budget.json'), config.dailyBudget);
  const server = createHttpServer(createProxy({ ...config, allowedNames: names, budget }));
  server.once('close', () => budget.close());
  server.once('error', () => { budget.close(); console.error('Proxy could not start. Check server configuration and local port.'); process.exitCode = 1; });
  server.listen(config.port, config.host, () => console.log('Milesian Ledger auction proxy started.'));
  return server;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try {
    const server = start();
    const stop = () => server.close();
    process.once('SIGINT', stop);
    process.once('SIGTERM', stop);
  } catch {
    // Deliberately omit exception objects, environment values and upstream bodies.
    console.error('Proxy startup failed. Check configuration, item allowlist and quota state/lock.');
    process.exitCode = 1;
  }
}
