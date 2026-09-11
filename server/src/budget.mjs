import { mkdirSync, readFileSync, writeFileSync, renameSync, openSync, closeSync, unlinkSync, existsSync, statSync } from 'node:fs';
import { dirname } from 'node:path';

const DAY = 24 * 60 * 60 * 1000;

// One process owns this file. Reserve before fetching: failed requests also spend budget.
export function createFileBudget(file, limit, now = Date.now) {
  mkdirSync(dirname(file), { recursive: true, mode: 0o700 });
  const lockFile = file + '.lock';
  const lock = openSync(lockFile, 'wx', 0o600);
  let times = [];
  let released = false;
  const release = () => {
    if (released) return;
    released = true;
    closeSync(lock);
    unlinkSync(lockFile);
  };
  try {
    if (existsSync(file)) {
      if (statSync(file).size > 1024 * 1024) throw new Error('Invalid quota state');
      const state = JSON.parse(readFileSync(file, 'utf8'));
      if (state.version !== 1 || !Array.isArray(state.timestamps) || state.timestamps.length > 10000 ||
          state.timestamps.some((t, i) => !Number.isSafeInteger(t) || t < 0 || (i > 0 && t < state.timestamps[i - 1]))) {
        throw new Error('Invalid quota state');
      }
      times = state.timestamps;
    }
  } catch {
    release();
    throw new Error('Quota state could not be loaded');
  }
  return {
    reserve() {
      if (released) throw new Error('Quota store closed');
      const time = now();
      // Clock moving backwards must not give an extra allocation.
      if (times.length && times[times.length - 1] > time) throw new Error('Clock moved backwards');
      const recent = times.filter(t => t > time - DAY);
      if (recent.length >= limit) return { allowed: false, retryAfter: Math.max(1, Math.ceil((recent[0] + DAY - time) / 1000)) };
      recent.push(time);
      // Never send an upstream request if durable reservation fails.
      writeFileSync(file + '.tmp', JSON.stringify({ version: 1, timestamps: recent }), { mode: 0o600, flush: true });
      renameSync(file + '.tmp', file);
      times = recent;
      return { allowed: true };
    },
    close: release,
  };
}
