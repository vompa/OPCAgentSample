<script setup lang="ts">
import type { AnswerChart } from '@/api/types';
import { formatNumber } from '@/utils/format';

const props = defineProps<{ chart: AnswerChart }>();

function widthOf(value: number): string {
  const percent = Math.max(0, Math.min(100, (value / props.chart.max) * 100));
  return `${percent.toFixed(1)}%`;
}
</script>

<template>
  <figure class="chart">
    <figcaption class="chart-title">{{ chart.title }}</figcaption>
    <div v-for="bar in chart.bars" :key="bar.label" class="bar-row">
      <span class="bar-label">{{ bar.label }}</span>
      <div class="bar-track" role="presentation">
        <div
          :class="['bar-fill', `bar-${bar.rating}`]"
          :style="{ width: widthOf(bar.value) }"
        ></div>
      </div>
      <span class="bar-value">{{ formatNumber(bar.value, chart.decimals) }}{{ chart.unit }}</span>
    </div>
  </figure>
</template>
