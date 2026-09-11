import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync, writeFileSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createFileBudget } from '../src/budget.mjs';

test('rolling 24-hour reservations survive restart; one process exclusively owns the ledger', () => {
  const directory = mkdtempSync(join(tmpdir(), 'milesian-proxy-test-'));
  const file = join(directory, 'quota.json');
  let time = 100000000;
  let budget;
  try {
    budget = createFileBudget(file, 2, () => time);
    assert.equal(budget.reserve().allowed, true);
    assert.throws(() => createFileBudget(file, 2, () => time));
    time += 1000;
    assert.equal(budget.reserve().allowed, true);
    assert.equal(budget.reserve().allowed, false);
    budget.close();
    budget = createFileBudget(file, 2, () => time);
    assert.equal(budget.reserve().allowed, false);
    time += 86400000;
    assert.equal(budget.reserve().allowed, true);
    budget.close();
    assert.equal(existsSync(file + '.lock'), false);
    assert.throws(() => budget.reserve());
  } finally { budget?.close(); rmSync(directory, { recursive: true, force: true }); }
});

test('corrupt quota and backwards clock fail closed', () => {
  const directory = mkdtempSync(join(tmpdir(), 'milesian-proxy-test-'));
  const file = join(directory, 'quota.json');
  let budget;
  try {
    writeFileSync(file, '{invalid');
    assert.throws(() => createFileBudget(file, 2, () => 50));
    assert.equal(existsSync(file + '.lock'), false);
    writeFileSync(file, JSON.stringify({ version: 1, timestamps: [100] }));
    budget = createFileBudget(file, 2, () => 50);
    assert.throws(() => budget.reserve());
  } finally { budget?.close(); rmSync(directory, { recursive: true, force: true }); }
});
