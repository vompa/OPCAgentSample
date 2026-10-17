// Symbole als SVG-Pfade (24 x 24, Strich). Jeder Eintrag ist eine Liste von [Tag, Attribute].
export type IconPart = readonly [
  tag: 'path' | 'rect' | 'circle',
  attributes: Record<string, string | number>,
];

export const ICONS = {
  thermometer: [['path', { d: 'M14 14.8V4a2 2 0 10-4 0v10.8a4 4 0 104 0z' }]],
  drop: [['path', { d: 'M12 3s6 6.5 6 11a6 6 0 01-12 0c0-4.5 6-11 6-11z' }]],
  chevron: [['path', { d: 'M9 6l6 6-6 6' }]],
  star: [['path', { d: 'M12 3l2.7 5.6 6.1.9-4.4 4.3 1 6.1L12 17l-5.4 2.9 1-6.1-4.4-4.3 6.1-.9z' }]],
  pencil: [
    ['path', { d: 'M4 20h4L19 9l-4-4L4 16z' }],
    ['path', { d: 'M13 7l4 4' }],
  ],
  pin: [
    ['path', { d: 'M9 3h6l-1 6 4 4H6l4-4z' }],
    ['path', { d: 'M12 13v8' }],
  ],
  mic: [
    ['rect', { x: 9, y: 3, width: 6, height: 11, rx: 3 }],
    ['path', { d: 'M5 11a7 7 0 0014 0M12 18v3' }],
  ],
  inbox: [
    ['path', { d: 'M3 13h5l2 3h4l2-3h5' }],
    ['path', { d: 'M5 5h14l2 8v6H3v-6z' }],
  ],
  bell: [
    ['path', { d: 'M6 8a6 6 0 0112 0c0 7 3 8 3 8H3s3-1 3-8' }],
    ['path', { d: 'M10 20a2 2 0 004 0' }],
  ],
  home: [
    ['path', { d: 'M3 11l9-7 9 7v9H3z' }],
    ['path', { d: 'M9 20v-6h6v6' }],
  ],
  chat: [['path', { d: 'M4 5h16v11H9l-5 4z' }]],
  close: [['path', { d: 'M6 6l12 12M18 6L6 18' }]],
  plus: [['path', { d: 'M12 5v14M5 12h14' }]],
  history: [
    ['path', { d: 'M4 12a8 8 0 108-8 8 8 0 00-6 2.7L4 9' }],
    ['path', { d: 'M4 4v5h5M12 8v4l3 2' }],
  ],
  check: [['path', { d: 'M5 12l5 5L20 7' }]],
  send: [['path', { d: 'M12 19V5M5 12l7-7 7 7' }]],
  mcp: [
    ['rect', { x: 4, y: 4, width: 16, height: 16, rx: 2 }],
    ['path', { d: 'M9 9h6v6H9z' }],
  ],
  arrow: [['path', { d: 'M5 12h14M13 6l6 6-6 6' }]],
  info: [
    ['circle', { cx: 12, cy: 12, r: 9 }],
    ['path', { d: 'M12 11v5M12 8h.01' }],
  ],
  shield: [
    ['path', { d: 'M12 3l7 3v6c0 4.5-3 7.5-7 9-4-1.5-7-4.5-7-9V6z' }],
    ['path', { d: 'M9 12l2.2 2.2L15.5 10' }],
  ],
  github: [
    ['circle', { cx: 6, cy: 5, r: 2.2 }],
    ['circle', { cx: 6, cy: 19, r: 2.2 }],
    ['circle', { cx: 18, cy: 9, r: 2.2 }],
    ['path', { d: 'M6 7.2v9.6M18 11.2c0 4-6 3-11 6' }],
  ],
  menu: [['path', { d: 'M4 7h16M4 12h16M4 17h10' }]],
  compose: [
    ['path', { d: 'M11 4H6a2 2 0 00-2 2v12a2 2 0 002 2h12a2 2 0 002-2v-5' }],
    ['path', { d: 'M18.5 3.5a1.6 1.6 0 012.3 2.3L12 14.6 8.5 15.5l.9-3.5z' }],
  ],
  chart: [
    ['path', { d: 'M4 4v16h16' }],
    ['path', { d: 'M8 15l3-4 3 2 4-6' }],
  ],
} as const satisfies Record<string, readonly IconPart[]>;

export type IconName = keyof typeof ICONS;
