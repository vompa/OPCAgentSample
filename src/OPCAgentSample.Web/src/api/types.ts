// Formate der HTTP-API (JSON, camelCase) – spiegeln src/OPCAgentSample.Server/Api/ApiModels.cs und Demo/DemoModels.cs.

export type Rating = 'ok' | 'warn' | 'crit' | 'info';
/** `nodata`: Es liegen keine Messwerte vor; weder Entwarnung noch Befund. */
export type StationStatus = 'running' | 'warning' | 'problem' | 'nodata';

export interface RoleView {
  id: string;
  title: string;
  label: string;
  scope: string;
  scopeDescription: string;
  devices: string;
  rights: string[];
  messageFilter: string;
  feedFilterText: string;
}

export interface UserView {
  id: string;
  displayName: string;
  initials: string;
  firstName: string;
  title: string;
  mobileTitle: string;
  workTime: string;
  notification: string;
}

export interface RoleOption {
  id: string;
  label: string;
  shortLabel: string;
}

/** Simulationsuhr des Servers (nicht die Browser-Uhr). */
export interface ClockView {
  utcNow: string;
  localText: string;
  localTime: string;
  greeting: string;
}

export interface StatusView {
  opcConnected: boolean;
  mcpToolCount: number;
  area: string;
  shift: string;
  scopeText: string;
  /** Die Historie wird beim Start noch aufgenommen (Kaltstart); Zahlen sind noch unvollständig. */
  historyLoading: boolean;
  /** Hinweis zur Zeitbasis (z. B. Simulator mit früherer Startzeit neu gestartet) oder null. */
  dataWarning: string | null;
}

/** Zustand der Anmeldung der Live-Demo (`GET /api/demo/session`). */
export interface DemoSessionState {
  enabled: boolean;
  authenticated: boolean;
  expiresAtUtc: string | null;
}

/** Antwort von `POST /api/demo/login`: Token für `Authorization: Bearer` (Apps) und Ablauf. */
export interface DemoLoginResult {
  token: string;
  expiresAtUtc: string;
}

export interface FeedEntry {
  time: string;
  source: string;
  text: string;
  level: Rating;
  /** Nur Oberfläche: frisch eingetroffen (kurz hervorgehoben). */
  fresh?: boolean;
}

export interface CountersView {
  inboxOpen: number;
  inboxHasUrgent: boolean;
  feedCount: number;
  latestFeed: FeedEntry | null;
}

export interface MeResponse {
  role: RoleView;
  user: UserView;
  roles: RoleOption[];
  clock: ClockView;
  status: StatusView;
  counters: CountersView;
}

export interface StatusCounts {
  problem: number;
  warning: number;
  running: number;
  /** Stationen ohne Messwerte; zählen weder als „Läuft“ noch als Befund. */
  noData?: number;
}

export interface StationKpi {
  value: string;
  unit: string;
}

export interface StationView {
  id: string;
  status: StationStatus;
  statusWord: string;
  article: string;
  kpi: StationKpi;
  message: string;
}

export interface StationGroupView {
  id: string;
  title: string;
  kpiLabel: string;
  stationCount: number;
  worstStatus: StationStatus;
  counts: StatusCounts;
  stations: StationView[];
}

export interface ClimateInfo {
  temperature: string;
  humidity: string;
  text: string;
  tooltip: string;
  status: string;
}

export interface OverviewResponse {
  scope: string;
  id: string;
  title: string;
  liveText: string;
  climate: ClimateInfo | null;
  counts: StatusCounts;
  groups: StationGroupView[];
}

export interface FavoriteView {
  shortTitle: string;
  question: string;
  isDefault: boolean;
}

export interface FavoriteDefault {
  shortTitle: string;
  question: string;
}

export interface FavoritesResponse {
  max: number;
  maxShortTitleLength: number;
  items: FavoriteView[];
  defaults: FavoriteDefault[];
}

export interface FavoriteInput {
  shortTitle: string;
  question: string;
}

export interface InboxItem {
  id: string;
  level: Rating;
  priority: string;
  priorityText: string;
  title: string;
  detail: string;
  due: string;
  question: string | null;
  takenBy: string | null;
  takenAtText: string | null;
}

export interface FeedResponse {
  filterText: string;
  items: FeedEntry[];
}

export interface AnswerTableRow {
  cells: string[];
  rating: Rating;
}

export interface AnswerTable {
  columns: string[];
  rows: AnswerTableRow[];
}

export interface ChartBar {
  label: string;
  value: number;
  rating: Rating;
}

export interface AnswerChart {
  title: string;
  max: number;
  unit: string;
  decimals: number;
  bars: ChartBar[];
}

export interface AnswerCard {
  headline: string;
  todo: string[];
  table: AnswerTable;
  chart: AnswerChart | null;
  why: string[];
  dataAsOf: string;
  sources: string[];
  prompt?: string | null;
  form?: string | null;
  notice?: string | null;
}

/** Ereignis `answer_draft`: Anfang der Karte, noch während Claude schreibt (nur Anzeige; die geprüfte Karte folgt in `answer`). */
export interface AnswerDraft {
  headline: string;
  todo: string[];
}

/** `denied` = Rolle darf das Tool nicht nutzen (kein Fehler). */
export type ToolOutcome = 'ok' | 'error' | 'denied';

export interface ToolCall {
  tool: string;
  parameters: Record<string, unknown> | null;
  durationMs: number | null;
  source: string;
  dataSource: string;
  dataAsOf?: string | null;
  outcome?: ToolOutcome | null;
}

export interface ChatSessionSummary {
  id: string;
  role: string;
  startedAtUtc: string;
  lastActivityUtc: string;
  title: string;
  turnCount: number;
}

export interface ChatTurnRecord {
  sequence: number;
  askedAtUtc: string;
  question: string;
  answerCard: AnswerCard;
  toolCalls: ToolCall[];
  dataAsOfUtc: string;
  inputTokens: number;
  outputTokens: number;
}

export interface ChatSessionPackage {
  session: ChatSessionSummary;
  turns: ChatTurnRecord[];
}

export interface TokenUsage {
  input: number;
  output: number;
  cacheRead: number;
  cacheWrite: number;
}

/** Ereignis `done` von POST /api/ask. */
export interface AskDone {
  sessionId: string;
  sequence: number;
  tokens: TokenUsage;
  dataAsOf: string;
  dataAsOfText: string;
  /** Gespeicherte Antwort auf dieselbe Frage von eben: keine Tokens, kein Kontingent. */
  repeat?: boolean;
}

/** Live-Ereignis `alarm`. */
export interface AlarmSummary {
  count: number;
  items: unknown[];
}

/** Live-Ereignis `inbox`. */
export interface InboxSummary {
  open: number;
  hasUrgent: boolean;
}

/** Antwort von GET /api/reports/summary: aggregierte Werte über alle Besitzer, ohne Frageinhalte. */
export interface ReportsSummary {
  period: {
    generatedAtUtc: string;
    firstQuestionAtUtc: string | null;
    serverStartedAtUtc: string;
    seriesHours: number;
  };
  questions: { total: number; today: number; last24Hours: number; answeredByClaude: number };
  tokens: { input: number; output: number; cacheRead: number; cacheWrite: number; total: number };
  cost: {
    estimatedUsd: number;
    cacheHitRate: number | null;
    prices: {
      inputPerMillion: number;
      outputPerMillion: number;
      cacheReadPerMillion: number;
      cacheWritePerMillion: number;
    };
  };
  latency: {
    measured: number;
    averageMs: number | null;
    p50Ms: number | null;
    p95Ms: number | null;
  };
  latencyHistogram: ReportHistogramBucket[];
  timeSeries: {
    startUtc: string;
    intervalMinutes: number;
    questions: number[];
    inputTokens: number[];
    outputTokens: number[];
  };
  tools: ReportToolRow[];
  ingestion: {
    lagSeconds: number | null;
    simulationClockUtc: string;
    watermarkUtc: string | null;
    ingestedRecordsSinceStart: number;
    liveConnections: number;
    quotaDenialsSinceStart: number;
  };
}

/** Behälter des Histogramms: `toMs` ist beim letzten, nach oben offenen Behälter `null`. */
export interface ReportHistogramBucket {
  fromMs: number;
  toMs: number | null;
  count: number;
}

export interface ReportToolRow {
  tool: string;
  role: string;
  outcome: ToolOutcome;
  calls: number;
  averageMs: number | null;
  p95Ms: number | null;
}

// ---- Maschinen-Report (GET /api/reports/machines) ----------------------------------------------------------

/** Zeiträume des Maschinen-Reports. */
export type MachineRange = 'shift' | 'yesterday' | 'week' | 'fourWeeks';

export interface MachineOee {
  oeePercent: number;
  availabilityPercent: number;
  performancePercent: number;
  qualityPercent: number;
  rating: Rating;
}

/** Werte der Vorperiode (Quoten, weil die laufende Periode noch nicht abgeschlossen ist). */
export interface MachinePrevious {
  oeePercent: number | null;
  scrapPercent: number | null;
  downtimeSharePercent: number | null;
  occupancyPercent: number | null;
}

export interface MachinePareto {
  reason: string;
  minutes: number;
  count: number;
  euro: number;
}

export interface MachineScrapReason {
  reason: string;
  count: number;
  percent: number;
}

/** Verlauf eines Prozesswerts mit Toleranzband; `outsidePercent` ist der Anteil der Werte außerhalb von [low, high]. */
export interface MachineTrend {
  key: string;
  label: string;
  unit: string;
  decimals: number;
  target: number | null;
  low: number | null;
  high: number | null;
  outsidePercent: number | null;
  basis: string;
  rating: Rating;
  labels: string[];
  values: number[];
}

export interface MachineShiftReliability {
  shift: string;
  mttrMinutes: number | null;
  failures: number;
  reactionMinutes: number | null;
  reactionCount: number;
}

export interface MachineReliability {
  mtbfHours: number | null;
  mttrMinutes: number | null;
  failures: number;
  unplannedSharePercent: number;
  byShift: MachineShiftReliability[];
}

export interface MachineMould {
  mouldId: string;
  wearPercent: number;
  strokes: number;
  strokeLimit: number;
  remainingStrokes: number;
  limitDay: string | null;
  rating: Rating;
}

export interface MachineMaintenanceItem {
  id: string;
  title: string;
  dueText: string;
  overdue: boolean;
  stopMinutes: number;
}

export interface MachineShiftOee {
  shift: string;
  oeePercent: number | null;
}

export type MachineType = 'press' | 'oven' | 'packaging';

export interface MachineStation {
  id: string;
  name: string;
  type: MachineType;
  rating: Rating;
  oee: MachineOee | null;
  occupancyPercent: number | null;
  unit: string | null;
  actual: number;
  planned: number;
  planPercent: number | null;
  scrapPercent: number | null;
  downtimeMinutes: number;
  downtimeSharePercent: number;
  downtimeEuro: number;
  scrapEuro: number;
  previous: MachinePrevious | null;
  pareto: MachinePareto[];
  scrapByReason: MachineScrapReason[];
  trends: MachineTrend[];
  reliability: MachineReliability;
  maintenance: { mould: MachineMould | null; items: MachineMaintenanceItem[] };
  shiftOee: MachineShiftOee[];
}

export interface MachineShift {
  shift: string;
  oee: MachineOee | null;
  downtimeMinutes: number;
  startupLossPieces: number;
  startupLossCount: number;
  startupLossMinutes: number;
  mttrMinutes: number | null;
  reactionMinutes: number | null;
  failures: number;
}

export interface MachineCostItem {
  station: string;
  reason: string;
  minutes: number;
  euro: number;
}

export interface MachineCosts {
  downtimeEuro: number;
  scrapEuro: number;
  totalEuro: number;
  previousTotalEuro: number | null;
  euroPerPlanHour: number;
  previousEuroPerPlanHour: number | null;
  topItems: MachineCostItem[];
}

export interface MachineSummary {
  oee: MachineOee | null;
  planPercent: number | null;
  scrapPercent: number | null;
  downtimeMinutes: number;
  previous: MachinePrevious | null;
}

/** Auffälligkeit; `question` ist die Frage, die „Mit Claude analysieren“ stellt. */
export interface MachineFinding {
  id: string;
  station: string | null;
  rating: Rating;
  title: string;
  text: string;
  question: string;
}

/** Verweis auf die Auffälligkeit eines Reports; der Server liest Zeitraum und Werte selbst aus dem Report. */
export interface ReportRef {
  kind: 'machines';
  range: MachineRange;
  findingId: string;
}

export interface MachineReport {
  role: string;
  range: MachineRange;
  defaultRange: MachineRange;
  compare: boolean;
  rangeLabel: string;
  previousLabel: string | null;
  startUtc: string;
  endUtc: string;
  generatedAtUtc: string;
  dataAsOf: string;
  source: string;
  notes: string[];
  summary: MachineSummary;
  stations: MachineStation[];
  shiftComparison: { basis: string; shifts: MachineShift[] };
  costs: MachineCosts;
  findings: MachineFinding[];
}
