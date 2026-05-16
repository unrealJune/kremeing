// Pure helpers for reconstructing hot-light history from raw flip events.
// Used by both the browser (via window.KREMEING_UPTIME) and Node tests
// (via require()), so this file MUST NOT touch `window`, `document`, or
// `fetch`. UMD wrapper handles both environments.
//
// The data shapes here line up with openapi.yaml:
//   HotLightFlip   = { storeId, status: 'on'|'off'|'unknown', observedAt: ISO }
//   HotLightHistory.flips[0] is a FirstObservation anchor — its `observedAt`
//   marks when tracking began. Anything before it is genuinely unknown.

(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    root.KREMEING_UPTIME = factory();
  }
}(typeof self !== 'undefined' ? self : this, function () {

  // ── flipsToIntervals(flips, sinceMs, untilMs) ────────────────────────
  // Produces a contiguous list of segments covering exactly [sinceMs, untilMs]:
  //   { startMs, endMs, status: 'on'|'off'|'unknown' }
  // Segments never overlap; consecutive same-status segments are merged.
  //
  // The "unknown" status is emitted when:
  //   - flips is empty
  //   - the range starts before any flip we have (pre-tracking)
  //   - an explicit 'unknown' flip is present
  function flipsToIntervals(flips, sinceMs, untilMs) {
    if (!(untilMs > sinceMs)) return [];
    const list = (flips || [])
      .map(f => ({ status: f.status, t: new Date(f.observedAt).getTime() }))
      .filter(f => Number.isFinite(f.t))
      .sort((a, b) => a.t - b.t);

    // Anchor: latest flip on/before sinceMs gives the starting status.
    // If none exists, the range begins as 'unknown' (we hadn't started
    // observing yet).
    let cursor = sinceMs;
    let curStatus = 'unknown';
    for (let i = 0; i < list.length; i++) {
      if (list[i].t <= sinceMs) curStatus = list[i].status;
      else break;
    }

    const out = [];
    const push = (start, end, status) => {
      if (end <= start) return;
      const last = out[out.length - 1];
      if (last && last.status === status && last.endMs === start) {
        last.endMs = end;     // merge adjacent same-status segments
      } else {
        out.push({ startMs: start, endMs: end, status });
      }
    };

    for (let i = 0; i < list.length; i++) {
      const f = list[i];
      if (f.t <= sinceMs) continue;
      if (f.t >= untilMs) break;
      push(cursor, f.t, curStatus);
      cursor = f.t;
      curStatus = f.status;
    }
    push(cursor, untilMs, curStatus);
    return out;
  }

  // ── localDayBounds(date) ─────────────────────────────────────────────
  // Returns { startMs, endMs } for the local-time day containing `date`.
  // Used for the today view and for grouping the 90-day grid by local
  // calendar day (so DST transitions don't shift the "morning" column).
  function localDayBounds(date) {
    const d = new Date(date);
    const start = new Date(d.getFullYear(), d.getMonth(), d.getDate(), 0, 0, 0, 0);
    const end = new Date(start.getTime() + 24 * 3600 * 1000);
    // Note: end may be 23h or 25h after start on DST transition days;
    // we intentionally use start + 24h so the visual scale stays constant.
    return { startMs: start.getTime(), endMs: end.getTime() };
  }

  // ── splitIntervalsByLocalDay(intervals, sinceMs, untilMs) ────────────
  // Slice a flat interval list along local-day boundaries. Returns a
  // map keyed by `YYYY-MM-DD` of that local day, value = array of
  // segments clipped to that day. Each segment keeps `{startMs, endMs,
  // status}` and has both endpoints inside the day.
  function splitIntervalsByLocalDay(intervals, sinceMs, untilMs) {
    const byDay = new Map();
    if (!intervals || intervals.length === 0) return byDay;

    // Walk day-by-day from the local day containing sinceMs forward.
    const firstDay = localDayBounds(sinceMs);
    let dayStart = firstDay.startMs;
    while (dayStart < untilMs) {
      const dayEnd = localDayBounds(new Date(dayStart + 26 * 3600 * 1000)).startMs;
      // ^ +26h then snap to local midnight handles DST cleanly.
      const key = formatLocalDateKey(new Date(dayStart));
      const dayClipStart = Math.max(dayStart, sinceMs);
      const dayClipEnd = Math.min(dayEnd, untilMs);
      const segs = [];
      for (const iv of intervals) {
        if (iv.endMs <= dayClipStart || iv.startMs >= dayClipEnd) continue;
        segs.push({
          startMs: Math.max(iv.startMs, dayClipStart),
          endMs: Math.min(iv.endMs, dayClipEnd),
          status: iv.status,
        });
      }
      byDay.set(key, { dayStartMs: dayStart, dayEndMs: dayEnd, segments: segs });
      dayStart = dayEnd;
    }
    return byDay;
  }

  function formatLocalDateKey(date) {
    const y = date.getFullYear();
    const m = String(date.getMonth() + 1).padStart(2, '0');
    const d = String(date.getDate()).padStart(2, '0');
    return `${y}-${m}-${d}`;
  }

  // ── commonOnProbabilities(intervals, target, opts) ───────────────────
  // Computes P(on | minute-of-day) using historical days from
  // `intervals`. Strategy:
  //   1. Same weekday as `target` (≥ 4 such days observed) — preferred.
  //   2. Else weekday/weekend class (≥ 4 days).
  //   3. Else all days.
  //   4. If fewer than `minDays` days of any history exist → null
  //      (caller should hide the overlay).
  //
  // Returns:
  //   {
  //     probabilities: Float32Array(SLOT_COUNT),  // 0..1 per slot
  //     basis: 'weekday' | 'weekday-class' | 'all',
  //     sampleDays: number,
  //   }
  //
  // SLOT_COUNT defaults to 48 (half-hour buckets) — fine-grained enough
  // for an overlay ribbon without becoming a histogram.
  const SLOT_COUNT = 48;
  function commonOnProbabilities(intervals, target, opts) {
    opts = opts || {};
    const minDays = opts.minDays != null ? opts.minDays : 14;
    const targetDate = new Date(target);
    const targetDow = targetDate.getDay();
    const targetIsWeekend = targetDow === 0 || targetDow === 6;

    // Determine the historical window: everything *before* the local
    // day of target, going as far back as the intervals reach.
    const todayStart = localDayBounds(targetDate).startMs;
    let earliestMs = todayStart;
    for (const iv of intervals) {
      if (iv.startMs < earliestMs) earliestMs = iv.startMs;
    }
    if (earliestMs >= todayStart) {
      return null;   // no history before today
    }
    const byDay = splitIntervalsByLocalDay(intervals, earliestMs, todayStart);

    // Collect days into three pools so we can pick the most specific one
    // that meets the sample threshold.
    const sameWeekday = [];
    const sameClass = [];
    const all = [];
    for (const dayInfo of byDay.values()) {
      const observed = dayHasObservation(dayInfo.segments);
      if (!observed) continue;   // skip days with zero observed seconds
      const d = new Date(dayInfo.dayStartMs);
      const dow = d.getDay();
      const isWeekend = dow === 0 || dow === 6;
      all.push(dayInfo);
      if (isWeekend === targetIsWeekend) sameClass.push(dayInfo);
      if (dow === targetDow) sameWeekday.push(dayInfo);
    }

    let pool, basis;
    if (sameWeekday.length >= 4) { pool = sameWeekday; basis = 'weekday'; }
    else if (sameClass.length >= 4) { pool = sameClass; basis = 'weekday-class'; }
    else if (all.length >= minDays) { pool = all; basis = 'all'; }
    else return null;

    // Accumulate observed + on seconds per slot.
    const slotSec = (24 * 3600) / SLOT_COUNT;
    const onSec = new Float64Array(SLOT_COUNT);
    const obsSec = new Float64Array(SLOT_COUNT);
    for (const dayInfo of pool) {
      for (const seg of dayInfo.segments) {
        const dayStart = dayInfo.dayStartMs;
        const startS = (seg.startMs - dayStart) / 1000;
        const endS = (seg.endMs - dayStart) / 1000;
        let s = startS;
        while (s < endS) {
          const slot = Math.min(SLOT_COUNT - 1, Math.floor(s / slotSec));
          const slotEndS = (slot + 1) * slotSec;
          const chunk = Math.min(endS, slotEndS) - s;
          if (seg.status !== 'unknown') obsSec[slot] += chunk;
          if (seg.status === 'on')      onSec[slot]  += chunk;
          s += chunk;
        }
      }
    }

    // Raw probability per slot, then a 3-tap moving average to smooth.
    const raw = new Float32Array(SLOT_COUNT);
    for (let i = 0; i < SLOT_COUNT; i++) {
      raw[i] = obsSec[i] > 0 ? onSec[i] / obsSec[i] : 0;
    }
    const smoothed = new Float32Array(SLOT_COUNT);
    for (let i = 0; i < SLOT_COUNT; i++) {
      const a = raw[(i - 1 + SLOT_COUNT) % SLOT_COUNT];
      const b = raw[i];
      const c = raw[(i + 1) % SLOT_COUNT];
      smoothed[i] = (a + b + c) / 3;
    }

    return { probabilities: smoothed, basis, sampleDays: pool.length };
  }

  function dayHasObservation(segments) {
    for (const s of segments) if (s.status !== 'unknown') return true;
    return false;
  }

  // ── basisLabel(basis, target) ─────────────────────────────────────────
  // Human label for the "Based on …" tooltip.
  function basisLabel(basis, target) {
    if (basis === 'weekday') {
      const names = ['Sundays', 'Mondays', 'Tuesdays', 'Wednesdays',
                     'Thursdays', 'Fridays', 'Saturdays'];
      return names[new Date(target).getDay()];
    }
    if (basis === 'weekday-class') {
      const dow = new Date(target).getDay();
      return (dow === 0 || dow === 6) ? 'weekends' : 'weekdays';
    }
    return 'all days';
  }

  return {
    flipsToIntervals,
    localDayBounds,
    splitIntervalsByLocalDay,
    formatLocalDateKey,
    commonOnProbabilities,
    basisLabel,
    SLOT_COUNT,
  };
}));
