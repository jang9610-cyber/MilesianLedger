import { SCHEMA } from './market-schema.mjs';
import { RETENTION } from './market-core.mjs';
import { isUnnamedEnchantScroll } from './market-enchant.mjs';
import { isPriceComparable, PRICE_COMPARISON_POLICY_VERSION } from './market-price-policy.mjs';
import { encodeChunks, decodeChunk, itemKey, addSafe, MAX_CHUNK_BYTES } from './market-chunks.mjs';

const DAY = 86400_000;
const MAX_SNAPSHOT_NAMES = 40_000;
// SQL row counters are measured by Cloudflare's actual SQLite cursors. They include
// index writes, unlike a count of application records. No accounting row is written
// for every query: the caller can checkpoint these deltas with its invocation budget.
export class MarketStore {
  constructor(storage, options = {}) {
    this.storage = storage; this.sql = storage.sql; this.onAccounting = options.onAccounting;
    this.accounting = { rows_read: 0, rows_written: 0, queries: 0, counters_available: true };
    this.exec(SCHEMA);
  }
  *iterate(sql, ...args) {
    const cursor = this.sql.exec(sql, ...args);
    let returned = 0;
    try { for (const row of cursor) { returned++; yield row; } }
    finally {
      const available = typeof cursor.rowsRead === 'number' && typeof cursor.rowsWritten === 'number';
      const verb = sql.trim().split(/\s+/)[0].toUpperCase();
      // Offline adapters without cursor counters use conservative estimates.
      const estimatedWrite = verb === 'INSERT' ? 4 : verb === 'UPDATE' ? 8 : verb === 'DELETE' ? 2000 : 0;
      const delta = { rows_read: available ? cursor.rowsRead : Math.max(returned, verb === 'SELECT' ? 1000 : 1),
        rows_written: available ? cursor.rowsWritten : estimatedWrite, queries: 1 };
      this.accounting.rows_read += delta.rows_read; this.accounting.rows_written += delta.rows_written;
      this.accounting.queries++; this.accounting.counters_available &&= available;
      this.onAccounting?.(delta);
    }
  }
  rows(sql, ...args) { return [...this.iterate(sql, ...args)]; }
  exec(sql, ...args) { for (const unused of this.iterate(sql, ...args)) { /* Consume cursor accounting. */ } }
  one(sql, ...args) { return this.rows(sql, ...args)[0] ?? null; }
  meta(key) { return this.one('SELECT value FROM market_v2_meta WHERE key=?', key)?.value ?? null; }
  setMeta(key, value) {
    if (value === null || value === undefined) this.exec('DELETE FROM market_v2_meta WHERE key=?', key);
    else this.exec('INSERT OR REPLACE INTO market_v2_meta VALUES (?,?)', key, String(value));
  }
  active(kind) { return this.one("SELECT * FROM market_v2_runs WHERE kind=? AND state='running' ORDER BY started DESC LIMIT 1", kind); }
  latest(kind) { return this.one('SELECT * FROM market_v2_runs WHERE kind=? ORDER BY started DESC LIMIT 1', kind); }
  start(kind, now) {
    if (!['history', 'list'].includes(kind)) throw Error('MARKET_INVALID_KIND');
    const active = this.active(kind); if (active) return active;
    const id = `${kind}-${now}`;
    this.exec("INSERT OR IGNORE INTO market_v2_runs(id,kind,started,state) VALUES (?,?,?,'running')", id, kind, now);
    if (!this.meta('collection_started')) this.setMeta('collection_started', now);
    return this.active(kind);
  }
  fail(run, code, now) {
    this.exec("UPDATE market_v2_runs SET state=?,error=?,finished=? WHERE id=? AND state='running'", code === 'MARKET_PILOT_LIMIT' ? 'limited' : 'failed', code, now, run.id);
  }
  retry(run, code, now, delay) {
    this.exec("UPDATE market_v2_runs SET attempts=attempts+1,error=?,next_attempt=? WHERE id=? AND state='running'", code, now + delay, run.id);
  }
  deduplicate(rows, run) {
    // The official auction_buy_id identifies a completed trade with an immutable
    // date_auction_buy. Its timestamp lets us find overlapping chunks through an
    // index, then compare exact IDs (never probabilistic hashes). Only the current
    // run and completed runs participate. Aborted scans cannot suppress later data.
    const unseen = new Map();
    for (const row of rows) if (!unseen.has(row.id)) unseen.set(row.id, row);
    if (!unseen.size) return [];
    const times = rows.map(r => r.time), oldest = Math.min(...times), newest = Math.max(...times);
    for (const chunk of this.iterate(`SELECT c.payload FROM market_v2_chunks c INDEXED BY v2_chunk_time
      JOIN market_v2_runs r ON r.id=c.run_id
      WHERE c.max_time>=? AND c.min_time<=? AND r.kind='history'
        AND (r.state='complete' OR r.id=?)`, oldest, newest, run.id)) {
      for (const [id] of decodeChunk(chunk.payload).ids) unseen.delete(id);
    }
    return [...unseen.values()];
  }
  commitPage(run, page, now) {
    if (!Array.isArray(page.rows) || page.rows.length > 500) throw Error('MARKET_INVALID_PAGE');
    return this.storage.transactionSync(() => {
      const current = this.one('SELECT * FROM market_v2_runs WHERE id=?', run.id);
      if (current?.state !== 'running' || current.pages !== run.pages || current.cursor !== run.cursor) return false;
      const cursorKey = current.cursor || '';
      if (this.one('SELECT 1 FROM market_v2_pages WHERE run_id=? AND cursor_key=?', run.id, cursorKey)) throw Error('MARKET_REPEAT_CURSOR');
      if (page.next_cursor && (page.next_cursor === cursorKey || this.one('SELECT 1 FROM market_v2_pages WHERE run_id=? AND cursor_key=?', run.id, page.next_cursor))) throw Error('MARKET_REPEAT_CURSOR');
      const accepted = run.kind === 'history' ? this.deduplicate(page.rows, current) : page.rows;
      let part = 0;
      for (const chunk of encodeChunks(accepted, run.kind)) this.exec('INSERT INTO market_v2_chunks VALUES (?,?,?,?,?,?,?)',
        run.id, current.pages, part++, chunk.min_time, chunk.max_time, chunk.records, chunk.payload);
      this.exec('INSERT INTO market_v2_pages VALUES (?,?)', run.id, cursorKey);
      // Intermediate checkpoints do not assign the indexed state column again.
      // SQLite rewrites v2_runs_state when state appears in SET, even if unchanged.
      const completion = page.next_cursor ? '' : ",state='complete',finished=?";
      this.exec(`UPDATE market_v2_runs SET pages=pages+1,rows_seen=rows_seen+?,cursor=?,attempts=0,error=NULL,next_attempt=0${completion}
        WHERE id=?`, page.rows.length, page.next_cursor, ...(page.next_cursor ? [] : [now]), run.id);
      // This pointer and the last page are one SQLite transaction. Publication
      // always reads complete runs, including history: no partial-run statistics.
      if (!page.next_cursor) this.setMeta(`${run.kind}_published`, run.id);
      return true;
    });
  }
  publicationSignature() {
    const runs = `${this.meta('history_published') || ''}|${this.meta('list_published') || ''}`;
    return runs === '|' ? runs : PRICE_COMPARISON_POLICY_VERSION + ':' + runs;
  }
  published(kind) {
    return this.one("SELECT * FROM market_v2_runs WHERE id=? AND state='complete'", this.meta(`${kind}_published`) || '');
  }
  cleanup(now, options = {}) {
    // Thousands of page alarms must not repeatedly scan the retention index.
    // One minute still provides 6,000 retired chunks/hour cleanup capacity.
    if (now < Number(this.meta('cleanup_due_at') || 0)) return false;
    const limit = (value, fallback, max) => Number.isSafeInteger(value) && value > 0 ? Math.min(value, max) : fallback;
    const chunks = limit(options.maxChunks, 100, 500), pages = limit(options.maxPages, 200, 1000), runs = limit(options.maxRuns, 100, 500);
    this.storage.transactionSync(() => {
      const cutoff = now - RETENTION;
      this.exec("UPDATE market_v2_runs SET state='failed',error='MARKET_RUN_EXPIRED',finished=? WHERE kind IN ('history','list') AND state='running' AND started<?", now, cutoff);
      // Each UNION arm uses the time index or run primary-key lookup. Avoid an OR
      // JOIN that would read every retained history chunk on every page alarm.
      this.exec(`DELETE FROM market_v2_chunks WHERE (run_id,page,part) IN (
        SELECT run_id,page,part FROM market_v2_chunks WHERE max_time<?
        UNION ALL
        SELECT c.run_id,c.page,c.part FROM market_v2_runs r JOIN market_v2_chunks c ON c.run_id=r.id
          WHERE r.kind='history' AND r.state IN ('failed','limited')
        UNION ALL
        SELECT c.run_id,c.page,c.part FROM market_v2_runs r JOIN market_v2_chunks c ON c.run_id=r.id
          WHERE r.kind='list' AND r.state IN ('complete','failed','limited')
            AND (r.started<? OR r.id!=COALESCE((SELECT value FROM market_v2_meta WHERE key='list_published'),'')) LIMIT ?)`, cutoff, cutoff, chunks);
      this.exec(`DELETE FROM market_v2_pages WHERE (run_id,cursor_key) IN (
        SELECT run_id,cursor_key FROM market_v2_pages WHERE run_id IN (
          SELECT id FROM market_v2_runs WHERE kind IN ('history','list') AND state IN ('complete','failed','limited')) LIMIT ?)`, pages);
      this.exec(`DELETE FROM market_v2_runs WHERE id IN (
        SELECT r.id FROM market_v2_runs r WHERE r.kind IN ('history','list') AND r.started<? AND r.state IN ('complete','failed','limited')
          AND NOT EXISTS (SELECT 1 FROM market_v2_chunks c WHERE c.run_id=r.id)
          AND NOT EXISTS (SELECT 1 FROM market_v2_pages p WHERE p.run_id=r.id) LIMIT ?)`, cutoff, runs);
      this.exec("DELETE FROM market_v2_meta WHERE key LIKE 'sql_budget:%' AND key<?", `sql_budget:${new Date(cutoff).toISOString().slice(0, 10)}`);
      for (const kind of ['history', 'list']) {
        const current = this.meta(`${kind}_published`);
        if (current && !this.one('SELECT 1 FROM market_v2_runs WHERE id=?', current)) this.exec('DELETE FROM market_v2_meta WHERE key=?', `${kind}_published`);
      }
      this.setMeta('cleanup_due_at', now + 60_000);
    });
    return true;
  }
  status(now) {
    const clean = r => r && ({ state: r.state, started_at: new Date(r.started).toISOString(),
      finished_at: r.finished ? new Date(r.finished).toISOString() : null, pages: r.pages, rows_seen: r.rows_seen, error: r.error });
    const history = this.published('history'), list = this.published('list'), start = this.meta('collection_started');
    const counts = this.one("SELECT SUM(CASE WHEN state='failed' THEN 1 ELSE 0 END) AS failed,SUM(CASE WHEN state='limited' THEN 1 ELSE 0 END) AS limited FROM market_v2_runs WHERE started>?", now - 7 * DAY);
    return { collection_started_at: start ? new Date(+start).toISOString() : null,
      history: { latest_run: clean(this.latest('history')), published: clean(history), stale: !history || now - history.finished > 40 * 60_000 },
      listings: { latest_run: clean(this.latest('list')), published: clean(list), stale: !list || now - list.finished > 90 * 60_000 },
      failed_runs_7d: counts.failed || 0, limited_runs_7d: counts.limited || 0,
      history_window_hours: 1, api_delay_minutes: 10, retention_days: 8,
      scope: 'all_categories', coverage: 'completed_scans_only', image_support: 'placeholder', storage_version: 2,
      notice: '공식 API의 완료된 수집 구간만 집계합니다. 수집 시작 전·장애 구간은 소급 복원되지 않으며, 매물은 수집 중 변동될 수 있습니다.' };
  }
  reserveCollectionPage(now, options = {}) {
    const day = new Date(now).toISOString().slice(0, 10), key = `sql_budget:${day}`;
    const positive = (value, fallback) => Number.isSafeInteger(value) && value > 0 ? value : fallback;
    const writeLimit = positive(options.writeLimit, 60_000), readLimit = positive(options.readLimit, 3_000_000);
    const reserveWrites = positive(options.reserveWrites, 20), reserveReads = positive(options.reserveReads, 1000);
    const baseline = { ...this.accounting };
    return this.storage.transactionSync(() => {
      const budget = JSON.parse(this.meta(key) || '{}');
      budget.day = day; budget.charged_writes ||= 0; budget.charged_reads ||= 0;
      budget.actual_writes ||= 0; budget.actual_reads ||= 0; budget.reservations ||= 0;
      const allowed = budget.charged_writes + reserveWrites <= writeLimit && budget.charged_reads + reserveReads <= readLimit;
      if (allowed) {
        budget.charged_writes += reserveWrites; budget.charged_reads += reserveReads; budget.reservations++;
        this.setMeta(key, JSON.stringify(budget));
      }
      return { allowed, day, reserved_writes: reserveWrites, reserved_reads: reserveReads, baseline,
        charged_writes: budget.charged_writes, charged_reads: budget.charged_reads,
        write_limit: writeLimit, read_limit: readLimit,
        retry_after_seconds: allowed ? 0 : Math.max(1, Math.ceil((Date.parse(day) + DAY - now) / 1000)) };
    });
  }
  settleCollectionPage(reservation, now = Date.now()) {
    if (!reservation?.allowed || reservation.settled) return;
    this.storage.transactionSync(() => {
      const key = `sql_budget:${reservation.day}`, budget = JSON.parse(this.meta(key) || '{}');
      // Native workerd measures this WITHOUT ROWID primary-key replacement as one
      // write. Charge two to include it and retain one extra write of headroom.
      // A reservation survives process death and approximate counters never refund.
      const written = Math.max(0, this.accounting.rows_written - reservation.baseline.rows_written) + 2;
      const read = Math.max(0, this.accounting.rows_read - reservation.baseline.rows_read) + 2;
      // Refund only when every query in this invocation has authoritative
      // native counters. Offline estimates retain the full reservation; an
      // invocation that crashes before settlement also keeps its reservation.
      const measured = reservation.baseline.counters_available && this.accounting.counters_available;
      const adjustment = (actual, reserved) => measured ? actual - reserved : Math.max(0, actual - reserved);
      budget.charged_writes = Math.max(0, (budget.charged_writes || 0) + adjustment(written, reservation.reserved_writes));
      budget.charged_reads = Math.max(0, (budget.charged_reads || 0) + adjustment(read, reservation.reserved_reads));
      budget.actual_writes = (budget.actual_writes || 0) + written;
      budget.actual_reads = (budget.actual_reads || 0) + read;
      budget.updated_at = new Date(now).toISOString(); budget.counters_available = this.accounting.counters_available;
      this.setMeta(key, JSON.stringify(budget));
    });
    reservation.settled = true;
  }
  chunkCount() { return this.one('SELECT COUNT(*) AS n FROM market_v2_chunks').n; }
  diagnostics(now = Date.now()) {
    const history = this.one(`SELECT COALESCE(SUM(c.records),0) AS records,COUNT(*) AS chunks,MIN(c.min_time) AS oldest_ms,MAX(c.max_time) AS newest_ms
      FROM market_v2_chunks c JOIN market_v2_runs r ON r.id=c.run_id WHERE r.kind='history' AND r.state='complete'`);
    const chunks = this.one('SELECT COUNT(*) AS count,COALESCE(SUM(LENGTH(CAST(payload AS BLOB))),0) AS bytes FROM market_v2_chunks');
    return { storage_version: 2, stored_trades: history.records, history_chunks: history.chunks,
      trade_time_range: { oldest_ms: history.oldest_ms, newest_ms: history.newest_ms },
      chunks: chunks.count, bytes: chunks.bytes, max_chunk_bytes: MAX_CHUNK_BYTES, sql_accounting: { ...this.accounting },
      sql_budget: JSON.parse(this.meta(`sql_budget:${new Date(now).toISOString().slice(0, 10)}`) || 'null') };
  }
  buildSnapshot(now) {
    const since24 = now - DAY, since7 = now - 7 * DAY;
    const day = new Map(), week = new Map(), stock = new Map();
    const sales = (map, names, values) => {
      const [i, time, quantity, trades, gold] = values;
      const [name, category] = names[i], key = itemKey(name, category);
      if (!map.has(key)) {
        if (map.size >= MAX_SNAPSHOT_NAMES) throw Error('MARKET_SNAPSHOT_TOO_MANY_ITEMS');
        map.set(key, { name, category, sold_quantity: 0, trade_count: 0, traded_gold: 0 });
      }
      const item = map.get(key);
      item.sold_quantity = addSafe(item.sold_quantity, quantity); item.trade_count = addSafe(item.trade_count, trades);
      item.traded_gold = addSafe(item.traded_gold, gold);
    };
    for (const chunk of this.iterate(`SELECT c.payload FROM market_v2_chunks c
      JOIN market_v2_runs r ON r.id=c.run_id WHERE r.kind='history' AND r.state='complete' AND c.max_time>=? AND c.min_time<=?`, since7, now)) {
      const data = decodeChunk(chunk.payload);
      for (const values of data.sales) {
        if (values[1] >= since7 && values[1] <= now) sales(week, data.names, values);
        if (values[1] >= since24 && values[1] <= now) sales(day, data.names, values);
      }
    }
    const publishedList = this.published('list');
    const list = publishedList && publishedList.started >= now - RETENTION ? publishedList : null;
    if (list) for (const chunk of this.iterate('SELECT payload FROM market_v2_chunks WHERE run_id=? ORDER BY page,part', list.id)) {
      const data = decodeChunk(chunk.payload);
      for (const [i, lots, quantity, price] of data.listings) {
        const [name, category] = data.names[i], key = itemKey(name, category);
        if (!stock.has(key)) {
          if (stock.size >= MAX_SNAPSHOT_NAMES) throw Error('MARKET_SNAPSHOT_TOO_MANY_ITEMS');
          stock.set(key, { name, category, listed_quantity: 0, listing_count: 0, min_price: price });
        }
        const item = stock.get(key);
        item.listed_quantity = addSafe(item.listed_quantity, quantity); item.listing_count = addSafe(item.listing_count, lots);
        item.min_price = Math.min(item.min_price, price);
      }
    }
    const combine = map => {
      const keys = new Set([...map.keys(), ...stock.keys()]);
      if (keys.size > MAX_SNAPSHOT_NAMES) throw Error('MARKET_SNAPSHOT_TOO_MANY_ITEMS');
      return [...keys].sort().map(key => {
        const trade = map.get(key), listing = stock.get(key), source = trade || listing;
        // Older chunks recorded a narrow allowlist and rejected any option metadata.
        // Derive the current policy from the retained identity, without rewriting
        // chunks or fetching trades again (which would also break exact ID dedup).
        const comparable = isPriceComparable(source.name, source.category);
        return { name: source.name, category: source.category, image_url: null,
          sold_quantity: trade ? trade.sold_quantity : 0, trade_count: trade ? trade.trade_count : 0,
          traded_gold: trade ? trade.traded_gold : 0,
          listed_quantity: list ? (listing ? listing.listed_quantity : 0) : null,
          listing_count: list ? (listing ? listing.listing_count : 0) : null,
          average_sale_price: comparable && trade?.sold_quantity && trade.traded_gold !== null ? trade.traded_gold / trade.sold_quantity : null,
          lowest_listing_price: isUnnamedEnchantScroll(source.name) ? null : listing?.min_price ?? null,
          price_comparable: comparable };
      });
    };
    // Unidentified scroll lots retain availability but cannot represent one enchant's
    // price. This also guards already-stored chunks from before named ingestion.
    const quotesByName = new Map();
    for (const listing of stock.values()) {
      const price = isUnnamedEnchantScroll(listing.name) ? null : listing.min_price;
      if (!quotesByName.has(listing.name)) quotesByName.set(listing.name, { name: listing.name, unit_price: price,
        listing_count: 0, quantity: 0, fetched_at: new Date(list.finished).toISOString() });
      const quote = quotesByName.get(listing.name);
      quote.unit_price = price === null || quote.unit_price === null ? null : Math.min(quote.unit_price, price);
      quote.listing_count = addSafe(quote.listing_count, listing.listing_count); quote.quantity = addSafe(quote.quantity, listing.listed_quantity);
    }
    return { schema_version: 1, generated_at: new Date(now).toISOString(),
      history_fetched_at: this.published('history')?.finished ? new Date(this.published('history').finished).toISOString() : null,
      listings_fetched_at: list ? new Date(list.finished).toISOString() : null,
      cutoff_24h: new Date(since24).toISOString(), cutoff_7d: new Date(since7).toISOString(),
      items_24h: combine(day), items_7d: combine(week), quotes: [...quotesByName.values()].sort((a, b) => a.name.localeCompare(b.name)),
      status: this.status(now) };
  }
  rankings(query, now) {
    // Compatibility entry point for offline tools. The public worker serves the
    // published snapshot; it must not call this method for each app request.
    const snapshot = this.buildSnapshot(now);
    const order = { quantity: 'sold_quantity', trades: 'trade_count', gold: 'traded_gold', supply: 'listed_quantity' }[query.sort] || 'sold_quantity';
    const items = snapshot[query.window === '7d' ? 'items_7d' : 'items_24h'].filter(r => (!query.category || r.category === query.category) && (!query.search || r.name.includes(query.search)))
      .sort((a, b) => (b[order] || 0) - (a[order] || 0) || a.name.localeCompare(b.name) || a.category.localeCompare(b.category));
    return { window: query.window, sort: query.sort, offset: query.offset, has_more: items.length > query.offset + query.limit,
      items: items.slice(query.offset, query.offset + query.limit), status: snapshot.status, generated_at: snapshot.generated_at };
  }
}
