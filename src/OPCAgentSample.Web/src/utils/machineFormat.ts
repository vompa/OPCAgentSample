// Formatierung und Hilfen für den Maschinen-Report.
import type { MachineOee, MachineRange, Rating } from '@/api/types';
import { formatNumber } from './format';

export const MACHINE_RANGES: readonly MachineRange[] = ['shift', 'yesterday', 'week', 'fourWeeks'];

export const RANGE_NAMES: Record<MachineRange, string> = {
  shift: 'Laufende Schicht',
  yesterday: 'Gestern',
  week: 'Diese Woche',
  fourWeeks: 'Letzte 4 Wochen',
};

export const RATING_WORDS: Record<Rating, string> = {
  ok: 'in Ordnung',
  warn: 'Warnung',
  crit: 'kritisch',
  info: 'Hinweis',
};

/** Startzeitraum je Rolle (wie auf dem Server): Schichtleiter die Schicht, Hallenleiterin die Woche, Produktionsleiter 4 Wochen. */
export function defaultRangeOf(role: string): MachineRange {
  if (role === 'hall_lead') return 'week';
  if (role === 'production_lead') return 'fourWeeks';
  return 'shift';
}

/** „47 min“ oder „4 h 51 min“. */
export function minutesText(minutes: number): string {
  const total = Math.round(minutes);
  return total >= 60
    ? `${Math.floor(total / 60)} h ${String(total % 60).padStart(2, '0')} min`
    : `${total} min`;
}

export function euroText(value: number): string {
  return `${formatNumber(value)} €`;
}

export function percentValue(value: number | null, decimals = 0): string {
  return value === null ? '–' : `${formatNumber(value, decimals)} %`;
}

export type DeltaDirection = 'up' | 'down' | 'flat';

export interface Delta {
  direction: DeltaDirection;
  /** „+3 Pkt“, „−0,4 Pkt“ oder „±0 Pkt“. */
  text: string;
  /** Besser (true), schlechter (false) oder gleich (null) für die Kennzahl. */
  better: boolean | null;
}

/**
 * Änderung gegenüber der Vorperiode in Prozentpunkten. `higherIsBetter`: bei OEE und Belegung steigend gut, bei Ausschuss und
 * Stillstand fallend gut. Änderungen unter der Rundung gelten als gleich.
 */
export function deltaOf(
  current: number | null,
  previous: number | null | undefined,
  higherIsBetter: boolean,
  decimals = 0,
): Delta | null {
  if (current === null || previous === null || previous === undefined) return null;
  const diff = Math.round((current - previous) * 10 ** decimals) / 10 ** decimals;
  if (diff === 0) return { direction: 'flat', text: '±0 Pkt', better: null };
  return {
    direction: diff > 0 ? 'up' : 'down',
    text: `${diff > 0 ? '+' : '−'}${formatNumber(Math.abs(diff), decimals)} Pkt`,
    better: higherIsBetter ? diff > 0 : diff < 0,
  };
}

export interface LossSegment {
  key: 'oee' | 'quality' | 'performance' | 'availability';
  label: string;
  percent: number;
}

/**
 * Zerlegt 100 % der Planbelegungszeit in die OEE und die drei Verluste: Verfügbarkeitsverlust = 1 − V, Leistungsverlust =
 * V·(1 − L), Qualitätsverlust = V·L·(1 − Q); die OEE ist V·L·Q. Die Segmente summieren sich auf 100.
 */
export function lossSegments(oee: MachineOee): LossSegment[] {
  const v = oee.availabilityPercent / 100;
  const l = oee.performancePercent / 100;
  const q = oee.qualityPercent / 100;
  return [
    { key: 'oee', label: 'OEE', percent: v * l * q * 100 },
    { key: 'quality', label: 'Qualitätsverlust', percent: v * l * (1 - q) * 100 },
    { key: 'performance', label: 'Leistungsverlust', percent: v * (1 - l) * 100 },
    { key: 'availability', label: 'Verfügbarkeitsverlust', percent: (1 - v) * 100 },
  ];
}
