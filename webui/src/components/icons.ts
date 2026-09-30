import { h, type FunctionalComponent } from 'vue'

type Path = string

function svg(paths: Path[], filled = false): FunctionalComponent {
  return () =>
    h(
      'svg',
      {
        xmlns: 'http://www.w3.org/2000/svg',
        viewBox: '0 0 24 24',
        fill: filled ? 'currentColor' : 'none',
        stroke: filled ? 'none' : 'currentColor',
        'stroke-width': '2',
        'stroke-linecap': 'round',
        'stroke-linejoin': 'round',
        'aria-hidden': 'true',
      },
      paths.map((d) => h('path', { d })),
    )
}

/** Brand mark: cat head with pointy ears. */
export const IconCatHead = svg(
  [
    'M12 21.6C6.8 21.6 3.6 18.3 3.6 14c0-1.8.6-3.4 1.6-4.7L4.4 3.7c-.1-.6.5-1.1 1-.8l5 3.5c.5-.1 1-.2 1.6-.2s1.1.1 1.6.2l5-3.5c.5-.3 1.1.2 1 .8l-.8 5.6c1 1.3 1.6 2.9 1.6 4.7 0 4.3-3.2 7.6-8.4 7.6Z',
  ],
  true,
)

export const IconDashboard = svg([
  'M3 3h7v9H3z',
  'M14 3h7v5h-7z',
  'M14 12h7v9h-7z',
  'M3 16h7v5H3z',
])

export const IconRoute = svg([
  'M9 20H4a2 2 0 0 1-2-2v-4a2 2 0 0 1 2-2h16a2 2 0 0 0 2-2V6a2 2 0 0 0-2-2h-5',
  'M6 8 3 5l3-3',
  'M18 16l3 3-3 3',
])

export const IconService = svg([
  'M21 8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16Z',
  'm3.3 7 8.7 5 8.7-5',
  'M12 22V12',
])

export const IconExtension = svg([
  'M14 7V4a1 1 0 0 0-1-1h-2a1 1 0 0 0-1 1v3H7a1 1 0 0 0-1 1v2.268a2 2 0 1 0 0 3.464V17a1 1 0 0 0 1 1h3v3a1 1 0 0 0 1 1h2a1 1 0 0 0 1-1v-3h3a1 1 0 0 0 1-1v-2.268a2 2 0 1 1 0-3.464V8a1 1 0 0 0-1-1Z',
])

export const IconSettings = svg([
  'M4 21v-7',
  'M4 10V3',
  'M12 21v-9',
  'M12 8V3',
  'M20 21v-5',
  'M20 12V3',
  'M2 14h4',
  'M10 8h4',
  'M18 16h4',
])

export const IconConnect = svg([
  'M9 17H7A5 5 0 0 1 7 7h2',
  'M15 7h2a5 5 0 1 1 0 10h-2',
  'M8 12h8',
])

export const IconSun = svg([
  'M12 16a4 4 0 1 0 0-8 4 4 0 0 0 0 8Z',
  'M12 2v2',
  'M12 20v2',
  'm4.93 4.93 1.41 1.41',
  'm17.66 17.66 1.41 1.41',
  'M2 12h2',
  'M20 12h2',
  'm6.34 17.66-1.41 1.41',
  'm19.07 4.93-1.41 1.41',
])

export const IconMoon = svg([
  'M12 3a6 6 0 0 0 9 9 9 9 0 1 1-9-9Z',
])

export const IconSystem = svg([
  'M2 5a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2Z',
  'M8 21h8',
  'M12 17v4',
])

export const IconGlobe = svg([
  'M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20Z',
  'M2 12h20',
  'M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10Z',
])

export const IconDots = svg([
  'M5 12h.01',
  'M12 12h.01',
  'M19 12h.01',
])

export const IconMenu = svg([
  'M4 6h16',
  'M4 12h16',
  'M4 18h16',
])

export const IconCollapse = svg([
  'm11 17-5-5 5-5',
  'm18 17-5-5 5-5',
])

export const IconExpand = svg([
  'm6 17 5-5-5-5',
  'm13 17 5-5-5-5',
])
