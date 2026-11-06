// Zustand des Backends (Container App: Server, Simulator, Redis). Die Demo schläft im Leerlauf; die Oberfläche
// weckt sie erst nach der Anmeldung. Diese Datei kennt den Aufwachablauf (`/api/health` mit Backoff) und woher die angezeigten
// Daten stammen (Vorschau, zuletzt gesehen, live).
import { defineStore } from 'pinia';
import { computed, ref } from 'vue';
import { api } from '@/api/client';
import { DEMO_ACCESS } from '@/config';
import { sleep } from '@/utils/sleep';
import { useAuthStore } from './auth';

/** asleep = noch nicht geweckt, waking = startet, ready = antwortet, failed = Frist abgelaufen. */
export type BackendPhase = 'asleep' | 'waking' | 'ready' | 'failed';
/** Herkunft der angezeigten Daten. */
export type DataSource = 'none' | 'preview' | 'cached' | 'live';

/** Längstes Warten auf den Kaltstart (typisch etwa 40 s, im schlechtesten Fall wenige Minuten). */
export const WAKE_DEADLINE_MS = 5 * 60_000;
/** Wartezeiten zwischen den Health-Aufrufen; die letzte gilt für alle weiteren. */
export const WAKE_DELAYS_MS: readonly number[] = [1_000, 2_000, 3_000, 5_000, 5_000, 8_000, 10_000];
/**
 * Ab dieser Wartezeit gilt der Start als ungewöhnlich lang (typisch sind etwa 40 s). Die Oberfläche weist dann darauf hin,
 * dass die Demo pausiert sein könnte: Eine gestoppte Container App (z. B. nach dem Budget-Not-Stopp) antwortet mit einer
 * 404-Seite von Azure ohne CORS-Header, die der Browser nicht von „startet noch“ unterscheiden kann.
 */
export const WAKE_SLOW_MS = 60_000;

export function waitDelay(attempt: number): number {
  return WAKE_DELAYS_MS[Math.min(attempt, WAKE_DELAYS_MS.length - 1)]!;
}

export const useBackendStore = defineStore('backend', () => {
  // Ohne Demo-Zugang (lokal) gibt es kein schlafendes Backend: Alles lädt sofort live.
  const initialPhase: BackendPhase = DEMO_ACCESS ? 'asleep' : 'ready';
  const phase = ref<BackendPhase>(initialPhase);
  const source = ref<DataSource>(DEMO_ACCESS ? 'none' : 'live');
  /** Stand der gezeigten Daten als Uhrzeit der Demo-Uhr (z. B. „09:42“); leer bei Live-Daten. */
  const stamp = ref('');
  /** Das Wecken dauert schon länger als `WAKE_SLOW_MS`: Die Demo ist vielleicht pausiert. */
  const slow = ref(false);
  let generation = 0;
  let wakeRun: Promise<boolean> | null = null;

  const ready = computed(() => phase.value === 'ready');
  const live = computed(() => source.value === 'live');

  function show(next: Exclude<DataSource, 'none'>, standText = ''): void {
    source.value = next;
    stamp.value = next === 'live' ? '' : standText;
  }

  async function poll(run: number, auth: ReturnType<typeof useAuthStore>): Promise<boolean> {
    const started = Date.now();
    const deadline = started + WAKE_DEADLINE_MS;
    for (let attempt = 0; run === generation; attempt++) {
      try {
        const state = await api.health();
        if (run !== generation) return false;
        if (state.status !== 'unhealthy') {
          phase.value = 'ready';
          slow.value = false;
          return true;
        }
      } catch {
        // 502/503/504, Netzwerkfehler und Zeitüberschreitung heißen „startet noch“, nicht Fehler. Der Endpunkt ist anonym;
        // jede andere Störung gilt ebenfalls als „noch nicht bereit“ und wird bis zur Frist weiter versucht.
      }
      if (run !== generation || !auth.authenticated) return false;
      if (Date.now() - started >= WAKE_SLOW_MS) slow.value = true;
      if (Date.now() > deadline) {
        phase.value = 'failed';
        return false;
      }
      await sleep(waitDelay(attempt));
    }
    return false;
  }

  /**
   * Weckt das Backend (nur angemeldet) und wartet, bis `/api/health` antwortet. Ein laufender Anmeldeversuch gegen das
   * schlafende Backend wird zuerst abgewartet. Mehrfache Aufrufe teilen sich denselben Lauf.
   */
  function wake(): Promise<boolean> {
    if (phase.value === 'ready') return Promise.resolve(true);
    if (wakeRun) return wakeRun;
    const run = ++generation;
    phase.value = 'waking';
    slow.value = false;
    const auth = useAuthStore();
    wakeRun = (async () => {
      try {
        await auth.pendingLogin;
        if (!auth.authenticated) {
          if (run === generation) phase.value = 'asleep';
          return false;
        }
        return await poll(run, auth);
      } finally {
        wakeRun = null;
      }
    })();
    return wakeRun;
  }

  /** Das Backend antwortet nicht mehr (eingeschlafen): bei der nächsten Gelegenheit neu wecken; die gezeigten Daten bleiben. */
  function markAsleep(): void {
    if (phase.value === 'ready') phase.value = 'asleep';
  }

  /** Zurück in den Zustand vor dem Wecken (Abmelden, abgelaufene Sitzung): laufendes Warten endet. */
  function reset(): void {
    generation += 1;
    wakeRun = null;
    phase.value = initialPhase;
    slow.value = false;
    source.value = DEMO_ACCESS ? 'none' : 'live';
    stamp.value = '';
  }

  return { phase, source, stamp, slow, ready, live, show, wake, markAsleep, reset };
});
