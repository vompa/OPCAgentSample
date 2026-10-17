<script setup lang="ts">
import { computed, useId } from 'vue';
import { useChartCursor } from '@/composables/useChartCursor';
import { formatNumber } from '@/utils/format';
import { compactNumber, niceMax } from '@/utils/reportFormat';

/** Eine Linie; `tone` wählt die Farbe (a = Blau, b = Orange), der Name steht immer auch in Legende und Tabelle. */
export interface LineSeries {
  name: string;
  values: number[];
  tone: 'a' | 'b';
}

const props = defineProps<{
  title: string;
  /** Beschriftung je Punkt (z. B. „14:00“). */
  labels: string[];
  series: LineSeries[];
  /** Einheit für die Beschreibung („Fragen“). */
  unit: string;
}>();

const id = useId();
const count = computed(() => props.labels.length);
const { shown, select, clear, onKeydown } = useChartCursor(count);

const top = computed(() => niceMax(Math.max(0, ...props.series.flatMap((s) => s.values))));

// Zeichenfläche 0–100 in beiden Richtungen, mit Rand, damit Linien und Punkte nicht abgeschnitten werden.
const LEFT = 2;
const WIDTH = 96;
const TOP = 4;
const HEIGHT = 92;

function xOf(index: number): number {
  return count.value <= 1 ? 50 : LEFT + (index / (count.value - 1)) * WIDTH;
}

function yOf(value: number): number {
  return TOP + HEIGHT - (value / top.value) * HEIGHT;
}

function pointsOf(values: number[]): string {
  return values.map((value, index) => `${xOf(index)},${yOf(value)}`).join(' ');
}

function areaOf(values: number[]): string {
  return `${xOf(0)},${yOf(0)} ${pointsOf(values)} ${xOf(values.length - 1)},${yOf(0)}`;
}

const gridLines = computed(() =>
  [0, 0.5, 1].map((fraction) => ({ fraction, y: yOf(fraction * top.value) })),
);
const axisLabels = computed(() => {
  const last = count.value - 1;
  return last < 0 ? [] : [0, Math.floor(last / 2), last].map((index) => props.labels[index] ?? '');
});

function onPointer(event: PointerEvent): void {
  const target = event.currentTarget as HTMLElement;
  const rect = target.getBoundingClientRect();
  if (rect.width === 0) return;
  const percent = ((event.clientX - rect.left) / rect.width) * 100;
  select(Math.round(((percent - LEFT) / WIDTH) * (count.value - 1)));
}

const total = computed(() =>
  props.series
    .map((s) => `${s.name} ${compactNumber(s.values.reduce((a, b) => a + b, 0))}`)
    .join(', '),
);
const description = computed(
  () =>
    `${props.title}: ${props.unit}, ${count.value} Werte von ${props.labels[0] ?? ''} bis ${props.labels[count.value - 1] ?? ''}. Summe: ${total.value}. Mit den Pfeiltasten einzelne Werte wählen.`,
);
</script>

<template>
  <figure class="rp-chart">
    <figcaption class="rp-chart-title">{{ title }}</figcaption>
    <ul v-if="series.length > 1" class="rp-legend" aria-label="Legende">
      <li v-for="line in series" :key="line.name">
        <span :class="['rp-key', `tone-${line.tone}`]" aria-hidden="true"></span>{{ line.name }}
      </li>
    </ul>
    <p class="rp-readout" aria-live="off">
      <span class="rp-readout-label">{{ labels[shown] }}</span>
      <span v-for="line in series" :key="line.name" class="rp-readout-item">
        <span :class="['rp-key', `tone-${line.tone}`]" aria-hidden="true"></span
        >{{ series.length > 1 ? `${line.name} ` : ''
        }}<strong>{{ formatNumber(line.values[shown] ?? 0) }}</strong>
      </span>
    </p>
    <div class="rp-plot-wrap">
      <ul class="rp-yaxis" aria-hidden="true">
        <li v-for="line in gridLines" :key="line.fraction" :style="{ top: `${line.y}%` }">
          {{ compactNumber(line.fraction * top) }}
        </li>
      </ul>
      <div
        class="rp-plot"
        role="group"
        tabindex="0"
        :aria-label="description"
        :aria-describedby="`${id}-table`"
        @pointermove="onPointer"
        @pointerleave="clear"
        @focus="select(count - 1)"
        @blur="clear"
        @keydown="onKeydown"
      >
        <svg
          class="rp-svg"
          viewBox="0 0 100 100"
          preserveAspectRatio="none"
          aria-hidden="true"
          focusable="false"
        >
          <line
            v-for="line in gridLines"
            :key="line.fraction"
            class="rp-grid"
            :x1="LEFT"
            :x2="LEFT + WIDTH"
            :y1="line.y"
            :y2="line.y"
          />
          <polygon
            v-if="series.length === 1"
            :class="['rp-area', `tone-${series[0]!.tone}`]"
            :points="areaOf(series[0]!.values)"
          />
          <polyline
            v-for="line in series"
            :key="line.name"
            :class="['rp-line', `tone-${line.tone}`]"
            :points="pointsOf(line.values)"
          />
          <line class="rp-cursor" :x1="xOf(shown)" :x2="xOf(shown)" :y1="TOP" :y2="TOP + HEIGHT" />
        </svg>
        <span
          v-for="line in series"
          :key="line.name"
          :class="['rp-dot', `tone-${line.tone}`]"
          :style="{ left: `${xOf(shown)}%`, top: `${yOf(line.values[shown] ?? 0)}%` }"
          aria-hidden="true"
        ></span>
      </div>
    </div>
    <div class="rp-xaxis" aria-hidden="true">
      <span v-for="(label, index) in axisLabels" :key="index">{{ label }}</span>
    </div>
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
              <th scope="col">Zeit</th>
              <th v-for="line in series" :key="line.name" scope="col" class="num">
                {{ line.name }}
              </th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="(label, index) in labels" :key="index">
              <th scope="row">{{ label }}</th>
              <td v-for="line in series" :key="line.name" class="num">
                {{ formatNumber(line.values[index] ?? 0) }}
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </details>
  </figure>
</template>
