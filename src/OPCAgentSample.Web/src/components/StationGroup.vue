<script setup lang="ts">
import { computed } from 'vue';
import type { StationGroupView } from '@/api/types';
import { usePlantStore } from '@/stores/plant';
import { STATUS_WORD } from '@/utils/format';
import { inertAttrs } from '@/utils/inert';
import IconSvg from './IconSvg.vue';

const props = defineProps<{ group: StationGroupView }>();
const plant = usePlantStore();

const open = computed(() => !plant.collapsed[props.group.id]);
const listId = computed(() => `group-${props.group.id}`);
const itemWord = computed(() => {
  const single = props.group.stationCount === 1;
  if (props.group.id === 'hallen') return single ? 'Halle' : 'Hallen';
  return single ? 'Station' : 'Stationen';
});
const worstCount = computed(() =>
  props.group.worstStatus === 'problem' ? props.group.counts.problem : props.group.counts.warning,
);
</script>

<template>
  <div class="group">
    <button
      type="button"
      class="group-toggle"
      :aria-expanded="open"
      :aria-controls="listId"
      @click="plant.toggleGroup(group.id)"
    >
      <IconSvg name="chevron" :size="12" :stroke-width="3" />
      <span class="group-title">{{ group.title }}</span>
      <span class="group-count">{{ group.stationCount }} {{ itemWord }}</span>
      <span class="group-spacer"></span>
      <span
        v-if="group.worstStatus !== 'running'"
        :class="['group-worst', `status-${group.worstStatus}`]"
      >
        {{ worstCount }} {{ STATUS_WORD[group.worstStatus] }}
      </span>
    </button>
    <!-- Die Liste bleibt im Baum und klappt per Zeilenhöhe (grid) sanft ein; geschlossen ist sie inert und unsichtbar. -->
    <div :class="['station-collapse', { 'is-open': open }]">
      <ul :id="listId" class="station-list" v-bind="inertAttrs(!open)">
        <li
          v-for="station in group.stations"
          :key="station.id"
          :class="['station', `is-${station.status}`]"
          :title="station.message"
        >
          <div class="station-top">
            <span class="station-id">{{ station.id }}</span>
            <span :class="['pill', `pill-${station.status}`]">{{ station.statusWord }}</span>
            <span class="station-article" :title="station.article || undefined">{{
              station.article
            }}</span>
            <span
              v-if="station.status !== 'nodata'"
              class="station-kpi"
              :title="station.kpi.unit"
              >{{ station.kpi.value }}</span
            >
          </div>
          <div class="station-message">{{ station.message }}</div>
        </li>
      </ul>
    </div>
  </div>
</template>
