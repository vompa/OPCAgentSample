<script setup lang="ts">
import { computed, onBeforeUnmount, watch } from 'vue';
import type { AnswerChart as AnswerChartData } from '@/api/types';
import AnswerChart from '@/components/AnswerChart.vue';
import ColumnChart from '@/components/ColumnChart.vue';
import IconSvg from '@/components/IconSvg.vue';
import LineChart from '@/components/LineChart.vue';
import MachinesView from '@/components/MachinesView.vue';
import { useMachinesStore } from '@/stores/machines';
import { REFRESH_MS, useReportsStore } from '@/stores/reports';
import { useUiStore, type ReportsTab } from '@/stores/ui';
import { dateTimeText, formatNumber, timeText } from '@/utils/format';
import {
  OUTCOME_NAMES,
  ROLE_NAMES,
  bucketLowerLabel,
  bucketRangeText,
  compactNumber,
  durationText,
  lagText,
  percentText,
  usdText,
} from '@/utils/reportFormat';

const reports = useReportsStore();
const machines = useMachinesStore();
const ui = useUiStore();

/** Nur wenn die Seite sichtbar ist (Desktop: Reports-Modus, Handy: Reiter), wird geladen und aktualisiert. */
const visible = computed(() => ui.deskReports || ui.mobileView === 'reports');

watch(
  () => visible.value && ui.reportsTab === 'usage',
  (isVisible) => {
    if (isVisible) reports.startRefreshing();
    else reports.stopRefreshing();
  },
  { immediate: true },
);

const TABS: { id: ReportsTab; label: string }[] = [
  { id: 'machines', label: 'Maschinen' },
  { id: 'usage', label: 'KI-Nutzung' },
];

/** Pfeiltasten wechseln den Reiter (Muster „Tabs“: Auswahl folgt dem Fokus). */
function onTabKeydown(event: KeyboardEvent): void {
  const step = event.key === 'ArrowRight' ? 1 : event.key === 'ArrowLeft' ? -1 : 0;
  if (step === 0) return;
  event.preventDefault();
  const index = TABS.findIndex((tab) => tab.id === ui.reportsTab);
  const next = TABS[(index + step + TABS.length) % TABS.length]!;
  ui.reportsTab = next.id;
  document.getElementById(`reports-tab-${next.id}`)?.focus();
}

function refresh(): void {
  if (ui.reportsTab === 'usage') void reports.load();
  else void machines.load();
}
onBeforeUnmount(() => reports.stopRefreshing());

const summary = computed(() => reports.summary);
const hasQuestions = computed(() => (summary.value?.questions.total ?? 0) > 0);

const hourLabels = computed(() => {
  const series = summary.value?.timeSeries;
  if (!series) return [];
  const start = new Date(series.startUtc).getTime();
  return series.questions.map((_, index) =>
    timeText(new Date(start + index * series.intervalMinutes * 60_000).toISOString()),
  );
});

const columns = computed(() =>
  (summary.value?.latencyHistogram ?? []).map((bucket) => ({
    label: bucketLowerLabel(bucket),
    range: bucketRangeText(bucket),
    value: bucket.count,
  })),
);

const toolCalls = computed<AnswerChartData | null>(() => {
  const totals = new Map<string, number>();
  for (const row of summary.value?.tools ?? [])
    totals.set(row.tool, (totals.get(row.tool) ?? 0) + row.calls);
  if (totals.size === 0) return null;
  const bars = [...totals]
    .sort((a, b) => b[1] - a[1])
    .map(([label, value]) => ({ label, value, rating: 'info' as const }));
  return {
    title: 'Aufrufe je Werkzeug',
    max: Math.max(...bars.map((b) => b.value)),
    unit: '',
    decimals: 0,
    bars,
  };
});

const updatedText = computed(() =>
  summary.value ? timeText(summary.value.period.generatedAtUtc) : '',
);
const sinceText = computed(() => {
  const first = summary.value?.period.firstQuestionAtUtc;
  return first ? `seit ${dateTimeText(first)}` : 'noch keine Fragen';
});
const refreshSeconds = REFRESH_MS / 1000;

function roleName(role: string): string {
  return ROLE_NAMES[role] ?? role;
}
</script>

<template>
  <section id="mview-reports" class="mview reports-view" aria-labelledby="reports-title">
    <header class="reports-head">
      <button type="button" class="btn reports-back" @click="ui.toggleDeskReports()">
        <IconSvg name="chevron" :size="14" :stroke-width="3" />Zurück zum Leitstand
      </button>
      <div class="reports-title-block">
        <h2 id="reports-title" class="m-page-title">Reports</h2>
        <p v-if="ui.reportsTab === 'machines'" class="panel-sub">
          Kennzahlen der Maschinen in Halle A: OEE, Stillstand, Ausschuss, Prozesswerte, Kosten
        </p>
        <p v-else class="panel-sub">
          Nutzung und Betrieb der Demo, über alle Besucher
          <template v-if="summary">· {{ sinceText }} · Stand {{ updatedText }} Uhr</template>
        </p>
      </div>
      <div class="reports-actions">
        <button type="button" class="btn" @click="refresh()">Aktualisieren</button>
      </div>
    </header>

    <div class="reports-tabs" role="tablist" aria-label="Report">
      <button
        v-for="tab in TABS"
        :id="`reports-tab-${tab.id}`"
        :key="tab.id"
        type="button"
        role="tab"
        :class="['reports-tab', { 'is-active': ui.reportsTab === tab.id }]"
        :aria-selected="ui.reportsTab === tab.id"
        :aria-controls="`reports-panel-${tab.id}`"
        :tabindex="ui.reportsTab === tab.id ? 0 : -1"
        @click="ui.reportsTab = tab.id"
        @keydown="onTabKeydown"
      >
        {{ tab.label }}
      </button>
    </div>

    <div
      v-if="ui.reportsTab === 'machines'"
      id="reports-panel-machines"
      class="reports-panel"
      role="tabpanel"
      aria-labelledby="reports-tab-machines"
    >
      <MachinesView />
    </div>

    <div
      v-else
      id="reports-panel-usage"
      class="reports-panel"
      role="tabpanel"
      aria-labelledby="reports-tab-usage"
    >
      <p
        v-if="reports.status === 'idle' || reports.status === 'loading'"
        class="empty-note"
        role="status"
      >
        Reports werden geladen …
      </p>

      <div v-else-if="reports.status === 'error'" class="reports-empty" role="alert">
        <p>Die Reports konnten nicht geladen werden: {{ reports.errorText }}</p>
        <button type="button" class="btn" @click="reports.load()">Erneut versuchen</button>
      </div>

      <template v-else-if="summary">
        <p v-if="reports.stale" class="reports-stale" role="status">
          Die Aktualisierung ist fehlgeschlagen ({{ reports.errorText }}). Angezeigt werden die
          zuletzt geladenen Werte.
        </p>

        <p v-if="reports.sample" class="reports-stale" role="status">
          Vorschau mit Beispielwerten. Die echten Zahlen erscheinen, sobald die Halle bereit ist.
        </p>

        <p v-if="!hasQuestions" class="reports-empty reports-first" role="status">
          <strong>Noch keine Fragen gestellt.</strong>
          Sobald im Verlauf eine Frage beantwortet wurde, erscheinen hier Antwortzeiten, Verlauf und
          Werkzeug-Aufrufe. Die Seite aktualisiert sich alle {{ refreshSeconds }} Sekunden.
        </p>

        <p v-if="hasQuestions && summary.tokens.total === 0" class="reports-note" role="note">
          Noch keine Tokens verbraucht: Claude hat noch keine Frage beantwortet (ohne API-Key
          antwortet die Demo nur mit vorbereiteten Antworten). Tokens, Kosten und Cache-Quote
          bleiben bis dahin leer.
        </p>

        <section class="rp-tiles" aria-label="Kennzahlen">
          <div class="rp-tile rp-hero">
            <span class="rp-tile-label">Fragen gesamt</span>
            <span class="rp-tile-value rp-hero-value">{{
              formatNumber(summary.questions.total)
            }}</span>
            <span class="rp-tile-note">
              heute {{ formatNumber(summary.questions.today) }} · letzte 24 h
              {{ formatNumber(summary.questions.last24Hours) }}
            </span>
          </div>
          <div class="rp-tile">
            <span class="rp-tile-label">Tokens</span>
            <span class="rp-tile-value">{{ compactNumber(summary.tokens.total) }}</span>
            <span class="rp-tile-note">
              Eingabe {{ compactNumber(summary.tokens.input) }} · Ausgabe
              {{ compactNumber(summary.tokens.output) }}
            </span>
          </div>
          <div class="rp-tile">
            <span class="rp-tile-label">Geschätzte Kosten</span>
            <span class="rp-tile-value">{{ usdText(summary.cost.estimatedUsd) }}</span>
            <span class="rp-tile-note">Schätzung aus den Tokens, nicht die Rechnung</span>
          </div>
          <div class="rp-tile">
            <span class="rp-tile-label">Cache-Trefferquote</span>
            <span class="rp-tile-value">{{ percentText(summary.cost.cacheHitRate) }}</span>
            <span class="rp-tile-note">
              gelesen {{ compactNumber(summary.tokens.cacheRead) }} · geschrieben
              {{ compactNumber(summary.tokens.cacheWrite) }}
            </span>
          </div>
          <div class="rp-tile">
            <span class="rp-tile-label">Ø Antwortzeit</span>
            <span class="rp-tile-value">{{ durationText(summary.latency.averageMs) }}</span>
            <span class="rp-tile-note">
              p50 {{ durationText(summary.latency.p50Ms) }} · p95
              {{ durationText(summary.latency.p95Ms) }} ·
              {{ formatNumber(summary.latency.measured) }}
              gemessen
            </span>
          </div>
          <div class="rp-tile">
            <span class="rp-tile-label">Kontingent-Ablehnungen</span>
            <span class="rp-tile-value">{{
              formatNumber(summary.ingestion.quotaDenialsSinceStart)
            }}</span>
            <span class="rp-tile-note">seit Serverstart</span>
          </div>
        </section>

        <div class="rp-grid-2">
          <section class="rp-card" aria-labelledby="rp-latency-title">
            <h3 id="rp-latency-title" class="panel-title">Antwortzeiten</h3>
            <p v-if="summary.latency.measured === 0" class="empty-note">
              Noch keine Antwortzeit erfasst.
            </p>
            <ColumnChart
              v-else
              title="Verteilung der Antwortzeiten"
              :columns="columns"
              unit="Fragen"
              axis-caption="Untergrenze des Bereichs in Sekunden"
            />
          </section>

          <section class="rp-card" aria-labelledby="rp-series-title">
            <h3 id="rp-series-title" class="panel-title">
              Verlauf der letzten {{ summary.period.seriesHours }} Stunden
            </h3>
            <p v-if="!hasQuestions" class="empty-note">Noch keine Fragen im Zeitraum.</p>
            <template v-else>
              <LineChart
                title="Fragen je Stunde"
                unit="Fragen"
                :labels="hourLabels"
                :series="[{ name: 'Fragen', values: summary.timeSeries.questions, tone: 'a' }]"
              />
              <LineChart
                v-if="summary.tokens.total > 0"
                title="Tokens je Stunde"
                unit="Tokens"
                :labels="hourLabels"
                :series="[
                  { name: 'Eingabe', values: summary.timeSeries.inputTokens, tone: 'a' },
                  { name: 'Ausgabe', values: summary.timeSeries.outputTokens, tone: 'b' },
                ]"
              />
            </template>
          </section>
        </div>

        <div class="rp-grid-2">
          <section class="rp-card" aria-labelledby="rp-tools-title">
            <h3 id="rp-tools-title" class="panel-title">MCP-Werkzeuge</h3>
            <p v-if="!toolCalls" class="empty-note">Noch keine Werkzeug-Aufrufe.</p>
            <div v-else class="rp-tools">
              <AnswerChart :chart="toolCalls" />
              <!-- Dieselben Zahlen wie die Balken, ergänzt um Rolle, Ergebnis und Dauer: zugeklappt, damit nichts doppelt steht. -->
              <details class="rp-table-toggle">
                <summary>Aufrufe nach Rolle und Ergebnis als Tabelle</summary>
                <div
                  class="rp-table-scroll"
                  tabindex="0"
                  role="region"
                  aria-label="Werkzeug-Aufrufe je Rolle und Ergebnis"
                >
                  <table class="rp-table">
                    <thead>
                      <tr>
                        <th scope="col">Werkzeug</th>
                        <th scope="col">Rolle</th>
                        <th scope="col">Ergebnis</th>
                        <th scope="col" class="num">Aufrufe</th>
                        <th scope="col" class="num">Ø Dauer</th>
                        <th scope="col" class="num">p95</th>
                      </tr>
                    </thead>
                    <tbody>
                      <tr
                        v-for="row in summary.tools"
                        :key="`${row.tool}|${row.role}|${row.outcome}`"
                      >
                        <th scope="row" class="rp-mono">{{ row.tool }}</th>
                        <td data-label="Rolle">{{ roleName(row.role) }}</td>
                        <td data-label="Ergebnis">
                          <span :class="['rp-outcome', `is-${row.outcome}`]">{{
                            OUTCOME_NAMES[row.outcome] ?? row.outcome
                          }}</span>
                        </td>
                        <td class="num" data-label="Aufrufe">{{ formatNumber(row.calls) }}</td>
                        <td class="num" data-label="Ø Dauer">{{ durationText(row.averageMs) }}</td>
                        <td class="num" data-label="p95">{{ durationText(row.p95Ms) }}</td>
                      </tr>
                    </tbody>
                  </table>
                </div>
              </details>
            </div>
          </section>

          <section class="rp-card" aria-labelledby="rp-ingest-title">
            <h3 id="rp-ingest-title" class="panel-title">Datenaufnahme</h3>
            <dl class="rp-facts">
              <div class="rp-fact">
                <dt>Collector-Rückstand</dt>
                <dd>{{ lagText(summary.ingestion.lagSeconds) }}</dd>
                <dd class="rp-tile-note">Simulationsuhr minus Wasserstand</dd>
              </div>
              <div class="rp-fact">
                <dt>Simulationsuhr</dt>
                <dd>{{ dateTimeText(summary.ingestion.simulationClockUtc) }}</dd>
              </div>
              <div class="rp-fact">
                <dt>Wasserstand</dt>
                <dd>
                  {{
                    summary.ingestion.watermarkUtc
                      ? dateTimeText(summary.ingestion.watermarkUtc)
                      : '–'
                  }}
                </dd>
              </div>
              <div class="rp-fact">
                <dt>Aufgenommene Datensätze</dt>
                <dd>{{ compactNumber(summary.ingestion.ingestedRecordsSinceStart) }}</dd>
                <dd class="rp-tile-note">seit Serverstart</dd>
              </div>
              <div class="rp-fact">
                <dt>Live-Verbindungen</dt>
                <dd>{{ formatNumber(summary.ingestion.liveConnections) }}</dd>
              </div>
            </dl>
          </section>
        </div>

        <p class="rp-privacy">
          Die Seite zeigt nur Zähler und Zeiten. Frage- und Antworttexte, Werkzeug-Parameter und
          Besucherkennungen sind nicht enthalten. Die Kosten rechnen mit
          {{ formatNumber(summary.cost.prices.inputPerMillion, 2) }} $ (Eingabe),
          {{ formatNumber(summary.cost.prices.outputPerMillion, 2) }} $ (Ausgabe),
          {{ formatNumber(summary.cost.prices.cacheReadPerMillion, 2) }} $ (Cache gelesen) und
          {{ formatNumber(summary.cost.prices.cacheWritePerMillion, 3) }} $ (Cache geschrieben) je 1
          Mio. Tokens.
        </p>
      </template>
    </div>
  </section>
</template>
