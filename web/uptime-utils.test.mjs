// Node-runnable tests for web/uptime-utils.js.
// Runs with the built-in Node test runner (Node 18+):
//   node --test web/uptime-utils.test.mjs
// No new test harness or dependency added.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const U = require('./uptime-utils.js');

function iso(t) { return new Date(t).toISOString(); }

test('flipsToIntervals: empty input → single unknown segment over range', () => {
  const out = U.flipsToIntervals([], 0, 1000);
  assert.deepEqual(out, [{ startMs: 0, endMs: 1000, status: 'unknown' }]);
});

test('flipsToIntervals: empty range returns []', () => {
  assert.deepEqual(U.flipsToIntervals([{ status: 'on', observedAt: iso(5) }], 10, 10), []);
  assert.deepEqual(U.flipsToIntervals([], 10, 5), []);
});

test('flipsToIntervals: anchor flip on/before since defines starting status', () => {
  // First-observation at t=0 says "off". Then flip to on at t=2000.
  // Range [1000, 3000] should be: off 1000-2000, on 2000-3000.
  const flips = [
    { status: 'off', observedAt: iso(0) },
    { status: 'on',  observedAt: iso(2000) },
  ];
  const out = U.flipsToIntervals(flips, 1000, 3000);
  assert.deepEqual(out, [
    { startMs: 1000, endMs: 2000, status: 'off' },
    { startMs: 2000, endMs: 3000, status: 'on' },
  ]);
});

test('flipsToIntervals: no flip before since → range starts as unknown', () => {
  const flips = [
    { status: 'on', observedAt: iso(500) },
    { status: 'off', observedAt: iso(800) },
  ];
  const out = U.flipsToIntervals(flips, 0, 1000);
  assert.deepEqual(out, [
    { startMs: 0,   endMs: 500, status: 'unknown' },
    { startMs: 500, endMs: 800, status: 'on' },
    { startMs: 800, endMs: 1000, status: 'off' },
  ]);
});

test('flipsToIntervals: flips outside range are ignored, last anchor still applies', () => {
  const flips = [
    { status: 'on',  observedAt: iso(100) },
    { status: 'off', observedAt: iso(200) },   // anchor for since=500
    { status: 'on',  observedAt: iso(5000) },  // beyond range
  ];
  const out = U.flipsToIntervals(flips, 500, 1000);
  assert.deepEqual(out, [{ startMs: 500, endMs: 1000, status: 'off' }]);
});

test('flipsToIntervals: adjacent same-status flips are merged', () => {
  const flips = [
    { status: 'on', observedAt: iso(0) },
    { status: 'on', observedAt: iso(100) },   // duplicate (poll re-confirms)
    { status: 'on', observedAt: iso(200) },
  ];
  const out = U.flipsToIntervals(flips, 0, 300);
  assert.deepEqual(out, [{ startMs: 0, endMs: 300, status: 'on' }]);
});

test('flipsToIntervals: unsorted input still produces correct segments', () => {
  const flips = [
    { status: 'on',  observedAt: iso(2000) },
    { status: 'off', observedAt: iso(0) },
    { status: 'off', observedAt: iso(3000) },
  ];
  const out = U.flipsToIntervals(flips, 0, 4000);
  assert.deepEqual(out, [
    { startMs: 0,    endMs: 2000, status: 'off' },
    { startMs: 2000, endMs: 3000, status: 'on' },
    { startMs: 3000, endMs: 4000, status: 'off' },
  ]);
});

test('localDayBounds: produces 24h range at local midnight', () => {
  // Use noon so DST edge cases don't bite the test.
  const noon = new Date(2024, 5, 15, 12, 0, 0).getTime();
  const { startMs, endMs } = U.localDayBounds(noon);
  const start = new Date(startMs);
  assert.equal(start.getHours(), 0);
  assert.equal(start.getMinutes(), 0);
  assert.equal(endMs - startMs, 24 * 3600 * 1000);
});

test('commonOnProbabilities: returns null when too little history', () => {
  // Just one prior day → below minDays threshold.
  const yesterday = new Date(2024, 5, 14, 9, 0, 0).getTime();
  const today = new Date(2024, 5, 15, 9, 0, 0).getTime();
  const intervals = [{ startMs: yesterday, endMs: today, status: 'on' }];
  const result = U.commonOnProbabilities(intervals, today);
  assert.equal(result, null);
});

test('commonOnProbabilities: prefers same-weekday basis when ≥ 4 weeks', () => {
  // Build 8 weeks of history, all Tuesdays "on" 9-10am, all other days off.
  const intervals = [];
  const target = new Date(2024, 5, 18, 12, 0, 0);   // a Tuesday
  for (let d = 1; d <= 56; d++) {
    const day = new Date(target.getTime() - d * 86400_000);
    const dayStart = new Date(day.getFullYear(), day.getMonth(), day.getDate()).getTime();
    const dayEnd = dayStart + 86400_000;
    if (day.getDay() === 2) {   // Tuesday
      const onStart = dayStart + 9 * 3600_000;
      const onEnd = dayStart + 10 * 3600_000;
      intervals.push({ startMs: dayStart, endMs: onStart, status: 'off' });
      intervals.push({ startMs: onStart,  endMs: onEnd,   status: 'on' });
      intervals.push({ startMs: onEnd,    endMs: dayEnd,  status: 'off' });
    } else {
      intervals.push({ startMs: dayStart, endMs: dayEnd, status: 'off' });
    }
  }
  const result = U.commonOnProbabilities(intervals, target);
  assert.ok(result, 'expected a result');
  assert.equal(result.basis, 'weekday');
  // At least 8 Tuesdays in the 56-day window.
  assert.ok(result.sampleDays >= 8);
  // 9:30am slot (slot index 19 with SLOT_COUNT=48) should be high.
  const slot = Math.floor((9.5 * 3600) / ((24 * 3600) / U.SLOT_COUNT));
  assert.ok(result.probabilities[slot] > 0.5,
    `expected 9:30am slot to be > 0.5, got ${result.probabilities[slot]}`);
  // 3am slot should be near zero.
  const dark = Math.floor((3 * 3600) / ((24 * 3600) / U.SLOT_COUNT));
  assert.ok(result.probabilities[dark] < 0.1);
});

test('commonOnProbabilities: falls back to weekday-class when same-weekday is thin', () => {
  // Only 2 Tuesdays of history, but plenty of weekdays overall.
  const target = new Date(2024, 5, 18, 12, 0, 0);   // Tuesday
  const intervals = [];
  for (let d = 1; d <= 21; d++) {
    const day = new Date(target.getTime() - d * 86400_000);
    const dow = day.getDay();
    // skip most Tuesdays so same-weekday pool is too small
    if (dow === 2 && d > 7) continue;
    const dayStart = new Date(day.getFullYear(), day.getMonth(), day.getDate()).getTime();
    intervals.push({ startMs: dayStart, endMs: dayStart + 86400_000, status: 'off' });
  }
  const result = U.commonOnProbabilities(intervals, target);
  assert.ok(result, 'expected a result');
  assert.ok(result.basis === 'weekday-class' || result.basis === 'all',
    `expected weekday-class or all, got ${result.basis}`);
});
