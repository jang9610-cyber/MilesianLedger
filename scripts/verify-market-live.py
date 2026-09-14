"""Read shared-market status, or run an explicitly bounded authenticated server pilot."""
import argparse, gzip, hashlib, json, pathlib, urllib.request, urllib.error, urllib.parse

root = pathlib.Path(__file__).resolve().parents[1]
config = json.loads((root / 'server/cloudflare/wrangler.market.jsonc').read_text(encoding='utf-8-sig'))
base = 'https://' + config['name'] + '.jang9610.workers.dev'
parser = argparse.ArgumentParser()
parser.add_argument('--pilot', help='Start an idempotent pilot with this operation ID')
parser.add_argument('--pages', type=int, default=2000)
parser.add_argument('--metrics', action='store_true')
parser.add_argument('--snapshot', action='store_true')
parser.add_argument('--output', type=pathlib.Path)
parser.add_argument('--summary', action='store_true', help='Print compact verification facts; output file retains full data')
parser.add_argument('--legacy-check', action='store_true', help='With --metrics, verify released-client quotes spend no upstream requests while collector is idle')
args = parser.parse_args()

def get(path, admin=False, method='GET', headers=None):
    request_headers = {'User-Agent': 'MilesianLedger/1.1.0-beta.1', 'Accept': 'application/json'}
    if headers:
        request_headers.update(headers)
    if admin:
        token = (root / 'server/cloudflare/.wrangler/market-admin-token').read_text().strip()
        request_headers['Authorization'] = 'Bearer ' + token
    try:
        response = urllib.request.urlopen(urllib.request.Request(base + path, headers=request_headers, method=method), timeout=50)
    except urllib.error.HTTPError as response:
        return response.code, dict(response.headers), response.read()
    with response:
        return response.status, dict(response.headers), response.read()

path = '/v1/market/admin/pilot?id=' + args.pilot + '&pages=' + str(args.pages) if args.pilot else '/v1/market/admin/metrics' if args.metrics else '/v1/market/status'
code, headers, body = get(path, args.metrics or bool(args.pilot), 'POST' if args.pilot else 'GET')
result = {'status': code, 'data': json.loads(body)}
if args.legacy_check:
    assert args.metrics and code == 200 and result['data'].get('shared_quotes_enabled'), 'Common quotes must already be enabled'
    before = result['data']['upstream_usage']['requests_24h']
    assert all(result['data']['status'][kind]['latest_run']['state'] != 'running' for kind in ['history', 'listings']), 'Wait until the collector is idle'
    names = ['거미줄', '굵은 실뭉치', '미스릴 광석']
    checked = []
    for name in names:
        status, _, payload = get('/v1/auction/list?' + urllib.parse.urlencode({'item_name': name}))
        quote = json.loads(payload)
        assert status == 200 and quote.get('source') == 'shared_snapshot'
        assert all(item['item_name'] == name for item in quote['auction_item'])
        checked.append({'name': name, 'source': quote['source'], 'fetched_at': quote['fetched_at'], 'rows': len(quote['auction_item'])})
    status, _, payload = get('/v1/market/admin/metrics', True)
    after = json.loads(payload)['upstream_usage']['requests_24h']
    assert status == 200 and before == after, 'Upstream usage changed; check for concurrent scheduled collection'
    result['legacy_check'] = {'items': checked, 'upstream_before': before, 'upstream_after': after, 'additional_upstream_requests': after - before}
if args.snapshot:
    code, headers, body = get('/v1/market/manifest')
    result['manifest_status'] = code
    if code == 200:
        manifest = json.loads(body)
        code, _, compressed = get(manifest['snapshot_url'])
        assert code == 200 and len(compressed) == manifest['compressed_bytes']
        assert hashlib.sha256(compressed).hexdigest() == manifest['sha256']
        raw = gzip.decompress(compressed)
        assert len(raw) == manifest['uncompressed_bytes']
        data = json.loads(raw)
        conditional, _, _ = get('/v1/market/manifest', headers={'If-None-Match': '"' + manifest['version'] + '"'})
        result['snapshot'] = {'version': manifest['version'], 'compressed_bytes': len(compressed), 'uncompressed_bytes': len(raw),
                              'items_24h': len(data['items_24h']), 'items_7d': len(data['items_7d']), 'quotes': len(data['quotes']),
                              'conditional_status': conditional, 'generated_at': data['generated_at'],
                              'listings_fetched_at': data.get('listings_fetched_at')}
if args.output:
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
if args.summary:
    data = result.get('data', {})
    status = data.get('status', data)
    result = { 'http_status': result['status'], 'storage_version': data.get('storage_version'),
              'history': status.get('history', {}).get('latest_run'), 'listings': status.get('listings', {}).get('latest_run'),
              'database_bytes': data.get('database_bytes'), 'sql_budget': data.get('sql_budget'),
              'upstream_usage': data.get('upstream_usage'),
              'alarm_at': data.get('alarm_at'), 'server_time': data.get('server_time'),
              'snapshot_error': data.get('snapshot_error'), 'snapshot': result.get('snapshot'),
              'schedule_enabled': data.get('schedule_enabled'), 'shared_quotes_enabled': data.get('shared_quotes_enabled'),
              'listing_interval_minutes': data.get('listing_interval_minutes'), 'legacy_check': result.get('legacy_check') }
print(json.dumps(result, ensure_ascii=False, indent=2))
