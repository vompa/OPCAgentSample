// Gemeinsame Testdaten im Format der API.
import type {
  AnswerCard,
  ChatSessionPackage,
  FavoritesResponse,
  MeResponse,
  ReportsSummary,
  ToolCall,
} from '@/api/types';

export const ME: MeResponse = {
  role: {
    id: 'shift_lead',
    title: 'Schichtleiter',
    label: 'Schichtleiter',
    scope: 'Halle A',
    scopeDescription: '',
    devices: '',
    rights: ['Lesen'],
    messageFilter: 'alle',
    feedFilterText: 'alle Meldungen',
  },
  user: {
    id: 'u1',
    displayName: 'Anna Berger',
    initials: 'AB',
    firstName: 'Anna',
    title: 'Schichtleiterin',
    mobileTitle: 'Schichtleiterin Halle A',
    workTime: '06:00-14:00',
    notification: 'Push',
  },
  roles: [{ id: 'shift_lead', label: 'Schichtleiter', shortLabel: 'Schicht' }],
  clock: {
    utcNow: '2026-10-15T07:42:00Z',
    localText: 'Do 15.10., 09:42',
    localTime: '09:42',
    greeting: 'Guten Morgen',
  },
  status: {
    opcConnected: true,
    mcpToolCount: 17,
    area: 'Halle A',
    shift: 'Frühschicht',
    scopeText: 'Halle A · Frühschicht',
    historyLoading: false,
    dataWarning: null,
  },
  counters: { inboxOpen: 2, inboxHasUrgent: true, feedCount: 0, latestFeed: null },
};

export const CARD: AnswerCard = {
  headline: 'RT-01 läuft unter Soll.',
  todo: ['Gewebe prüfen'],
  table: {
    columns: ['Thema', 'Wert', 'Bewertung'],
    rows: [{ cells: ['Presskraft', '812 kN'], rating: 'warn' }],
  },
  chart: {
    title: 'Ausschuss',
    max: 10,
    unit: ' %',
    decimals: 1,
    bars: [{ label: 'RT-01', value: 2.5, rating: 'crit' }],
  },
  why: ['Gewebe verschlissen.'],
  dataAsOf: '09:42',
  sources: ['OPC HistoryRead'],
  prompt: 'Schichtübergabe',
  form: null,
  notice: null,
};

export const TOOL_CALLS: ToolCall[] = [
  {
    tool: 'get_station_status',
    parameters: { scope: 'Halle A' },
    durationMs: 41,
    source: 'mcp',
    dataSource: 'OPC UA',
    dataAsOf: '09:42',
    outcome: 'ok',
  },
  {
    tool: 'get_overproduction',
    parameters: {},
    durationMs: 3,
    source: 'mcp',
    dataSource: 'Rolle',
    outcome: 'denied',
  },
];

export const FAVORITES: FavoritesResponse = {
  max: 3,
  maxShortTitleLength: 22,
  items: [
    {
      shortTitle: 'Übergabe',
      question: 'Was ist bei der Schichtübergabe wichtig?',
      isDefault: true,
    },
    { shortTitle: 'Eigener', question: 'Wie läuft RT-02?', isDefault: false },
  ],
  defaults: [{ shortTitle: 'Übergabe', question: 'Was ist bei der Schichtübergabe wichtig?' }],
};

/** Verlauf einer Sitzung; Serverzeiten liegen bewusst weit weg vom Datenstand der Simulationsuhr. */
export function sessionPackage(dataAsOfUtc = '2026-10-14T13:30:00Z'): ChatSessionPackage {
  return {
    session: {
      id: 's1',
      role: 'shift_lead',
      startedAtUtc: '2030-01-01T00:00:00Z',
      lastActivityUtc: '2030-01-01T00:05:00Z',
      title: 'Schichtübergabe',
      turnCount: 1,
    },
    turns: [
      {
        sequence: 1,
        askedAtUtc: '2030-01-01T00:01:00Z',
        question: 'Schichtübergabe?',
        answerCard: CARD,
        toolCalls: TOOL_CALLS,
        dataAsOfUtc,
        inputTokens: 1,
        outputTokens: 1,
      },
    ],
  };
}

/** Zusammenfassung der Reports mit Fragen, Tokens, Antwortzeiten und Werkzeug-Aufrufen. */
export const REPORTS_SUMMARY: ReportsSummary = {
  period: {
    generatedAtUtc: '2026-10-15T07:42:30Z',
    firstQuestionAtUtc: '2026-10-14T09:00:00Z',
    serverStartedAtUtc: '2026-10-15T05:00:00Z',
    seriesHours: 24,
  },
  questions: { total: 12, today: 5, last24Hours: 9, answeredByClaude: 4 },
  tokens: { input: 40_000, output: 8_000, cacheRead: 120_000, cacheWrite: 10_000, total: 178_000 },
  cost: {
    estimatedUsd: 0.0123,
    cacheHitRate: 0.75,
    prices: {
      inputPerMillion: 0.1,
      outputPerMillion: 0.5,
      cacheReadPerMillion: 0.01,
      cacheWritePerMillion: 0.125,
    },
  },
  latency: { measured: 12, averageMs: 2400, p50Ms: 1800, p95Ms: 6200 },
  latencyHistogram: [
    { fromMs: 0, toMs: 250, count: 0 },
    { fromMs: 250, toMs: 500, count: 1 },
    { fromMs: 500, toMs: 1000, count: 2 },
    { fromMs: 1000, toMs: 2000, count: 5 },
    { fromMs: 2000, toMs: 3000, count: 2 },
    { fromMs: 3000, toMs: 5000, count: 1 },
    { fromMs: 5000, toMs: 8000, count: 1 },
    { fromMs: 8000, toMs: 12000, count: 0 },
    { fromMs: 12000, toMs: 20000, count: 0 },
    { fromMs: 20000, toMs: 30000, count: 0 },
    { fromMs: 30000, toMs: null, count: 0 },
  ],
  timeSeries: {
    startUtc: '2026-10-14T08:00:00Z',
    intervalMinutes: 60,
    questions: Array.from({ length: 24 }, (_, i) => (i % 5 === 0 ? 2 : i % 3 === 0 ? 1 : 0)),
    inputTokens: Array.from({ length: 24 }, (_, i) => (i % 5 === 0 ? 4000 : 0)),
    outputTokens: Array.from({ length: 24 }, (_, i) => (i % 5 === 0 ? 900 : 0)),
  },
  tools: [
    {
      tool: 'get_hall_overview',
      role: 'shift_lead',
      outcome: 'ok',
      calls: 9,
      averageMs: 42,
      p95Ms: 80,
    },
    { tool: 'get_kpis', role: 'hall_lead', outcome: 'ok', calls: 4, averageMs: 120, p95Ms: 300 },
    { tool: 'get_kpis', role: 'hall_lead', outcome: 'denied', calls: 1, averageMs: 2, p95Ms: 2 },
  ],
  ingestion: {
    lagSeconds: 12,
    simulationClockUtc: '2026-10-15T07:42:00Z',
    watermarkUtc: '2026-10-15T07:41:48Z',
    ingestedRecordsSinceStart: 1_250_000,
    liveConnections: 2,
    quotaDenialsSinceStart: 0,
  },
};
