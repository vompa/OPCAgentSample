// Maschinen-Report (GET /api/reports/machines): Zeitraum, Vergleich, gewählte Station. In der Vorschau (Backend schläft) kommt der
// Report des Standardzeitraums der Rolle aus der Momentaufnahme; andere Zeiträume gibt es erst, wenn die Halle bereit ist.
import { defineStore } from 'pinia';
import { computed, ref } from 'vue';
import { api } from '@/api/client';
import type { MachineRange, MachineReport } from '@/api/types';
import { loadMachinesSample } from '@/preview/snapshot';
import { defaultRangeOf } from '@/utils/machineFormat';
import { useBackendStore } from './backend';
import { useIdentityStore } from './identity';

export type MachinesStatus = 'idle' | 'loading' | 'ready' | 'error';

export const MACHINES_REFRESH_MS = 30_000;

export const useMachinesStore = defineStore('machines', () => {
  const report = ref<MachineReport | null>(null);
  const status = ref<MachinesStatus>('idle');
  const errorText = ref('');
  const stale = ref(false);
  /** Die gezeigten Werte stammen aus der Momentaufnahme der Vorschau. */
  const sample = ref(false);
  const range = ref<MachineRange>(defaultRangeOf(useIdentityStore().role));
  const compare = ref(true);
  /** Kosten in Euro zeigen: beim Produktionsleiter von Anfang an, sonst auf Wunsch. */
  const showCosts = ref(useIdentityStore().role === 'production_lead');
  const selectedId = ref<string | null>(null);
  let loadedRole = useIdentityStore().role;
  let timer: ReturnType<typeof setInterval> | undefined;
  let inFlight = false;
  let again = false;

  const selected = computed(
    () => report.value?.stations.find((s) => s.id === selectedId.value) ?? null,
  );

  /** In der Vorschau gibt es nur den Standardzeitraum der Rolle. */
  function rangeAvailable(candidate: MachineRange): boolean {
    return !sample.value || candidate === defaultRangeOf(useIdentityStore().role);
  }

  /** Nach einem Rollenwechsel gelten der Startzeitraum und die Kostenanzeige der neuen Rolle. */
  function syncRole(): void {
    const role = useIdentityStore().role;
    if (role === loadedRole) return;
    loadedRole = role;
    range.value = defaultRangeOf(role);
    showCosts.value = role === 'production_lead';
    compare.value = true;
    selectedId.value = null;
    report.value = null;
    status.value = 'idle';
  }

  async function load(): Promise<void> {
    if (inFlight) {
      again = true;
      return;
    }
    syncRole();
    inFlight = true;
    if (report.value === null) status.value = 'loading';
    try {
      const role = useIdentityStore().role;
      if (useBackendStore().ready) {
        report.value = await api.reportsMachines(role, range.value, compare.value);
        sample.value = false;
      } else {
        report.value = await loadMachinesSample(role);
        range.value = report.value.range;
        compare.value = report.value.compare;
        sample.value = true;
      }
      status.value = 'ready';
      stale.value = false;
      errorText.value = '';
    } catch (error) {
      errorText.value = error instanceof Error ? error.message : String(error);
      if (report.value === null) status.value = 'error';
      else stale.value = true;
    } finally {
      inFlight = false;
    }
    if (again) {
      again = false;
      await load();
    }
  }

  function setRange(next: MachineRange): void {
    if (!rangeAvailable(next) || next === range.value) return;
    range.value = next;
    void load();
  }

  function setCompare(next: boolean): void {
    if (sample.value || next === compare.value) return;
    compare.value = next;
    void load();
  }

  function select(id: string | null): void {
    selectedId.value = id;
  }

  /** Lädt sofort; live danach alle 30 s, solange die Seite sichtbar ist (die Vorschau ändert sich nicht). */
  function startRefreshing(): void {
    stopRefreshing();
    void load();
    timer = setInterval(() => {
      if (!globalThis.document?.hidden && !sample.value) void load();
    }, MACHINES_REFRESH_MS);
  }

  function stopRefreshing(): void {
    if (timer !== undefined) clearInterval(timer);
    timer = undefined;
  }

  return {
    report,
    status,
    errorText,
    stale,
    sample,
    range,
    compare,
    showCosts,
    selectedId,
    selected,
    rangeAvailable,
    load,
    setRange,
    setCompare,
    select,
    startRefreshing,
    stopRefreshing,
  };
});
