<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue';
import FindingList from '@/components/FindingList.vue';
import MachineDetail from '@/components/MachineDetail.vue';
import MachineGrid from '@/components/MachineGrid.vue';
import ShiftComparison from '@/components/ShiftComparison.vue';
import { useBackendStore } from '@/stores/backend';
import { useIdentityStore } from '@/stores/identity';
import { useMachinesStore } from '@/stores/machines';
import { isMobile, useUiStore } from '@/stores/ui';
import {
  MACHINE_RANGES,
  RANGE_NAMES,
  deltaOf,
  euroText,
  minutesText,
  percentValue,
} from '@/utils/machineFormat';

const machines = useMachinesStore();
const ui = useUiStore();
const identity = useIdentityStore();
const backend = useBackendStore();

/** Nur wenn der Reiter sichtbar ist (Desktop: Reports offen, Handy: Reiter Reports), wird geladen und aktualisiert. */
const visible = computed(
  () => (ui.deskReports || ui.mobileView === 'reports') && ui.reportsTab === 'machines',
);

watch(
  [visible, () => identity.role, () => backend.ready],
  ([isVisible]) => {
    if (isVisible) machines.startRefreshing();
    else machines.stopRefreshing();
  },
  { immediate: true },
);
onBeforeUnmount(() => machines.stopRefreshing());

const report = computed(() => machines.report);
const compare = computed(() => machines.compare && report.value?.previousLabel != null);
const stationFindings = computed(() =>
  (report.value?.findings ?? []).filter((f) => f.station === machines.selectedId),
);
const overviewFindings = computed(() => report.value?.findings ?? []);

const detailElement = ref<HTMLElement | null>(null);

watch(
  () => machines.selectedId,
  async (id) => {
    await nextTick();
    if (id === null) return;
    // Handy: Die Detailseite beginnt oben; Desktop: Der Stationsbereich rückt unter das Raster.
    const container = document.getElementById('mview-reports');
    const detail = detailElement.value;
    if (!container) return;
    if (isMobile() || !detail) {
      container.scrollTop = 0;
      return;
    }
    const offset = detail.getBoundingClientRect().top - container.getBoundingClientRect().top;
    container.scrollTop += offset - 12;
  },
);

interface Tile {
  key: string;
  label: string;
  value: string;
  note: string;
  delta: ReturnType<typeof deltaOf>;
}

const tiles = computed<Tile[]>(() => {
  const r = report.value;
  if (!r) return [];
  const summary = r.summary;
  const previous = summary.previous;
  const list: Tile[] = [
    {
      key: 'oee',
      label: 'OEE Halle A',
      value: percentValue(summary.oee?.oeePercent ?? null),
      note: 'Pressen und Verpackung',
      delta: compare.value
        ? deltaOf(summary.oee?.oeePercent ?? null, previous?.oeePercent, true)
        : null,
    },
    {
      key: 'plan',
      label: 'Planerfüllung Pressen',
      value: percentValue(summary.planPercent),
      note: 'Ist gegen Soll der Scheiben',
      delta: null,
    },
    {
      key: 'scrap',
      label: 'Ausschuss Pressen',
      value: percentValue(summary.scrapPercent, 1),
      note: 'Anteil der Scheiben',
      delta: compare.value ? deltaOf(summary.scrapPercent, previous?.scrapPercent, false, 1) : null,
    },
    {
      key: 'downtime',
      label: 'Stillstand ungeplant',
      value: minutesText(summary.downtimeMinutes),
      note:
        previous && compare.value
          ? `Vorher ${percentValue(previous.downtimeSharePercent, 1)} der Planzeit`
          : 'Alle Stationen der Halle',
      delta: null,
    },
  ];
  if (machines.showCosts) {
    list.push({
      key: 'costs',
      label: 'Kosten der Verluste',
      value: euroText(r.costs.totalEuro),
      note: `${euroText(r.costs.euroPerPlanHour)} je Planstunde${
        compare.value && r.costs.previousEuroPerPlanHour !== null
          ? `, vorher ${euroText(r.costs.previousEuroPerPlanHour)}`
          : ''
      }`,
      delta: null,
    });
  }
  return list;
});

const costRows = computed(() => {
  const stations = report.value?.stations ?? [];
  return stations
    .map((s) => ({
      id: s.id,
      down: s.downtimeEuro,
      scrap: s.scrapEuro,
      total: s.downtimeEuro + s.scrapEuro,
    }))
    .filter((row) => row.total > 0)
    .sort((a, b) => b.total - a.total);
});
const costMax = computed(() => Math.max(1, ...costRows.value.map((row) => row.total)));
const itemMax = computed(() =>
  Math.max(1, ...(report.value?.costs.topItems ?? []).map((i) => i.euro)),
);

function selectStation(id: string): void {
  machines.select(id);
}
</script>

<template>
  <div :class="['mc', { 'has-detail': machines.selected }]">
    <p
      v-if="machines.status === 'idle' || machines.status === 'loading'"
      class="empty-note"
      role="status"
    >
      Maschinen-Report wird geladen …
    </p>

    <div v-else-if="machines.status === 'error'" class="reports-empty" role="alert">
      <p>Der Maschinen-Report konnte nicht geladen werden: {{ machines.errorText }}</p>
      <button type="button" class="btn" @click="machines.load()">Erneut versuchen</button>
    </div>

    <template v-else-if="report">
      <p v-if="machines.stale" class="reports-stale" role="status">
        Die Aktualisierung ist fehlgeschlagen ({{ machines.errorText }}). Angezeigt werden die
        zuletzt geladenen Werte.
      </p>
      <p v-if="machines.sample" class="reports-stale" role="status">
        Vorschau mit Beispielwerten vom Stand {{ report.dataAsOf }} Uhr. Die anderen Zeiträume und
        der Vergleich sind verfügbar, sobald die Halle bereit ist.
      </p>

      <div class="mc-controls">
        <div class="mc-ranges" role="group" aria-label="Zeitraum">
          <button
            v-for="range in MACHINE_RANGES"
            :key="range"
            type="button"
            :class="['mc-chip', { 'is-active': machines.range === range }]"
            :aria-pressed="machines.range === range"
            :aria-disabled="!machines.rangeAvailable(range) ? 'true' : undefined"
            :disabled="!machines.rangeAvailable(range)"
            @click="machines.setRange(range)"
          >
            {{ RANGE_NAMES[range] }}
          </button>
        </div>
        <div class="mc-options">
          <label class="mc-check">
            <input
              type="checkbox"
              :checked="machines.compare"
              :disabled="machines.sample"
              @change="machines.setCompare(($event.target as HTMLInputElement).checked)"
            />
            <span>Mit Vorperiode vergleichen</span>
          </label>
          <label class="mc-check">
            <input v-model="machines.showCosts" type="checkbox" />
            <span>Kosten in € zeigen</span>
          </label>
        </div>
      </div>
      <p class="mc-context">
        <strong>{{ report.rangeLabel }}</strong>
        <template v-if="compare"> · verglichen mit {{ report.previousLabel }}</template> · Halle A,
        8 Stationen · Datenstand {{ report.dataAsOf }} Uhr
      </p>

      <div class="mc-main">
        <section class="mc-kpis" aria-label="Kennzahlen der Halle A">
          <div v-for="tile in tiles" :key="tile.key" class="mc-kpi">
            <span class="mc-kpi-label">{{ tile.label }}</span>
            <span class="mc-kpi-value">{{ tile.value }}</span>
            <span
              v-if="tile.delta"
              :class="[
                'mc-delta',
                `is-${tile.delta.direction}`,
                {
                  'is-better': tile.delta.better === true,
                  'is-worse': tile.delta.better === false,
                },
              ]"
              ><span class="mc-arrow" aria-hidden="true"></span>{{ tile.delta.text }}
              <span class="visually-hidden">gegenüber {{ report.previousLabel }}</span></span
            >
            <span class="mc-kpi-note">{{ tile.note }}</span>
          </div>
        </section>

        <section aria-labelledby="mc-grid-title">
          <h3 id="mc-grid-title" class="panel-title mc-section-title">
            Stationen <span class="mc-hint">Station wählen für den Stationsbereich</span>
          </h3>
          <MachineGrid
            :stations="report.stations"
            :selected-id="machines.selectedId"
            :compare="compare"
            :previous-label="report.previousLabel"
            :show-costs="machines.showCosts"
            @select="selectStation"
          />
        </section>
      </div>

      <div v-if="machines.selected" ref="detailElement" class="mc-detail-wrap">
        <MachineDetail
          :station="machines.selected"
          :range="report.range"
          :range-label="report.rangeLabel"
          :findings="stationFindings"
          :show-costs="machines.showCosts"
          @back="machines.select(null)"
        />
      </div>

      <div class="mc-main mc-overview">
        <div class="mc-overview-grid">
          <section class="rp-card" aria-labelledby="mc-findings-title">
            <h3 id="mc-findings-title" class="panel-title">Auffälligkeiten</h3>
            <FindingList
              :findings="overviewFindings"
              :range="report.range"
              empty-text="Keine Auffälligkeit im Zeitraum."
            />
          </section>

          <section class="rp-card" aria-labelledby="mc-shifts-title">
            <h3 id="mc-shifts-title" class="panel-title">Schichtvergleich</h3>
            <ShiftComparison
              :shifts="report.shiftComparison.shifts"
              :basis="report.shiftComparison.basis"
            />
          </section>

          <section
            v-if="machines.showCosts"
            class="rp-card mc-costs"
            aria-labelledby="mc-costs-title"
          >
            <h3 id="mc-costs-title" class="panel-title">Kosten der Verluste</h3>
            <p class="mc-cost-total">
              <strong>{{ euroText(report.costs.totalEuro) }}</strong>
              <span class="mc-muted"
                >Stillstand {{ euroText(report.costs.downtimeEuro) }} · Ausschuss
                {{ euroText(report.costs.scrapEuro)
                }}<template v-if="compare && report.costs.previousTotalEuro !== null">
                  · Vorperiode {{ euroText(report.costs.previousTotalEuro) }}</template
                ></span
              >
            </p>
            <div class="mc-costs-grid">
              <div>
                <h4 class="mc-subtitle">Je Station</h4>
                <ul class="mc-bars" aria-label="Kosten je Station">
                  <li v-for="row in costRows" :key="row.id" class="mc-bar-row">
                    <span class="mc-bar-label">{{ row.id }}</span>
                    <span class="mc-bar-track" aria-hidden="true">
                      <span
                        class="mc-bar-fill"
                        :style="{ width: `${(row.down / costMax) * 100}%` }"
                      ></span>
                      <span
                        class="mc-bar-fill is-scrap"
                        :style="{ width: `${(row.scrap / costMax) * 100}%` }"
                      ></span>
                    </span>
                    <span class="mc-bar-value"
                      >{{ euroText(row.total) }}
                      <span class="mc-bar-sub"
                        >{{ euroText(row.down) }} + {{ euroText(row.scrap) }}</span
                      ></span
                    >
                  </li>
                </ul>
                <p class="mc-trend-key" aria-hidden="true">
                  <span class="mc-key-band is-down"></span>Stillstand
                  <span class="mc-key-band is-scrap"></span>Ausschuss
                </p>
              </div>
              <div>
                <h4 class="mc-subtitle">Größte Posten (Station und Grund)</h4>
                <ul class="mc-bars" aria-label="Größte Kostenposten">
                  <li
                    v-for="item in report.costs.topItems"
                    :key="`${item.station}|${item.reason}`"
                    class="mc-bar-row"
                  >
                    <span class="mc-bar-label">{{ item.station }} · {{ item.reason }}</span>
                    <span class="mc-bar-track" aria-hidden="true"
                      ><span
                        class="mc-bar-fill"
                        :style="{ width: `${(item.euro / itemMax) * 100}%` }"
                      ></span
                    ></span>
                    <span class="mc-bar-value"
                      >{{ euroText(item.euro) }}
                      <span class="mc-bar-sub">{{ minutesText(item.minutes) }}</span></span
                    >
                  </li>
                </ul>
              </div>
            </div>
          </section>
        </div>
        <p v-for="note in report.notes" :key="note" class="rp-privacy">{{ note }}</p>
        <p class="rp-privacy">
          Quelle: {{ report.source }}. Die Zahlen entstehen mit denselben Funktionen wie die
          MCP-Werkzeuge (Kennzahlen aus Summen, nie aus gemittelten Prozenten). Die Seite zeigt
          keine Personendaten.
        </p>
      </div>
    </template>
  </div>
</template>
