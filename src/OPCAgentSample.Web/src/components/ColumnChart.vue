<script setup lang="ts">
import { computed, useId } from 'vue';
import { useChartCursor } from '@/composables/useChartCursor';
import { formatNumber } from '@/utils/format';
import { niceMax } from '@/utils/reportFormat';

export interface Column {
  /** Kurze Beschriftung unter der Säule. */
  label: string;
  /** Ausführliche Angabe für Zeile und Tabelle („1–2 s“). */
  range: string;
  value: number;
}

const props = defineProps<{
  title: string;
  columns: Column[];
  /** Einheit der Werte im Plural („Fragen“). */
  unit: string;
  /** Erklärung der Achse unter dem Diagramm. */
  axisCaption: string;
}>();

const id = useId();
const count = computed(() => props.columns.length);
const top = computed(() => niceMax(Math.max(0, ...props.columns.map((c) => c.value))));
const total = computed(() => props.columns.reduce((sum, c) => sum + c.value, 0));
const peak = computed(() =>
  props.columns.reduce((best, c) => (c.value > best.value ? c : best), props.columns[0]!),
);
const peakIndex = computed(() => props.columns.indexOf(peak.value));
const { shown, select, clear, onKeydown } = useChartCursor(count, peakIndex);
const description = computed(
  () =>
    `${props.title}: ${formatNumber(total.value)} ${props.unit} in ${count.value} Bereichen, die meisten im Bereich ${peak.value.range}. Mit den Pfeiltasten einzelne Bereiche wählen.`,
);
</script>

<template>
  <figure class="rp-chart">
    <figcaption class="rp-chart-title">{{ title }}</figcaption>
    <p class="rp-readout" aria-live="off">
      <span class="rp-readout-label">{{ columns[shown]?.range }}</span>
      <span class="rp-readout-item"
        ><span class="rp-key tone-a" aria-hidden="true"></span
        ><strong>{{ formatNumber(columns[shown]?.value ?? 0) }}</strong> {{ unit }}</span
      >
    </p>
    <div
      class="rp-columns"
      role="group"
      tabindex="0"
      :aria-label="description"
      :aria-describedby="`${id}-table`"
      @pointerleave="clear"
      @focus="select(peakIndex)"
      @blur="clear"
      @keydown="onKeydown"
    >
      <div
        v-for="(column, index) in columns"
        :key="column.range"
        :class="['rp-column', { 'is-active': index === shown }]"
        @pointerenter="select(index)"
      >
        <div class="rp-column-track">
          <div class="rp-column-bar" :style="{ height: `${(column.value / top) * 100}%` }"></div>
        </div>
        <span class="rp-column-label" aria-hidden="true">{{ column.label }}</span>
      </div>
    </div>
    <p class="rp-axis-caption">{{ axisCaption }}</p>
    <details class="rp-table-toggle">
      <summary>Werte als Tabelle</summary>
      <div
        :id="`${id}-table`"
        class="rp-table-scroll"
        tabindex="0"
        role="region"
        :aria-label="`${title} als Tabelle`"
      >
        <table class="rp-table">
          <thead>
            <tr>
              <th scope="col">Bereich</th>
              <th scope="col" class="num">{{ unit }}</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="column in columns" :key="column.range">
              <th scope="row">{{ column.range }}</th>
              <td class="num">{{ formatNumber(column.value) }}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </details>
  </figure>
</template>
