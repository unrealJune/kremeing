// Real, pannable map. Leaflet + CartoDB Positron tiles — clean light basemap,
// no API key, free for general use (attribution rendered by Leaflet).
//
// Stores are added as L.markers using the `pinIcon` factory from pins.jsx.
// Clicking a marker invokes `onSelect(store.id)`. The map view animates to
// the selected store. The user's location is rendered as a separate marker.

function MapView({ stores, scheme, selected, onSelect, center, userPos }) {
  const containerRef = React.useRef(null);
  const mapRef = React.useRef(null);
  const markersRef = React.useRef(new Map()); // id -> { marker, hot, selected }
  const userMarkerRef = React.useRef(null);

  // ── init Leaflet map once ─────────────────────────────────────────────
  React.useEffect(() => {
    if (mapRef.current || !containerRef.current) return;

    const map = L.map(containerRef.current, {
      center: [center.lat, center.lng],
      zoom: 13,
      minZoom: 3,
      maxZoom: 19,
      zoomControl: false, // we may add our own; default is in top-left corner
      attributionControl: true,
      worldCopyJump: true,
    });

    L.tileLayer(
      'https://{s}.basemaps.cartocdn.com/light_all/{z}/{x}/{y}{r}.png',
      {
        attribution:
          '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> &copy; <a href="https://carto.com/attributions">CARTO</a>',
        subdomains: 'abcd',
        maxZoom: 19,
      }
    ).addTo(map);

    L.control.zoom({ position: 'bottomright' }).addTo(map);

    mapRef.current = map;

    return () => {
      map.remove();
      mapRef.current = null;
      markersRef.current.clear();
      userMarkerRef.current = null;
    };
  }, []);

  // ── store markers: rebuild whenever stores or selected change ────────
  React.useEffect(() => {
    const map = mapRef.current;
    if (!map) return;

    // Remove markers for stores no longer in the list
    const incomingIds = new Set(stores.map(s => s.id));
    for (const [id, entry] of markersRef.current) {
      if (!incomingIds.has(id)) {
        entry.marker.remove();
        markersRef.current.delete(id);
      }
    }

    for (const store of stores) {
      const isSelected = store.id === selected;
      const existing = markersRef.current.get(store.id);

      if (existing) {
        if (existing.status !== store.currentStatus || existing.selected !== isSelected) {
          existing.marker.setIcon(window.pinIcon(store, scheme, isSelected));
          existing.status = store.currentStatus;
          existing.selected = isSelected;
        }
        existing.marker.setLatLng([store.latitude, store.longitude]);
      } else {
        const marker = L.marker([store.latitude, store.longitude], {
          icon: window.pinIcon(store, scheme, isSelected),
          riseOnHover: true,
          keyboard: true,
          alt: store.name,
        });
        marker.on('click', () => onSelect(store.id));
        marker.addTo(map);
        markersRef.current.set(store.id, {
          marker,
          status: store.currentStatus,
          selected: isSelected,
        });
      }
    }
  }, [stores, scheme, selected, onSelect]);

  // ── pan to selected store ─────────────────────────────────────────────
  React.useEffect(() => {
    const map = mapRef.current;
    if (!map || selected == null) return;
    const store = stores.find(s => s.id === selected);
    if (!store) return;
    map.panTo([store.latitude, store.longitude], { animate: true, duration: 0.6 });
  }, [selected, stores]);

  // ── recenter when the center prop moves (e.g. geolocation resolves) ──
  React.useEffect(() => {
    const map = mapRef.current;
    if (!map) return;
    map.panTo([center.lat, center.lng], { animate: true, duration: 0.6 });
  }, [center.lat, center.lng]);

  // ── user-location marker ──────────────────────────────────────────────
  React.useEffect(() => {
    const map = mapRef.current;
    if (!map) return;
    if (userMarkerRef.current) {
      userMarkerRef.current.remove();
      userMarkerRef.current = null;
    }
    if (!userPos) return;

    const html = `
      <div class="kk-userdot">
        <span class="kk-userdot-pulse"></span>
        <span class="kk-userdot-core" style="border-color:${scheme.surface};"></span>
      </div>
    `;
    const icon = L.divIcon({
      html,
      className: 'kk-userdot-icon',
      iconSize: [38, 38],
      iconAnchor: [19, 19],
    });
    const m = L.marker([userPos.lat, userPos.lng], { icon, interactive: false, keyboard: false });
    m.addTo(map);
    userMarkerRef.current = m;
  }, [userPos, scheme]);

  return (
    <div
      ref={containerRef}
      style={{ position: 'absolute', inset: 0, background: scheme.mapLand }}
      aria-label="Map of nearby Krispy Kreme stores"
    />
  );
}

window.MapView = MapView;
