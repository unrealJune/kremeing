// M3 islands — top search bar, list overlay, bottom sheet, charts.
// Aligned with NearbyStore + UptimeBucket from openapi.yaml: store fields
// limited to what the contract returns (no rating, no hours, no nextHot).

// ────────────────────────────────────────────────────────────────────────
// Top search bar — text input for ZIP/"City, ST" queries plus a status
// indicator (hot count, locating spinner, or error) below the input.
// Tapping the list icon on the right opens the StoreList overlay.
// ────────────────────────────────────────────────────────────────────────
function TopAppBar({ hotCount, scheme, onOpenList, onSearch, locating, searching, error }) {
  const [value, setValue] = React.useState('');

  let dotColor, dotShadow, label;
  if (error) {
    dotColor = scheme.tertiary;
    dotShadow = `0 0 0 4px ${scheme.tertiary}1F`;
    label = error.toString().slice(0, 60);
  } else if (searching) {
    dotColor = scheme.primary;
    dotShadow = `0 0 0 4px ${scheme.primary}1F`;
    label = 'Searching…';
  } else if (locating) {
    dotColor = scheme.outline;
    dotShadow = 'none';
    label = 'Locating…';
  } else {
    dotColor = hotCount > 0 ? scheme.primary : scheme.outline;
    dotShadow = hotCount > 0 ? `0 0 0 4px ${scheme.primary}1F` : 'none';
    label = `${hotCount} ${hotCount === 1 ? 'store' : 'stores'} hot now`;
  }

  const submit = (e) => {
    e.preventDefault();
    const q = value.trim();
    if (q.length === 0) return;
    onSearch?.(q);
    // Drop focus on mobile so the keyboard tucks away after submit.
    e.currentTarget?.querySelector('input')?.blur();
  };

  return (
    <div className="app-top-bar" style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
      <form
        onSubmit={submit}
        role="search"
        style={{
          display: 'flex', alignItems: 'center',
          background: scheme.surfaceContainerHigh,
          borderRadius: 28,
          height: 56,
          padding: '0 8px 0 16px',
          gap: 12,
          boxShadow: '0 1px 2px rgba(0,0,0,0.05), 0 4px 14px rgba(0,0,0,0.06)',
        }}
      >
        <MIcon name="search" size={22} color={scheme.onSurfaceVariant} />
        <input
          type="search"
          inputMode="search"
          autoComplete="off"
          value={value}
          onChange={(e) => setValue(e.target.value)}
          placeholder="Search ZIP or city (e.g. 98109)"
          aria-label="Search by ZIP or city"
          style={{
            flex: 1, minWidth: 0,
            background: 'transparent', border: 'none', outline: 'none',
            fontSize: 16, color: scheme.onSurface, fontFamily: 'inherit',
          }}
        />
        {value.length > 0 && (
          <button
            type="button"
            onClick={() => setValue('')}
            aria-label="Clear search"
            style={{
              width: 32, height: 32, borderRadius: '50%',
              border: 'none', background: 'transparent', cursor: 'pointer',
              display: 'flex', alignItems: 'center', justifyContent: 'center',
              color: scheme.onSurfaceVariant, padding: 0,
            }}>
            <MIcon name="close" size={18} color={scheme.onSurfaceVariant} />
          </button>
        )}
        <button
          type="button"
          onClick={onOpenList}
          aria-label="Open store list"
          style={{
            width: 40, height: 40, borderRadius: '50%',
            border: 'none', background: 'transparent', cursor: 'pointer',
            display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 0,
          }}>
          <MIcon name="list" size={22} color={scheme.onSurfaceVariant} />
        </button>
      </form>
      <div style={{
        padding: '0 16px', display: 'flex', alignItems: 'center', gap: 8,
        fontSize: 13, color: scheme.onSurfaceVariant, lineHeight: '20px',
      }}>
        <span style={{
          width: 8, height: 8, borderRadius: '50%',
          background: dotColor, flexShrink: 0,
          boxShadow: dotShadow,
        }} />
        <span style={{
          flex: 1,
          whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
        }}>{label}</span>
      </div>
    </div>
  );
}

// ────────────────────────────────────────────────────────────────────────
// Distance formatter — accepts the contract's number `distanceMiles`
// ────────────────────────────────────────────────────────────────────────
function formatDistance(miles) {
  if (miles == null || isNaN(miles)) return '';
  if (miles < 0.1) return '< 0.1 mi';
  if (miles < 10) return `${miles.toFixed(1)} mi`;
  return `${Math.round(miles)} mi`;
}

// ────────────────────────────────────────────────────────────────────────
// Store list overlay
// ────────────────────────────────────────────────────────────────────────
function StoreList({ stores, scheme, onClose, onPick }) {
  const [closing, setClosing] = React.useState(false);
  const beginClose = React.useCallback(() => {
    if (closing) return;
    setClosing(true);
    setTimeout(onClose, 200);
  }, [closing, onClose]);
  const beginPick = (id) => {
    setClosing(true);
    setTimeout(() => onPick(id), 200);
  };

  const sorted = [...stores].sort((a, b) => {
    const aHot = a.currentStatus === 'on';
    const bHot = b.currentStatus === 'on';
    if (aHot !== bHot) return aHot ? -1 : 1;
    return (a.distanceMiles ?? 99) - (b.distanceMiles ?? 99);
  });
  const hotN = stores.filter(s => s.currentStatus === 'on').length;

  return (
    <div
      className="app-overlay"
      role="dialog"
      aria-modal="true"
      aria-label="Nearby stores"
      style={{
        background: scheme.surface,
        animation: closing
          ? 'overlay-fade-out 200ms ease-in forwards'
          : 'sheet-rise 0.32s cubic-bezier(0.2, 0.9, 0.3, 1.05)',
        display: 'flex', flexDirection: 'column',
        overflow: 'hidden',
      }}
    >
      <div style={{
        padding: 'calc(env(safe-area-inset-top, 0px) + 16px) 8px 8px',
        display: 'flex', alignItems: 'center', gap: 4,
      }}>
        <button
          onClick={beginClose} aria-label="Back"
          style={{
            width: 48, height: 48, borderRadius: '50%',
            border: 'none', background: 'transparent',
            cursor: 'pointer',
            display: 'flex', alignItems: 'center', justifyContent: 'center',
          }}
        >
          <MIcon name="arrow_back" size={24} color={scheme.onSurface} />
        </button>
        <div style={{
          fontSize: 22, fontWeight: 500, color: scheme.onSurface, letterSpacing: 0,
        }}>
          Nearby
        </div>
      </div>

      <div style={{ padding: '4px 24px 16px' }}>
        <div style={{ fontSize: 14, color: scheme.onSurfaceVariant }}>
          {hotN} hot · {stores.length} stores
        </div>
      </div>

      <div style={{ flex: 1, overflow: 'auto', padding: '0 12px 24px' }}>
        {sorted.map((s) => {
          const hot = s.currentStatus === 'on';
          const since = window.relativeTime(s.lastFlippedAt);
          return (
            <button
              key={s.id}
              onClick={() => beginPick(s.id)}
              style={{
                width: '100%',
                display: 'flex', alignItems: 'center', gap: 16,
                background: 'transparent', border: 'none',
                padding: '12px 12px',
                cursor: 'pointer', textAlign: 'left',
                borderRadius: 12,
                fontFamily: 'inherit',
              }}
            >
              <div style={{
                width: 40, height: 40, borderRadius: '50%',
                flexShrink: 0,
                background: hot ? scheme.primary : scheme.secondaryContainer,
                display: 'flex', alignItems: 'center', justifyContent: 'center',
              }}>
                <MIcon
                  name={hot ? 'local_fire_department' : 'donut_small'}
                  size={20}
                  color={hot ? scheme.onPrimary : scheme.onSecondaryContainer}
                />
              </div>
              <div style={{ flex: 1, minWidth: 0 }}>
                <div style={{
                  fontSize: 16, fontWeight: 500, color: scheme.onSurface,
                  whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
                }}>
                  {window.shortName(s.name)}
                </div>
                <div style={{
                  fontSize: 13, color: hot ? scheme.primary : scheme.onSurfaceVariant,
                  marginTop: 2, fontWeight: hot ? 500 : 400,
                  whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
                }}>
                  {hot
                    ? (since ? `Hot since ${since}` : 'Hot now')
                    : (s.currentStatus === 'unknown' ? 'Status unknown' : 'Hot light off')}
                  {' · '}{formatDistance(s.distanceMiles)}
                </div>
              </div>
            </button>
          );
        })}
      </div>
    </div>
  );
}

// ────────────────────────────────────────────────────────────────────────
// Hour strip — 24 UptimeBuckets, current hour highlighted
// ────────────────────────────────────────────────────────────────────────
function HourStrip({ buckets, scheme }) {
  if (!buckets || buckets.length === 0) {
    return <div style={{ height: 28, color: scheme.onSurfaceVariant, fontSize: 13 }}>No data</div>;
  }
  const now = new Date();
  return (
    <div style={{ width: '100%' }}>
      <div style={{ display: 'flex', gap: 2, height: 28, width: '100%', alignItems: 'flex-end' }}>
        {buckets.map((b, i) => {
          const frac = b.fractionOn ?? 0;
          const observedRatio = b.totalSeconds > 0 ? (b.observedSeconds / b.totalSeconds) : 1;
          const isCurrent = new Date(b.startUtc) <= now && now < new Date(b.endUtc);
          const height = frac > 0 ? Math.max(8, 8 + frac * 20) : 6;
          const bg = observedRatio < 0.25
            ? scheme.outlineVariant
            : (frac > 0.5 ? scheme.primary : (frac > 0 ? scheme.primary + '99' : scheme.surfaceContainerHigh));
          return (
            <div key={i} style={{
              flex: 1,
              height,
              borderRadius: 3,
              background: bg,
              outline: isCurrent ? `2px solid ${scheme.onSurface}` : 'none',
              outlineOffset: 1,
              transition: 'height 200ms',
            }} />
          );
        })}
      </div>
      <div style={{
        display: 'flex', justifyContent: 'space-between', marginTop: 8,
        fontSize: 11, color: scheme.onSurfaceVariant,
      }}>
        <span>12a</span><span>6a</span><span>12p</span><span>6p</span><span>12a</span>
      </div>
    </div>
  );
}

// ────────────────────────────────────────────────────────────────────────
// Uptime chart — 90 daily UptimeBuckets, hover scrubs
// ────────────────────────────────────────────────────────────────────────
function UptimeChart({ buckets, scheme }) {
  const [hover, setHover] = React.useState(null);
  const trackRef = React.useRef(null);

  // Touch / pointer support — phones don't fire mouseenter, so we map
  // the touch X coordinate into a bar index against the chart's width.
  const indexFromClientX = React.useCallback((clientX) => {
    const el = trackRef.current;
    if (!el || !buckets || buckets.length === 0) return null;
    const rect = el.getBoundingClientRect();
    const x = Math.max(0, Math.min(rect.width, clientX - rect.left));
    return Math.min(buckets.length - 1, Math.floor((x / rect.width) * buckets.length));
  }, [buckets]);

  const onPointerMove = React.useCallback((e) => {
    setHover(indexFromClientX(e.clientX));
  }, [indexFromClientX]);

  const onPointerLeave = React.useCallback(() => setHover(null), []);

  if (!buckets || buckets.length === 0) {
    return <div style={{ height: 38, color: scheme.onSurfaceVariant, fontSize: 13 }}>No data</div>;
  }
  return (
    <div style={{ width: '100%' }}>
      <div
        ref={trackRef}
        style={{
          display: 'flex', alignItems: 'flex-end', gap: 1.5, height: 38, width: '100%',
          touchAction: 'pan-y',   // let the page still scroll vertically
        }}
        onPointerMove={onPointerMove}
        onPointerDown={onPointerMove}
        onPointerLeave={onPointerLeave}
        onPointerCancel={onPointerLeave}
      >
        {buckets.map((b, i) => {
          const ratio = b.fractionOn ?? 0;
          const observedRatio = b.totalSeconds > 0 ? (b.observedSeconds / b.totalSeconds) : 1;
          let bg;
          if (observedRatio < 0.25) bg = scheme.outlineVariant;
          else if (ratio === 0) bg = scheme.surfaceContainerHigh;
          else if (ratio < 0.1) bg = scheme.primaryContainer;
          else if (ratio < 0.25) bg = scheme.primary + '99';
          else bg = scheme.primary;
          const h = ratio === 0 ? 6 : 12 + ratio * 26;
          return (
            <div key={i}
              style={{
                flex: 1, minWidth: 0, height: h, background: bg,
                borderRadius: 2,
                pointerEvents: 'none',  // pointer events go to the track, not the bars
                opacity: hover !== null && hover !== i ? 0.45 : 1,
                transition: 'opacity 160ms',
              }}
            />
          );
        })}
      </div>
      <div style={{
        display: 'flex', justifyContent: 'space-between', marginTop: 8,
        fontSize: 11, color: scheme.onSurfaceVariant,
      }}>
        <span>{buckets.length} days ago</span>
        <span>
          {hover !== null
            ? `Day ${hover + 1} · ${Math.round((buckets[hover].fractionOn ?? 0) * 24)} hot hrs`
            : 'Today'}
        </span>
      </div>
    </div>
  );
}

// ────────────────────────────────────────────────────────────────────────
// Bottom Sheet — fetches uptime when opened, switches by tab
// ────────────────────────────────────────────────────────────────────────
function BottomSheet({ store, onClose, scheme, fetchUptimeBuckets }) {
  const [tab, setTab] = React.useState('today');
  const [hourly, setHourly] = React.useState(null);
  const [daily, setDaily] = React.useState(null);

  // ── share + notify ───────────────────────────────────────────────────
  const [shareToast, setShareToast] = React.useState(null);   // 'copied' | 'error' | null
  const [notifyState, setNotifyState] = React.useState('idle'); // 'idle' | 'subscribed' | 'denied' | 'unsupported'
  const pollIntervalRef = React.useRef(null);
  const lastSeenStatusRef = React.useRef(store.currentStatus);

  // ── swipe-to-dismiss + exit animation ────────────────────────────────
  const [closing, setClosing] = React.useState(false);
  const [dragY, setDragY] = React.useState(0);
  const dragStartRef = React.useRef(null);

  // Threshold past which a drag releases as a dismiss. ~80px feels right
  // on phone-sized sheets — far enough to be intentional, near enough to
  // not require a full-screen swipe.
  const DISMISS_PX = 80;

  const beginClose = React.useCallback(() => {
    if (closing) return;
    setClosing(true);
    // Match the sheet-fall keyframe duration in the stylesheet.
    setTimeout(onClose, 220);
  }, [closing, onClose]);

  const onHandlePointerDown = (e) => {
    if (closing) return;
    dragStartRef.current = e.clientY;
    e.currentTarget.setPointerCapture(e.pointerId);
  };
  const onHandlePointerMove = (e) => {
    if (dragStartRef.current == null) return;
    setDragY(Math.max(0, e.clientY - dragStartRef.current));
  };
  const onHandlePointerUp = (e) => {
    if (dragStartRef.current == null) return;
    const dy = Math.max(0, e.clientY - dragStartRef.current);
    dragStartRef.current = null;
    if (dy > DISMISS_PX) beginClose();
    else setDragY(0);
  };

  React.useEffect(() => {
    let cancelled = false;
    setHourly(null); setDaily(null);
    fetchUptimeBuckets(store.id, 'hour').then(b => { if (!cancelled) setHourly(b); });
    fetchUptimeBuckets(store.id, 'day').then(b => { if (!cancelled) setDaily(b); });
    return () => { cancelled = true; };
  }, [store.id, fetchUptimeBuckets]);

  // Stop polling when the sheet unmounts (close, navigate, sheet swap).
  React.useEffect(() => () => {
    if (pollIntervalRef.current) {
      clearInterval(pollIntervalRef.current);
      pollIntervalRef.current = null;
    }
  }, []);

  // ── share ─────────────────────────────────────────────────────────────
  // Build a shareable deep link. Includes lat/lng so the recipient's
  // /stores/nearby fetch will return this store regardless of where
  // they are.
  const buildShareUrl = () =>
    `${window.location.origin}/?store=${store.id}` +
    `&lat=${store.latitude.toFixed(4)}&lng=${store.longitude.toFixed(4)}`;

  const onShare = async () => {
    const url = buildShareUrl();
    const data = {
      title: `${window.shortName(store.name)} — Hot Light`,
      text: store.currentStatus === 'on'
        ? `🔥 The Hot Light is ON at ${window.shortName(store.name)}!`
        : `Track the Hot Light at ${window.shortName(store.name)}.`,
      url,
    };
    try {
      // navigator.share works on iOS Safari, Android Chrome, recent
      // desktop Safari/Edge. We feature-detect canShare too — Firefox
      // has the symbol but rejects most payloads on desktop.
      if (navigator.share && (!navigator.canShare || navigator.canShare(data))) {
        await navigator.share(data);
      } else if (navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(url);
        setShareToast('copied');
        setTimeout(() => setShareToast(null), 2200);
      } else {
        // Last-resort fallback: prompt with the URL preselected.
        window.prompt('Copy this link:', url);
      }
    } catch (err) {
      // AbortError = user cancelled the native share — silent.
      if (err && err.name !== 'AbortError') {
        setShareToast('error');
        setTimeout(() => setShareToast(null), 2200);
      }
    }
  };

  // ── notify when hot ──────────────────────────────────────────────────
  // Polls every 60s while subscribed; fires a browser notification on
  // off→on transitions. Lifetime is tied to this BottomSheet instance —
  // closing the sheet stops the polling. Cross-session persistence
  // would require a service worker + Web Push; deferred for now.
  const onNotify = async () => {
    if (notifyState === 'subscribed') {
      setNotifyState('idle');
      if (pollIntervalRef.current) {
        clearInterval(pollIntervalRef.current);
        pollIntervalRef.current = null;
      }
      return;
    }
    if (typeof Notification === 'undefined') {
      setNotifyState('unsupported');
      return;
    }
    if (Notification.permission === 'denied') {
      setNotifyState('denied');
      return;
    }
    if (Notification.permission !== 'granted') {
      const result = await Notification.requestPermission();
      if (result !== 'granted') {
        setNotifyState(result === 'denied' ? 'denied' : 'idle');
        return;
      }
    }

    setNotifyState('subscribed');
    lastSeenStatusRef.current = store.currentStatus;
    pollIntervalRef.current = setInterval(async () => {
      try {
        const dto = await window.KREMEING_API.fetchHotLight(store.id);
        if (dto.status === 'on' && lastSeenStatusRef.current !== 'on') {
          new Notification(`🔥 ${window.shortName(store.name)}`, {
            body: 'Hot doughnuts ready now',
            tag: `kremeing-${store.id}`,   // collapses repeats per store
            renotify: false,
          });
        }
        lastSeenStatusRef.current = dto.status;
      } catch (err) {
        // transient upstream blip — keep polling
      }
    }, 60_000);
  };

  const hot = store.currentStatus === 'on';
  const unknown = store.currentStatus === 'unknown';
  const since = window.relativeTime(store.lastFlippedAt);

  // Combine entrance, drag-follow, and exit. Closing wins over dragY so
  // releasing past threshold animates cleanly to off-screen.
  const sheetStyle = closing
    ? { animation: 'sheet-fall 220ms cubic-bezier(0.4, 0, 1, 1) forwards' }
    : (dragY > 0
        ? { transform: `translateY(${dragY}px)` }
        : { animation: 'sheet-rise 0.36s cubic-bezier(0.2, 0.9, 0.3, 1.05)' });

  return (
    <div
      className="app-bottom-sheet"
      role="dialog"
      aria-modal="true"
      aria-label={`${window.shortName(store.name)} details`}
      style={{
        background: scheme.surfaceContainerLow, color: scheme.onSurface,
        boxShadow: '0 -1px 3px rgba(0,0,0,0.06), 0 -8px 28px rgba(0,0,0,0.08)',
        transition: dragY === 0 && !closing ? 'transform 220ms cubic-bezier(0.2, 0.9, 0.3, 1.05)' : 'none',
        ...sheetStyle,
      }}
    >
      {/* Drag affordance — gets the pointer events so finger swipes
          dismiss the sheet. The `touch-action: none` prevents the
          browser from interpreting the drag as a page scroll. */}
      <div
        onPointerDown={onHandlePointerDown}
        onPointerMove={onHandlePointerMove}
        onPointerUp={onHandlePointerUp}
        onPointerCancel={onHandlePointerUp}
        style={{
          display: 'flex', justifyContent: 'center', padding: '12px 0 4px',
          cursor: 'grab', touchAction: 'none',
        }}
        aria-label="Drag down to dismiss"
        role="separator"
      >
        <div style={{ width: 32, height: 4, borderRadius: 2, background: scheme.outlineVariant }} />
      </div>

      <div style={{
        display: 'flex', alignItems: 'flex-start', gap: 12,
        padding: '12px 24px 12px',
      }}>
        <div style={{ flex: 1, minWidth: 0 }}>
          <div style={{
            fontSize: 22, fontWeight: 500, color: scheme.onSurface, letterSpacing: 0,
            whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
          }}>
            {window.shortName(store.name)}
          </div>
          <div style={{
            fontSize: 14, color: scheme.onSurfaceVariant, marginTop: 4,
            display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap',
          }}>
            <span>{formatDistance(store.distanceMiles)}</span>
            {store.address && (
              <>
                <span style={{ color: scheme.outlineVariant }}>·</span>
                <span style={{
                  whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
                  maxWidth: 240,
                }}>{store.address}</span>
              </>
            )}
          </div>
        </div>
        <button
          onClick={beginClose} aria-label="Close"
          style={{
            width: 40, height: 40, borderRadius: '50%',
            border: 'none', background: 'transparent',
            cursor: 'pointer', flexShrink: 0,
            display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 0,
          }}
        >
          <MIcon name="close" size={20} color={scheme.onSurfaceVariant} />
        </button>
      </div>

      {hot ? (
        <div style={{
          margin: '0 24px 16px', padding: '12px 16px',
          background: scheme.primaryContainer, color: scheme.onPrimaryContainer,
          borderRadius: 16,
          display: 'flex', alignItems: 'center', gap: 12,
        }}>
          <span
            className="kk-glow-dot"
            style={{
              width: 10, height: 10, borderRadius: '50%',
              background: scheme.primary,
              animation: 'dial-glow 2.6s ease-in-out infinite',
              boxShadow: `0 0 0 4px ${scheme.primary}33`,
              flexShrink: 0,
            }}
          />
          <div style={{ flex: 1, minWidth: 0 }}>
            <div style={{ fontSize: 14, fontWeight: 600 }}>Hot Light On</div>
            {since && (
              <div style={{ fontSize: 12, opacity: 0.85, marginTop: 2 }}>
                Glazing since {since}
              </div>
            )}
          </div>
        </div>
      ) : (
        <div style={{
          margin: '0 24px 16px', padding: '12px 16px',
          background: scheme.surfaceContainerHigh, color: scheme.onSurfaceVariant,
          borderRadius: 16,
          display: 'flex', alignItems: 'center', gap: 12,
        }}>
          <MIcon name="schedule" size={18} color={scheme.onSurfaceVariant} />
          <div style={{ fontSize: 14 }}>
            {unknown ? 'Status unknown' : (since ? `Hot light off · last flipped ${since}` : 'Hot light off')}
          </div>
        </div>
      )}

      <div style={{ display: 'flex', gap: 8, padding: '0 16px 18px' }}>
        <button style={{
          flex: 1, height: 48, borderRadius: 24, border: 'none',
          background: scheme.primary, color: scheme.onPrimary,
          cursor: 'pointer',
          display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 8,
          fontSize: 14, fontWeight: 500, fontFamily: 'inherit',
        }}
          onClick={() => {
            const url = `https://www.google.com/maps/dir/?api=1&destination=${store.latitude},${store.longitude}`;
            window.open(url, '_blank', 'noopener');
          }}>
          <MIcon name="directions" size={18} color={scheme.onPrimary} />
          Directions
        </button>
        <button
          onClick={onNotify}
          aria-label={notifyState === 'subscribed' ? 'Stop notifying' : 'Notify when hot'}
          aria-pressed={notifyState === 'subscribed'}
          title={
            notifyState === 'subscribed' ? 'Notifying you when the light flips on'
            : notifyState === 'denied'   ? 'Notifications blocked — enable in browser settings'
            : notifyState === 'unsupported' ? 'Notifications not supported in this browser'
            : 'Get a notification when the Hot Light turns on'
          }
          disabled={notifyState === 'denied' || notifyState === 'unsupported'}
          style={{
            width: 48, height: 48, borderRadius: 24, border: 'none',
            background: notifyState === 'subscribed' ? scheme.primary : scheme.secondaryContainer,
            color: notifyState === 'subscribed' ? scheme.onPrimary : scheme.onSecondaryContainer,
            cursor: 'pointer',
            display: 'flex', alignItems: 'center', justifyContent: 'center',
            opacity: (notifyState === 'denied' || notifyState === 'unsupported') ? 0.5 : 1,
          }}>
          <MIcon
            name="notifications_active"
            size={20}
            color={notifyState === 'subscribed' ? scheme.onPrimary : scheme.onSecondaryContainer}
          />
        </button>
        <button
          onClick={onShare}
          aria-label="Share"
          title={shareToast === 'copied' ? 'Link copied!' : 'Share a link to this store'}
          style={{
            width: 48, height: 48, borderRadius: 24,
            border: `1px solid ${scheme.outline}`,
            background: shareToast === 'copied' ? scheme.secondaryContainer : 'transparent',
            color: scheme.onSurface, cursor: 'pointer',
            display: 'flex', alignItems: 'center', justifyContent: 'center',
            transition: 'background 200ms',
          }}>
          <MIcon
            name={shareToast === 'copied' ? 'check' : 'ios_share'}
            size={20}
            color={shareToast === 'copied' ? scheme.onSecondaryContainer : scheme.onSurface}
          />
        </button>
      </div>

      <div style={{ height: 1, background: scheme.outlineVariant, margin: '0 24px' }} />

      <div style={{ padding: '16px 24px 4px' }}>
        <div style={{
          fontSize: 11, fontWeight: 600, color: scheme.onSurfaceVariant,
          letterSpacing: 0.5, textTransform: 'uppercase',
          marginBottom: 12,
        }}>
          Hot light history
        </div>

        <div style={{
          display: 'inline-flex',
          border: `1px solid ${scheme.outline}`, borderRadius: 9999,
          marginBottom: 16, overflow: 'hidden',
        }}>
          {[
            { id: 'today', label: 'Today' },
            { id: 'history', label: '90 days' },
          ].map((t, i) => {
            const active = tab === t.id;
            return (
              <button
                key={t.id}
                onClick={() => setTab(t.id)}
                style={{
                  border: 'none',
                  borderLeft: i > 0 ? `1px solid ${scheme.outline}` : 'none',
                  background: active ? scheme.secondaryContainer : 'transparent',
                  color: active ? scheme.onSecondaryContainer : scheme.onSurface,
                  padding: '8px 18px',
                  fontSize: 14, fontWeight: 500,
                  cursor: 'pointer', fontFamily: 'inherit',
                  display: 'inline-flex', alignItems: 'center', gap: 6,
                }}
              >
                {active && <MIcon name="check" size={16} color={scheme.onSecondaryContainer} />}
                {t.label}
              </button>
            );
          })}
        </div>

        <div style={{ minHeight: 60, marginBottom: 12 }}>
          {tab === 'today'
            ? <HourStrip buckets={hourly} scheme={scheme} />
            : <UptimeChart buckets={daily} scheme={scheme} />}
        </div>
      </div>
    </div>
  );
}

Object.assign(window, {
  TopAppBar, BottomSheet, StoreList, UptimeChart, HourStrip,
});
