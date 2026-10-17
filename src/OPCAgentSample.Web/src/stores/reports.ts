// Reports: aggregierte Kennzahlen (GET /api/reports/summary). Solange die Seite sichtbar ist, wird regelmäßig aktualisiert.
import { defineStore } from 'pinia';
import { ref } from 'vue';
import { api } from '@/api/client';
import type { ReportsSummary } from '@/api/types';
import { loadReportsSample } from '@/preview/snapshot';
import { useBackendStore } from './backend';

export type ReportsStatus = 'idle' | 'loading' | 'ready' | 'error';

export const REFRESH_MS = 30_000;

export const useReportsStore = defineStore('reports', () => {
  const summary = ref<ReportsSummary | null>(null);
  const status = ref<ReportsStatus>('idle');
  const errorText = ref('');
  /** Letzte Aktualisierung schlug fehl, es werden noch die zuletzt geladenen Werte gezeigt. */
  const stale = ref(false);
  /** Die gezeigten Werte sind Beispielwerte der Vorschau (das Backend schläft noch). */
  const sample = ref(false);
  let timer: ReturnType<typeof setInterval> | undefined;
  let inFlight = false;

  /** Lädt die Zusammenfassung; ein Fehler lässt bereits angezeigte Werte stehen und markiert sie als veraltet. */
  async function load(): Promise<void> {
    if (inFlight) return;
    inFlight = true;
    if (summary.value === null) status.value = 'loading';
    try {
      const live = useBackendStore().ready;
      summary.value = live ? await api.reportsSummary() : await loadReportsSample();
      sample.value = !live;
      status.value = 'ready';
      stale.value = false;
      errorText.value = '';
    } catch (error) {
      errorText.value = error instanceof Error ? error.message : String(error);
      if (summary.value === null) status.value = 'error';
      else stale.value = true;
    } finally {
      inFlight = false;
    }
  }

  /** Lädt sofort und danach alle 30 s, solange die Seite des Browsers sichtbar ist. */
  function startRefreshing(): void {
    stopRefreshing();
    void load();
    timer = setInterval(() => {
      if (!globalThis.document?.hidden) void load();
    }, REFRESH_MS);
  }

  function stopRefreshing(): void {
    if (timer !== undefined) clearInterval(timer);
    timer = undefined;
  }

  return { summary, sample, status, errorText, stale, load, startRefreshing, stopRefreshing };
});
