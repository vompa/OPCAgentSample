// Formatierung und Hilfen für die Report-Seite.
import type { ReportHistogramBucket } from '@/api/types';
import { formatNumber } from './format';

export const ROLE_NAMES: Record<string, string> = {
  shift_lead: 'Schichtleiter',
  hall_lead: 'Hallenleiter',
  production_lead: 'Produktionsleiter',
};

export const OUTCOME_NAMES: Record<string, string> = {
  ok: 'OK',
  denied: 'Verweigert',
  error: 'Fehler',
};

/** Große Zahlen kompakt: 1.284 · 12,9 Tsd. · 4,2 Mio. */
export function compactNumber(value: number): string {
  const abs = Math.abs(value);
  if (abs >= 1_000_000) return `${formatNumber(value / 1_000_000, 1)} Mio.`;
  if (abs >= 10_000) return `${formatNumber(value / 1_000, 1)} Tsd.`;
  return formatNumber(value);
}

/** Dauer in Millisekunden als „420 ms“ oder „3,2 s“. */
export function durationText(ms: number | null): string {
  if (ms === null) return '–';
  return ms < 1000 ? `${formatNumber(Math.round(ms))} ms` : `${formatNumber(ms / 1000, 1)} s`;
}

/** Rückstand in Sekunden als „0 s“, „45 s“, „12 min“, „3,5 h“. */
export function lagText(seconds: number | null): string {
  if (seconds === null) return '–';
  if (seconds < 60) return `${formatNumber(Math.round(seconds))} s`;
  if (seconds < 3600) return `${formatNumber(Math.round(seconds / 60))} min`;
  return `${formatNumber(seconds / 3600, 1)} h`;
}

export function usdText(value: number): string {
  const decimals = value > 0 && value < 0.01 ? 4 : 2;
  return `${formatNumber(value, decimals)} $`;
}

export function percentText(rate: number | null): string {
  return rate === null ? '–' : `${formatNumber(rate * 100, 0)} %`;
}

/** Untergrenze eines Histogramm-Behälters in Sekunden („0,25“). */
export function bucketLowerLabel(bucket: ReportHistogramBucket): string {
  return formatNumber(bucket.fromMs / 1000, bucket.fromMs % 1000 === 0 ? 0 : 2);
}

/** Behälter als Bereich: „1–2 s“, „ab 30 s“, „unter 0,25 s“. */
export function bucketRangeText(bucket: ReportHistogramBucket): string {
  const from = formatNumber(bucket.fromMs / 1000, bucket.fromMs % 1000 === 0 ? 0 : 2);
  if (bucket.toMs === null) return `ab ${from} s`;
  const to = formatNumber(bucket.toMs / 1000, bucket.toMs % 1000 === 0 ? 0 : 2);
  return bucket.fromMs === 0 ? `unter ${to} s` : `${from}–${to} s`;
}

/** Rundet den größten Wert auf 1, 2, 5 oder 10 mal Zehnerpotenz auf (mindestens 2, damit die Mitte eine ganze Zahl ist). */
export function niceMax(max: number): number {
  if (max <= 2) return 2;
  const exponent = Math.floor(Math.log10(max));
  const base = 10 ** exponent;
  const fraction = max / base;
  const step = fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10;
  return step * base;
}
