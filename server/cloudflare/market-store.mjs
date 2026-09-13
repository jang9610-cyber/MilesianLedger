import { SCHEMA } from './market-schema.mjs';
import { RETENTION } from './market-core.mjs';

export class MarketStore {
  constructor(storage) {
    this.storage = storage; this.sql = storage.sql; this.sql.exec(SCHEMA);
    // Planned pilot limits are incomplete scans, not provider or storage failures.
    this.sql.exec("UPDATE market_runs SET state='limited' WHERE state='failed' AND error='MARKET_PILOT_LIMIT'");
  }
  rows(sql, ...args) { return [...this.sql.exec(sql, ...args)]; }
  one(sql, ...args) { return this.rows(sql, ...args)[0] ?? null; }
  meta(key) { return this.one('SELECT value FROM market_meta WHERE key=?', key)?.value ?? null; }
  setMeta(key, value) { this.sql.exec('INSERT OR REPLACE INTO market_meta VALUES (?,?)', key, String(value)); }
  active(kind) { return this.one("SELECT * FROM market_runs WHERE kind=? AND state='running' ORDER BY started DESC LIMIT 1", kind); }
  latest(kind) { return this.one('SELECT * FROM market_runs WHERE kind=? ORDER BY started DESC LIMIT 1', kind); }
  start(kind, now) {
    const id = `${kind}-${now}`;
    this.sql.exec("INSERT OR IGNORE INTO market_runs(id,kind,started,state) VALUES (?,?,?,'running')", id, kind, now);
    if (!this.meta('collection_started')) this.setMeta('collection_started', now);
    return this.active(kind);
  }
  fail(run, code, now) {
    this.sql.exec("UPDATE market_runs SET state=?, error=?, finished=? WHERE id=? AND state='running'", code === 'MARKET_PILOT_LIMIT' ? 'limited' : 'failed', code, now, run.id);
  }
  retry(run, code, now, delay) {
    this.sql.exec('UPDATE market_runs SET attempts=attempts+1,error=?,next_attempt=? WHERE id=?', code, now + delay, run.id);
  }
  commitPage(run, page, now) {
    this.storage.transactionSync(() => {
      const current = this.one('SELECT * FROM market_runs WHERE id=?', run.id);
      if (current?.state !== 'running' || current.pages !== run.pages) return; // Alarm redelivery.
      const cursorKey = run.cursor || '';
      if (this.one('SELECT 1 FROM market_pages WHERE run_id=? AND cursor_key=?', run.id, cursorKey)) throw Error('MARKET_REPEAT_CURSOR');
      if (page.next_cursor && (page.next_cursor === cursorKey || this.one('SELECT 1 FROM market_pages WHERE run_id=? AND cursor_key=?', run.id, page.next_cursor))) throw Error('MARKET_REPEAT_CURSOR');
      if (run.kind === 'history') {
        for (const r of page.rows) this.sql.exec('INSERT OR IGNORE INTO market_sales VALUES (?,?,?,?,?,?,?)', r.id, r.name, r.category, r.quantity, r.price, r.time, r.comparable);
      } else {
        for (const r of page.rows) this.sql.exec(`INSERT INTO market_listings VALUES (?,?,?,?,?,?,?)
          ON CONFLICT(run_id,name,category) DO UPDATE SET lots=lots+1, quantity=quantity+excluded.quantity,
          min_price=MIN(min_price,excluded.min_price), comparable=MIN(comparable,excluded.comparable)`,
          run.id, r.name, r.category, 1, r.quantity, r.price, r.comparable);
      }
      this.sql.exec('INSERT INTO market_pages VALUES (?,?)', run.id, cursorKey);
      this.sql.exec(`UPDATE market_runs SET pages=pages+1,rows_seen=rows_seen+?,cursor=?,attempts=0,error=NULL,next_attempt=0,
        state=?,finished=? WHERE id=?`, page.rows.length, page.next_cursor, page.next_cursor ? 'running' : 'complete', page.next_cursor ? null : now, run.id);
      if (!page.next_cursor) this.setMeta(`${run.kind}_published`, run.id);
    });
  }
  cleanup(now) {
    // Bounded batches keep maintenance from blocking history collection. Eight days support a rolling seven-day view.
    this.sql.exec('DELETE FROM market_sales WHERE id IN (SELECT id FROM market_sales WHERE time<? LIMIT 2000)', now - RETENTION);
    this.sql.exec(`DELETE FROM market_listings WHERE rowid IN (SELECT l.rowid FROM market_listings l
      JOIN market_runs r ON l.run_id=r.id WHERE r.state!='running'
      AND l.run_id!=COALESCE((SELECT value FROM market_meta WHERE key='list_published'),'') LIMIT 2000)`);
    this.sql.exec('DELETE FROM market_pages WHERE run_id IN (SELECT id FROM market_runs WHERE started<? AND state!=?)', now - RETENTION, 'running');
    this.sql.exec(`DELETE FROM market_runs WHERE started<? AND state!='running'
      AND id NOT IN (SELECT value FROM market_meta WHERE key IN ('history_published','list_published'))
      AND NOT EXISTS (SELECT 1 FROM market_listings l WHERE l.run_id=market_runs.id)`, now - RETENTION);
  }
  status(now) {
    const clean = r => r && ({ state: r.state, started_at: new Date(r.started).toISOString(),
      finished_at: r.finished ? new Date(r.finished).toISOString() : null, pages: r.pages, rows_seen: r.rows_seen, error: r.error });
    const published = kind => this.one('SELECT * FROM market_runs WHERE id=?', this.meta(`${kind}_published`) || '');
    const history = published('history'), list = published('list');
    const start = this.meta('collection_started');
    const failed = this.one("SELECT COUNT(*) AS n FROM market_runs WHERE state='failed' AND started>?", now - 7 * 86400_000).n;
    const limited = this.one("SELECT COUNT(*) AS n FROM market_runs WHERE state='limited' AND started>?", now - 7 * 86400_000).n;
    return { collection_started_at: start ? new Date(+start).toISOString() : null,
      history: { latest_run: clean(this.latest('history')), published: clean(history), stale: !history || now - history.finished > 40 * 60_000 },
      listings: { latest_run: clean(this.latest('list')), published: clean(list), stale: !list || now - list.finished > 90 * 60_000 },
      failed_runs_7d: failed, limited_runs_7d: limited, history_window_hours: 1, api_delay_minutes: 10,
      scope: 'all_categories', coverage: 'observed_api_records', image_support: 'placeholder',
      notice: '공식 API에서 관측한 통계입니다. 수집 시작 전·장애 구간은 소급 복원되지 않으며, 매물은 수집 구간 중 변동될 수 있습니다.' };
  }
  rankings(query, now) {
    const since = now - (query.window === '7d' ? 7 : 1) * 86400_000;
    const order = { quantity: 'sold_quantity', trades: 'trade_count', gold: 'traded_gold', supply: 'listed_quantity' }[query.sort];
    const result = this.rows(`WITH trades AS (
      SELECT name,category,SUM(quantity) AS sold_quantity,COUNT(*) AS trade_count,
        SUM(CAST(price AS REAL)*quantity) AS traded_gold, MIN(comparable) AS comparable
      FROM market_sales WHERE time>=? AND time<=? GROUP BY name,category
    ), stock AS (SELECT * FROM market_listings WHERE run_id=?), names AS (
      SELECT name,category FROM trades UNION SELECT name,category FROM stock
    ) SELECT n.name,n.category,COALESCE(t.sold_quantity,0) AS sold_quantity,COALESCE(t.trade_count,0) AS trade_count,
      COALESCE(t.traded_gold,0) AS traded_gold,COALESCE(s.quantity,0) AS listed_quantity,COALESCE(s.lots,0) AS listing_count,
      CASE WHEN t.comparable=1 THEN t.traded_gold/t.sold_quantity ELSE NULL END AS average_sale_price,
      CASE WHEN s.comparable=1 THEN s.min_price ELSE NULL END AS lowest_listing_price,
      MIN(COALESCE(t.comparable,1),COALESCE(s.comparable,1)) AS comparable
    FROM names n LEFT JOIN trades t ON n.name=t.name AND n.category=t.category
      LEFT JOIN stock s ON n.name=s.name AND n.category=s.category
    WHERE (?='' OR n.category=?) AND (?='' OR instr(n.name,?)>0)
    ORDER BY ${order} DESC,n.name ASC,n.category ASC LIMIT ? OFFSET ?`, since, now, this.meta('list_published') || '',
    query.category, query.category, query.search, query.search, query.limit + 1, query.offset);
    return { window: query.window, sort: query.sort, offset: query.offset, has_more: result.length > query.limit,
      items: result.slice(0, query.limit).map(r => ({ ...r, image_url: null,
        traded_gold: Number.isSafeInteger(r.traded_gold) ? r.traded_gold : null,
        price_comparable: Boolean(r.comparable),
        // A missing snapshot is unknown supply, not zero stock.
        listed_quantity: this.meta('list_published') ? r.listed_quantity : null,
        listing_count: this.meta('list_published') ? r.listing_count : null,
        average_sale_price: r.comparable ? r.average_sale_price : null,
        lowest_listing_price: r.comparable ? r.lowest_listing_price : null,
      })), status: this.status(now), generated_at: new Date(now).toISOString() };
  }
}
