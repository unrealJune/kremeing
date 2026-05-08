// Pin HTML factories for Leaflet `L.divIcon`. The pin styling matches the M3
// design: hot pins are filled-primary circles with a white flame and a soft
// pulsing halo; cold pins are smaller secondary-container dots.
//
// Returned object: { html, className, iconSize, iconAnchor } — fed straight
// into L.divIcon. Click handling is wired by the map layer via marker.on.

const FLAME_SVG = `
<svg viewBox="0 0 24 24" width="100%" height="100%" aria-hidden="true">
  <path d="M12 2c1 4 5 5 5 10a5 5 0 11-10 0c0-2 1-3 1-5 0 0 2 1 2 4 1-3 1-5 2-9z" fill="currentColor"/>
</svg>
`;

function pinIcon(store, scheme, selected) {
  const hot = store.currentStatus === 'on';
  const unknown = store.currentStatus === 'unknown';
  const baseSize = hot ? (selected ? 44 : 36) : (selected ? 22 : 18);
  const box = baseSize + 16; // includes halo bleed

  const haloHtml = hot ? `
    <span class="kk-pin-halo" style="
      width:${baseSize * 1.6}px; height:${baseSize * 1.6}px;
      background: radial-gradient(circle, ${scheme.primary}33 0%, ${scheme.primary}00 65%);
    "></span>
  ` : '';

  const dotColor = unknown ? scheme.outline : scheme.secondary;

  const inner = hot
    ? `<span class="kk-pin-flame" style="color:${scheme.onPrimary};
         width:${Math.round(baseSize * 0.55)}px;
         height:${Math.round(baseSize * 0.55)}px;">${FLAME_SVG}</span>`
    : `<span class="kk-pin-dot" style="
         width:${baseSize * 0.4}px; height:${baseSize * 0.4}px;
         background:${dotColor};"></span>`;

  const bg = hot ? scheme.primary : (unknown ? scheme.surfaceContainerHigh : scheme.secondaryContainer);
  const ring = hot ? 'none' : `2px solid ${scheme.surface}`;
  const shadow = hot
    ? `0 4px 12px ${scheme.primary}55, 0 1px 2px rgba(0,0,0,0.18)`
    : `0 1px 3px rgba(0,0,0,0.20)`;

  const html = `
    <div class="kk-pin ${hot ? 'kk-pin--hot' : 'kk-pin--cold'} ${selected ? 'kk-pin--selected' : ''}"
         style="width:${box}px; height:${box}px;">
      ${haloHtml}
      <div class="kk-pin-core" style="
        width:${baseSize}px; height:${baseSize}px;
        background:${bg};
        border:${ring};
        box-shadow:${shadow};
        transform: translate(-50%, -50%) ${selected ? 'scale(1.04)' : 'scale(1)'};
      ">${inner}</div>
    </div>
  `;

  return L.divIcon({
    html,
    className: 'kk-pin-icon', // strips Leaflet's default white-square background
    iconSize: [box, box],
    iconAnchor: [box / 2, box / 2],
  });
}

window.pinIcon = pinIcon;
