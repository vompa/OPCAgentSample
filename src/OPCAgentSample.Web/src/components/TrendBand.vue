<script setup lang="ts">
import { computed, useId } from 'vue';
import type { MachineTrend } from '@/api/types';
import RatingMark from '@/components/RatingMark.vue';
import { useChartCursor } from '@/composables/useChartCursor';
import { formatNumber } from '@/utils/format';

const props = defineProps<{ trend: MachineTrend }>();

const id = useId();
const count = computed(() => props.trend.values.length);
const { shown, select, clear, onKeydown } = useChartCursor(count);

// Zeichenfläche 0–100 in beiden Richtungen, mit Rand, damit Linie und Punkte nicht abgeschnitten werden.
const LEFT = 2;
const WIDTH = 96;
const TOP = 6;
const HEIGHT = 88;

const domain = computed(() => {
  const { values, low, high } = props.trend;
  const min = Math.min(...values, low ?? Infinity);
  const max = Math.max(...values, high ?? -Infinity);
  const pad = (max - min) * 0.12 || 1;
  return { min: min - pad, max: max + pad };
});

function xOf(index: number): number {
  return count.value <= 1 ? 50 : LEFT + (index / (count.value - 1)) * WIDTH;
}

function yOf(value: number): number {
  const { min, max } = domain.value;
  return TOP + HEIGHT - ((value - min) / (max - min)) * HEIGHT;
}

const line = computed(() =>
  props.trend.values.map((value, index) => `${xOf(index)},${yOf(value)}`).join(' '),
);
const band = computed(() => {
  const top = yOf(props.trend.high ?? domain.value.max);
  const bottom = yOf(props.trend.low ?? domain.value.min);
  return { y: top, height: bottom - top };
});

function isOutside(value: number): boolean {
  const { low, high } = props.trend;
  return (low !== null && value < low) || (high !== null && value > high);
}

const outsideIndexes = computed(() =>
  props.trend.values.flatMap((value, index) => (isOutside(value) ? [index] : [])),
);

const ticks = computed(() => {
  const { low, high, target } = props.trend;
  const list: { value: number; text: string }[] = [];
  if (high !== null) list.push({ value: high, text: formatNumber(high, props.trend.decimals) });
  if (target !== null && target !== low && target !== high)
    list.push({ value: target, text: formatNumber(target, props.trend.decimals) });
  if (low !== null) list.push({ value: low, text: formatNumber(low, props.trend.decimals) });
  return list.map((tick) => ({ ...tick, y: yOf(tick.value) }));
});

const axisLabels = computed(() => {
  const last = count.value - 1;
  return last < 0
    ? []
    : [0, Math.floor(last / 2), last].map((index) => props.trend.labels[index] ?? '');
});

const bandText = computed(() => {
  const { low, high, unit, decimals } = props.trend;
  const f = (value: number): string => formatNumber(value, decimals);
  if (low !== null && high !== null) return `Toleranzband ${f(low)} bis ${f(high)} ${unit}`;
  if (low !== null) return `Untergrenze ${f(low)} ${unit}`;
  return high !== null ? `Obergrenze ${f(high)} ${unit}` : '';
});

function valueText(value: number): string {
  return `${formatNumber(value, props.trend.decimals)} ${props.trend.unit}`;
}

function onPointer(event: PointerEvent): void {
  const target = event.currentTarget as HTMLElement;
  const rect = target.getBoundingClientRect();
  if (rect.width === 0) return;
  const percent = ((event.clientX - rect.left) / rect.width) * 100;
  select(Math.round(((percent - LEFT) / WIDTH) * (count.value - 1)));
}

const description = computed(
  () =>
    `${props.trend.label}: ${count.value} Werte von ${props.trend.labels[0] ?? ''} bis ${props.trend.labels[count.value - 1] ?? ''}. ${bandText.value}. ${formatNumber(props.trend.outsidePercent ?? 0, 1)} Prozent der ${props.trend.basis} außerhalb. Mit den Pfeiltasten einzelne Werte wählen.`,
);
</script>

<template>
  <figure class="rp-chart mc-trend">
    <figcaption class="rp-chart-title">{{ trend.label }}</figcaption>
    <p class="mc-trend-summary">
      <RatingMark :rating="trend.rating" />
      <span
        ><strong>{{ formatNumber(trend.outsidePercent ?? 0, 1) }} %</strong> der
        {{ trend.basis }} außerhalb · {{ bandText }}</span
      >
    </p>
    <p class="rp-readout" aria-live="off">
      <span class="rp-readout-label">{{ trend.labels[shown] }}</span>
      <span class="rp-readout-item"
        ><strong>{{ valueText(trend.values[shown] ?? 0) }}</strong>
        <span v-if="isOutside(trend.values[shown] ?? 0)" class="mc-outside"
          >außerhalb des Bands</span
        ></span
      >
    </p>
    <div class="rp-plot-wrap">
      <ul class="rp-yaxis" aria-hidden="true">
        <li v-for="tick in ticks" :key="tick.value" :style="{ top: `${tick.y}%` }">
          {{ tick.text }}
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
          <rect class="mc-band" :x="LEFT" :y="band.y" :width="WIDTH" :height="band.height" />
          <line
            v-if="trend.target !== null"
            class="mc-target"
            :x1="LEFT"
            :x2="LEFT + WIDTH"
            :y1="yOf(trend.target)"
            :y2="yOf(trend.target)"
          />
          <polyline class="rp-line" :points="line" />
          <line class="rp-cursor" :x1="xOf(shown)" :x2="xOf(shown)" :y1="TOP" :y2="TOP + HEIGHT" />
        </svg>
        <span
          v-for="index in outsideIndexes"
          :key="index"
          class="mc-flag"
          :style="{ left: `${xOf(index)}%`, top: `${yOf(trend.values[index]!)}%` }"
          aria-hidden="true"
        ></span>
        <span
          class="rp-dot"
          :style="{ left: `${xOf(shown)}%`, top: `${yOf(trend.values[shown] ?? 0)}%` }"
          aria-hidden="true"
        ></span>
      </div>
    </div>
    <div class="rp-xaxis" aria-hidden="true">
      <span v-for="(label, index) in axisLabels" :key="index">{{ label }}</span>
    </div>
    <p class="mc-trend-key" aria-hidden="true">
      <span class="mc-key-band"></span>Toleranzband
      <span v-if="trend.target !== null" class="mc-key-target"></span
      ><template v-if="trend.target !== null"
        >Soll {{ formatNumber(trend.target, trend.decimals) }}</template
      >
      <span class="mc-key-flag"></span>außerhalb
    </p>
    <details class="rp-table-toggle">
      <summary>Werte als Tabelle</summary>
      <div
        :id="`${id}-table`"
        class="rp-table-scroll"
        tabindex="0"
        role="region"
        :aria-label="`${trend.label} als Tabelle`"
      >
        <table class="rp-table">
          <thead>
            <tr>
              <th scope="col">Zeit</th>
              <th scope="col" class="num">{{ trend.unit }}</th>
              <th scope="col">Lage</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="(label, index) in trend.labels" :key="index">
              <th scope="row">{{ label }}</th>
              <td class="num" data-label="Wert">
                {{ formatNumber(trend.values[index] ?? 0, trend.decimals) }}
              </td>
              <td data-label="Lage">
                {{ isOutside(trend.values[index] ?? 0) ? 'außerhalb' : 'innerhalb' }}
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </details>
  </figure>
</template>
