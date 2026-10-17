<script setup lang="ts">
import { computed } from 'vue';
import { usePlantStore } from '@/stores/plant';
import ClimateLine from './ClimateLine.vue';
import CounterBoxes from './CounterBoxes.vue';
import StationGroup from './StationGroup.vue';

const LEGEND_TITLE =
  'Der Prozentwert ist die Kennzahl der Gruppe (Ist/Soll bzw. Belegung). Die Ampel folgt allen Regeln, ' +
  'zum Beispiel Form, Taktzeit oder Material, und nicht allein dem Prozentwert; die Ursache steht unter der Station.';

const plant = usePlantStore();

/** Was der Prozentwert je Station bedeutet, aus den Kennzahl-Beschriftungen der Gruppen. */
const legend = computed(() => {
  const labels = [...new Set((plant.overview?.groups ?? []).map((group) => group.kpiLabel))];
  return labels.length > 0 ? `% = ${labels.join(' bzw. ')} · Ampel: alle Regeln` : '';
});
</script>

<template>
  <aside id="plant" class="col-left" aria-label="Anlagen">
    <template v-if="plant.overview">
      <div class="plant-head">
        <h2 class="plant-title">Anlagen · {{ plant.overview.title }}</h2>
        <span class="plant-live">{{ plant.overview.liveText }}</span>
      </div>
      <ClimateLine v-if="plant.overview.climate" :climate="plant.overview.climate" />
      <div class="counters" role="group" aria-label="Zähler">
        <CounterBoxes :counts="plant.overview.counts" variant="counter" />
      </div>
      <p v-if="legend" class="plant-legend" :title="LEGEND_TITLE">{{ legend }}</p>
      <StationGroup v-for="group in plant.overview.groups" :key="group.id" :group="group" />
    </template>
  </aside>
</template>
