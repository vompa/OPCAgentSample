// Formatierung für die Anzeige: Uhrzeiten (Ortszeit Wien), Zahlen, Parameter, Kurztitel.
import type { Rating, StationStatus, ToolCall } from '@/api/types';

const ZONE = 'Europe/Vienna';
const WEEKDAYS = ['So', 'Mo', 'Di', 'Mi', 'Do', 'Fr', 'Sa'];

export const RATING_TEXT: Record<Rating, string> = {
  ok: 'OK',
  warn: 'Warnung',
  crit: 'Kritisch',
  info: 'Info',
};

export const STATUS_WORD: Record<StationStatus, string> = {
  running: 'Läuft',
  warning: 'Warnung',
  problem: 'Problem',
  nodata: 'Keine Daten',
};

/** „09:42“ – aus einem UTC-Zeitpunkt (Datenstand der Simulationsuhr), nicht aus der Browser-Uhr. */
export function timeText(iso: string): string {
  return new Date(iso).toLocaleTimeString('de-AT', {
    hour: '2-digit',
    minute: '2-digit',
    timeZone: ZONE,
  });
}

/** „Do 15.10., 09:42“ */
export function dateTimeText(iso: string): string {
  const date = new Date(iso);
  const parts = new Intl.DateTimeFormat('de-AT', {
    day: '2-digit',
    month: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    timeZone: ZONE,
    hourCycle: 'h23',
  }).formatToParts(date);
  const get = (type: string) => parts.find((part) => part.type === type)?.value ?? '';
  const weekday = WEEKDAYS[new Date(date.toLocaleString('en-US', { timeZone: ZONE })).getDay()];
  return `${weekday} ${get('day')}.${get('month')}., ${get('hour')}:${get('minute')}`;
}

/** Kalendertag in Wien („2026-10-15“), um Zeitpunkte nach Tagen zu gruppieren. */
export function dayKey(iso: string): string {
  return new Date(iso).toLocaleDateString('sv-SE', { timeZone: ZONE });
}

/** „Do 15.10.“ */
export function dateText(iso: string): string {
  const parts = new Intl.DateTimeFormat('de-AT', {
    day: '2-digit',
    month: '2-digit',
    timeZone: ZONE,
  }).formatToParts(new Date(iso));
  const get = (type: string) => parts.find((part) => part.type === type)?.value ?? '';
  const weekday = WEEKDAYS[new Date(`${dayKey(iso)}T12:00:00Z`).getUTCDay()];
  return `${weekday} ${get('day')}.${get('month')}.`;
}

/** Zahl mit festen Nachkommastellen und Komma (0,80). */
export function formatNumber(value: number, decimals = 0): string {
  return value.toLocaleString('de-DE', {
    minimumFractionDigits: decimals,
    maximumFractionDigits: decimals,
  });
}

/** Parameter eines Tool-Aufrufs in der Schreibweise des Mockups: { scope: "Halle A", schicht: "vorherige" } */
export function formatParameters(parameters: ToolCall['parameters']): string {
  const entries = Object.entries(parameters ?? {});
  if (entries.length === 0) return '{ }';
  return `{ ${entries.map(([key, value]) => `${key}: ${JSON.stringify(value)}`).join(', ')} }`;
}

export function shortTitleOf(question: string, max = 22): string {
  const text = question.trim().replace(/[?.!]+$/, '');
  return text.length > max ? `${text.slice(0, max - 1)}…` : text;
}

export function truncate(text: string, max: number): string {
  return text.length > max ? `${text.slice(0, max - 2)} …` : text;
}

export function totalDuration(toolCalls: ToolCall[]): number {
  return toolCalls.reduce((sum, call) => sum + (call.durationMs ?? 0), 0);
}

/** Beschriftung der Datenquelle im MCP-Band; Verweigerungen (Rolle) sind kein Fehler. */
export function callSourceText(call: ToolCall): string {
  const parts = [call.dataSource, call.dataAsOf ? `Stand ${call.dataAsOf}` : null];
  if (call.outcome === 'denied') parts.push('Verweigert (Rolle)');
  else if (call.outcome === 'error' && call.dataSource !== 'Fehler') parts.push('Fehler');
  return parts.filter(Boolean).join(' · ');
}

/** Kennungen und Zeiträume, die nicht am Bindestrich umbrechen sollen: RT-03, F-125-07, 06:00-07:05. */
const IDENTIFIER_PATTERN =
  /\b[A-Z]{1,3}-\d{1,4}(?:-\d{1,3})?\b|\b\d{1,2}:\d{2}\s?[-–]\s?\d{1,2}:\d{2}\b/g;

export interface TextPart {
  text: string;
  /** Teil einer Kennung: darf nicht umbrechen. */
  keep: boolean;
}

/** Zerlegt einen Text in Kennungen (unteilbar) und übrigen Text, damit Tabellen und Überschriften sauber umbrechen. */
export function splitIdentifiers(text: string): TextPart[] {
  const parts: TextPart[] = [];
  let last = 0;
  for (const match of text.matchAll(IDENTIFIER_PATTERN)) {
    if (match.index > last) parts.push({ text: text.slice(last, match.index), keep: false });
    parts.push({ text: match[0], keep: true });
    last = match.index + match[0].length;
  }
  if (last < text.length) parts.push({ text: text.slice(last), keep: false });
  return parts;
}
