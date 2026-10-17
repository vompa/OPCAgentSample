// Momentaufnahme der Demo (Vorschau-Daten): statische Dateien unter /snapshot, erzeugt vom Werkzeug OPCAgentSample.Snapshot
// zur Demo-Uhr (Do 15.10.2026 09:42). Sie liegen auf derselben Herkunft wie die Oberfläche, ihr Abruf weckt das Backend nicht.
import { SNAPSHOT_BASE } from '@/config';
import type {
  AnswerCard,
  ChatSessionPackage,
  FavoritesResponse,
  FeedResponse,
  InboxItem,
  MachineReport,
  MeResponse,
  OverviewResponse,
  ReportsSummary,
  ToolCall,
} from '@/api/types';

/** Vorbereitete Antwort auf eine Standard-Favoritenfrage. */
export interface PreparedAnswer {
  toolCalls: ToolCall[];
  card: AnswerCard;
}

/** Alles, was die Oberfläche einer Rolle zeigt, solange das Backend schläft. */
export interface RoleData {
  me: MeResponse;
  overview: OverviewResponse;
  favorites: FavoritesResponse;
  inbox: InboxItem[];
  feed: FeedResponse;
  history: ChatSessionPackage;
}

/** Datei der Momentaufnahme (und Form der Eintragung im Zwischenspeicher, ergänzt um die Herkunft). */
export interface RoleSnapshot extends RoleData {
  version: string;
  asOfUtc: string;
  /** Vorbereitete Antworten, Schlüssel = Fragetext der Standard-Favoriten. */
  answers: Record<string, PreparedAnswer>;
}

export const SNAPSHOT_VERSION = '1';

async function fetchJson<T>(name: string): Promise<T> {
  const response = await fetch(`${SNAPSHOT_BASE}/${name}.json`, { credentials: 'omit' });
  if (!response.ok)
    throw new Error(`Momentaufnahme ${name} nicht verfügbar (HTTP ${response.status})`);
  return (await response.json()) as T;
}

export async function loadSnapshot(role: string): Promise<RoleSnapshot> {
  const snapshot = await fetchJson<RoleSnapshot>(role);
  if (snapshot.version !== SNAPSHOT_VERSION)
    throw new Error('Momentaufnahme hat ein unbekanntes Format');
  return snapshot;
}

export function loadReportsSample(): Promise<ReportsSummary> {
  return fetchJson<ReportsSummary>('reports');
}

/** Maschinen-Report der Rolle im Standardzeitraum der Rolle (Stand 09:42); andere Zeiträume gibt es in der Vorschau nicht. */
export function loadMachinesSample(role: string): Promise<MachineReport> {
  return fetchJson<MachineReport>(`machines_${role}`);
}

/** Vergleichsform einer Frage (wie im Server: Leerraum, Satzzeichen am Ende und Groß-/Kleinschreibung ignorieren). */
export function normalizeQuestion(question: string): string {
  return question
    .split(/\s+/)
    .filter(Boolean)
    .join(' ')
    .replace(/[?.!]+$/, '')
    .toLowerCase();
}

/** Vorbereitete Antwort zu einer Frage oder null (dann geht die Frage in die Warteschlange). */
export function findPreparedAnswer(
  answers: Record<string, PreparedAnswer>,
  question: string,
): PreparedAnswer | null {
  const wanted = normalizeQuestion(question);
  const key = Object.keys(answers).find((candidate) => normalizeQuestion(candidate) === wanted);
  return key ? answers[key]! : null;
}
