<script setup lang="ts">
import { computed } from 'vue';
import type { StatusCounts, StationStatus } from '@/api/types';

const props = defineProps<{ counts: StatusCounts; variant: 'counter' | 'm-counter' }>();

const ENTRIES: readonly [Exclude<StationStatus, 'nodata'>, string][] = [
  ['problem', 'Problem'],
  ['warning', 'Warnung'],
  ['running', 'Läuft'],
];

type CounterEntry = readonly [status: StationStatus, label: string, count: number];

/** „Keine Daten“ erscheint nur, wenn es solche Stationen gibt, und zählt nie zu „Läuft“. */
const entries = computed<CounterEntry[]>(() => {
  const shown: CounterEntry[] = ENTRIES.map(([status, label]) => [
    status,
    label,
    props.counts[status],
  ]);
  const noData = props.counts.noData ?? 0;
  return noData > 0 ? [...shown, ['nodata', 'Keine Daten', noData]] : shown;
});
</script>

<template>
  <div
    v-for="[status, label, count] in entries"
    :key="status"
    :class="[variant, `status-${status}`]"
  >
    <span class="counter-n">{{ count }}</span
    ><span class="counter-label">{{ label }}</span>
  </div>
</template>
