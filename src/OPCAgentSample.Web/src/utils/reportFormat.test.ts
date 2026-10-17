import { describe, expect, it } from 'vitest';
import {
  bucketLowerLabel,
  bucketRangeText,
  compactNumber,
  durationText,
  lagText,
  niceMax,
  percentText,
  usdText,
} from './reportFormat';

describe('reportFormat', () => {
  it('kürzt große Zahlen', () => {
    expect(compactNumber(0)).toBe('0');
    expect(compactNumber(1284)).toBe('1.284');
    expect(compactNumber(12_900)).toBe('12,9 Tsd.');
    expect(compactNumber(4_200_000)).toBe('4,2 Mio.');
  });

  it('formatiert Dauer, Rückstand, Prozent und Dollar', () => {
    expect(durationText(null)).toBe('–');
    expect(durationText(420)).toBe('420 ms');
    expect(durationText(3200)).toBe('3,2 s');
    expect(lagText(null)).toBe('–');
    expect(lagText(45)).toBe('45 s');
    expect(lagText(720)).toBe('12 min');
    expect(lagText(12_600)).toBe('3,5 h');
    expect(percentText(0.753)).toBe('75 %');
    expect(percentText(null)).toBe('–');
    expect(usdText(0)).toBe('0,00 $');
    expect(usdText(0.0042)).toBe('0,0042 $');
    expect(usdText(1.5)).toBe('1,50 $');
  });

  it('beschriftet die Behälter des Histogramms', () => {
    expect(bucketLowerLabel({ fromMs: 0, toMs: 250, count: 0 })).toBe('0');
    expect(bucketLowerLabel({ fromMs: 250, toMs: 500, count: 0 })).toBe('0,25');
    expect(bucketRangeText({ fromMs: 0, toMs: 250, count: 0 })).toBe('unter 0,25 s');
    expect(bucketRangeText({ fromMs: 1000, toMs: 2000, count: 0 })).toBe('1–2 s');
    expect(bucketRangeText({ fromMs: 30_000, toMs: null, count: 0 })).toBe('ab 30 s');
  });

  it('rundet die Hochachse auf schöne Werte', () => {
    expect(niceMax(0)).toBe(2);
    expect(niceMax(1)).toBe(2);
    expect(niceMax(3)).toBe(5);
    expect(niceMax(7)).toBe(10);
    expect(niceMax(130)).toBe(200);
    expect(niceMax(4100)).toBe(5000);
  });
});
