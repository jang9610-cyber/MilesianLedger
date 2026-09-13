// Separate SQLite Durable Object; never migrates the existing barter quota database.
export const SCHEMA = `
CREATE TABLE IF NOT EXISTS market_runs (
  id TEXT PRIMARY KEY, kind TEXT NOT NULL, started INTEGER NOT NULL, finished INTEGER,
  state TEXT NOT NULL, pages INTEGER NOT NULL DEFAULT 0, rows_seen INTEGER NOT NULL DEFAULT 0,
  cursor TEXT, attempts INTEGER NOT NULL DEFAULT 0, error TEXT, next_attempt INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS runs_state ON market_runs(kind, state, started);
CREATE TABLE IF NOT EXISTS market_pages (
  run_id TEXT NOT NULL, cursor_key TEXT NOT NULL, PRIMARY KEY(run_id, cursor_key)
);
CREATE TABLE IF NOT EXISTS market_sales (
  id TEXT PRIMARY KEY, name TEXT NOT NULL, category TEXT NOT NULL,
  quantity INTEGER NOT NULL, price INTEGER NOT NULL, time INTEGER NOT NULL, comparable INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS sales_time ON market_sales(time);
CREATE INDEX IF NOT EXISTS sales_name_time ON market_sales(name, category, time);
CREATE TABLE IF NOT EXISTS market_listings (
  run_id TEXT NOT NULL, name TEXT NOT NULL, category TEXT NOT NULL, lots INTEGER NOT NULL,
  quantity INTEGER NOT NULL, min_price INTEGER NOT NULL, comparable INTEGER NOT NULL,
  PRIMARY KEY(run_id, name, category)
);
CREATE TABLE IF NOT EXISTS market_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);

-- V2 is isolated from the incomplete pilot data. Legacy tables remain untouched.
CREATE TABLE IF NOT EXISTS market_v2_runs (
  id TEXT PRIMARY KEY, kind TEXT NOT NULL, started INTEGER NOT NULL, finished INTEGER,
  state TEXT NOT NULL, pages INTEGER NOT NULL DEFAULT 0, rows_seen INTEGER NOT NULL DEFAULT 0,
  cursor TEXT, attempts INTEGER NOT NULL DEFAULT 0, error TEXT, next_attempt INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS v2_runs_state ON market_v2_runs(kind, state, started);
CREATE TABLE IF NOT EXISTS market_v2_pages (
  run_id TEXT NOT NULL, cursor_key TEXT NOT NULL, PRIMARY KEY(run_id, cursor_key)
) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS market_v2_chunks (
  run_id TEXT NOT NULL, page INTEGER NOT NULL, part INTEGER NOT NULL,
  min_time INTEGER, max_time INTEGER, records INTEGER NOT NULL,
  payload TEXT NOT NULL, PRIMARY KEY(run_id, page, part)
) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS v2_chunk_time ON market_v2_chunks(max_time, min_time);
CREATE TABLE IF NOT EXISTS market_v2_meta (
  key TEXT PRIMARY KEY, value TEXT NOT NULL
) WITHOUT ROWID;
`;
