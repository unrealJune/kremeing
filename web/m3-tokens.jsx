// Material 3 design tokens, themed for Krispy Kreme.
// Light scheme is what ships; dark is kept for future toggle.

const M3_LIGHT = {
  name: 'light',
  primary: '#C73E1D',
  onPrimary: '#FFFFFF',
  primaryContainer: '#FFDAD1',
  onPrimaryContainer: '#410001',
  secondary: '#1F6B3F',
  onSecondary: '#FFFFFF',
  secondaryContainer: '#A4F4B5',
  onSecondaryContainer: '#002112',
  tertiary: '#9C4221',
  onTertiary: '#FFFFFF',
  tertiaryContainer: '#FFDBCD',
  onTertiaryContainer: '#3A0F00',
  surface: '#FBFAF8',
  surfaceDim: '#DCDADA',
  surfaceContainerLow: '#F4F2F0',
  surfaceContainer: '#EEECEA',
  surfaceContainerHigh: '#E8E6E3',
  surfaceContainerHighest: '#E1DFDC',
  onSurface: '#1B1B1B',
  onSurfaceVariant: '#48474A',
  outline: '#797979',
  outlineVariant: '#CACACA',
  mapLand: '#F4F2EE',
  mapPark: '#CCDFC3',
  mapParkDeep: '#A6C898',
  mapWater: '#BBD8E0',
  mapWaterShimmer: '#88B4C0',
  mapHighway: '#FFFFFF',
  mapHighwayCasing: '#E2DEDA',
  mapRoad: '#FFFFFF',
  mapRoadCasing: '#E5E1DD',
  mapTinyRoad: '#CFCBC6',
  mapBlock: '#E2DFDA',
  mapLabel: '#8A8782',
  scrim: 'rgba(0, 0, 0, 0.32)',
};

const M3_DARK = {
  name: 'dark',
  primary: '#FFB4A2',
  onPrimary: '#5C1900',
  primaryContainer: '#842A0E',
  onPrimaryContainer: '#FFDAD1',
  secondary: '#88D89A',
  onSecondary: '#003920',
  secondaryContainer: '#005230',
  onSecondaryContainer: '#A4F4B5',
  tertiary: '#F4C76C',
  onTertiary: '#412D00',
  tertiaryContainer: '#5D4200',
  onTertiaryContainer: '#FFDFA8',
  surface: '#1A120D',
  surfaceDim: '#1A120D',
  surfaceContainerLow: '#221913',
  surfaceContainer: '#2B1F18',
  surfaceContainerHigh: '#36281F',
  surfaceContainerHighest: '#41322A',
  onSurface: '#F1E0D2',
  onSurfaceVariant: '#D8C2AA',
  outline: '#A08B75',
  outlineVariant: '#54453A',
  mapLand: '#1F1714',
  mapPark: '#243828',
  mapParkDeep: '#365F40',
  mapWater: '#0F2530',
  mapWaterShimmer: '#3D6478',
  mapHighway: '#5A4E68',
  mapHighwayCasing: '#0E0808',
  mapRoad: '#3D3128',
  mapRoadCasing: '#0E0808',
  mapTinyRoad: '#2D241D',
  mapBlock: '#2D241D',
  mapLabel: '#7A6F58',
  scrim: 'rgba(0, 0, 0, 0.45)',
};

const M3_SCHEMES = { light: M3_LIGHT, dark: M3_DARK };

// Inline SVG icon set in Material Symbols rounded style — avoids font-ligature
// flakiness and lets the app render before the icon font has loaded.
const M3_ICONS = {
  search: <><circle cx="11" cy="11" r="6" stroke="currentColor" strokeWidth="2" fill="none"/><path d="M16 16l4 4" stroke="currentColor" strokeWidth="2" strokeLinecap="round"/></>,
  close: <path d="M6 6l12 12M18 6L6 18" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" fill="none"/>,
  check: <path d="M5 12.5l4.5 4.5L19 7.5" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round" fill="none"/>,
  star: <path d="M12 2.5l2.9 6.5 7.1.7-5.4 4.8 1.6 7-6.2-3.7-6.2 3.7 1.6-7L2 9.7l7.1-.7z" fill="currentColor"/>,
  schedule: <g><circle cx="12" cy="12" r="9" stroke="currentColor" strokeWidth="2" fill="none"/><path d="M12 7v5.3l3.5 2.2" stroke="currentColor" strokeWidth="2" strokeLinecap="round" fill="none"/></g>,
  directions: <g><path d="M12 2.5L21.5 12 12 21.5 2.5 12z" stroke="currentColor" strokeWidth="2" fill="none" strokeLinejoin="round"/><path d="M9 14v-2.5h4V9.5l3.5 3-3.5 3v-2H9z" fill="currentColor"/></g>,
  notifications_active: <g><path d="M5 8a7 7 0 0114 0c0 6 2.5 8 2.5 8h-19S5 14 5 8z" stroke="currentColor" strokeWidth="2" strokeLinejoin="round" fill="none"/><path d="M10 20.5a2 2 0 004 0" stroke="currentColor" strokeWidth="2" strokeLinecap="round" fill="none"/></g>,
  ios_share: <g><path d="M12 3v13M7 8l5-5 5 5" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" fill="none"/><path d="M5 13v6.5a2 2 0 002 2h10a2 2 0 002-2V13" stroke="currentColor" strokeWidth="2" strokeLinecap="round" fill="none"/></g>,
  local_fire_department: <path d="M12 2c1 4 5 5 5 10a5 5 0 11-10 0c0-2 1-3 1-5 0 0 2 1 2 4 1-3 1-5 2-9z" fill="currentColor"/>,
  donut_small: <g><circle cx="12" cy="12" r="9" stroke="currentColor" strokeWidth="2.2" fill="none"/><circle cx="12" cy="12" r="3.5" stroke="currentColor" strokeWidth="2.2" fill="none"/></g>,
  phone: <path d="M5 4h3l2 5-2.5 1.5a11 11 0 005 5L14 13l5 2v3a2 2 0 01-2 2A14 14 0 013 6a2 2 0 012-2z" fill="currentColor"/>,
  arrow_back: <path d="M11 5l-7 7 7 7M4 12h16" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round" fill="none"/>,
  list: <path d="M4 7h2M9 7h11M4 12h2M9 12h11M4 17h2M9 17h11" stroke="currentColor" strokeWidth="2" strokeLinecap="round" fill="none"/>,
};

function MIcon({ name, size = 24, color = 'currentColor', style = {} }) {
  const inner = M3_ICONS[name];
  if (!inner) return null;
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" aria-hidden="true"
      style={{ color, display: 'inline-block', flexShrink: 0, ...style }}>
      {inner}
    </svg>
  );
}

Object.assign(window, { M3_SCHEMES, MIcon });
