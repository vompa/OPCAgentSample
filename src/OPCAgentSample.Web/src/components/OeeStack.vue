<script setup lang="ts">
import { computed, useId } from 'vue';
import type { MachineOee } from '@/api/types';
import { formatNumber } from '@/utils/format';
import { lossSegments } from '@/utils/machineFormat';

const props = defineProps<{ oee: MachineOee; title: string }>();

const id = useId();
const segments = computed(() => lossSegments(props.oee));
const description = computed(
  () =>
    `${props.title}: OEE ${props.oee.oeePercent} Prozent. Verfügbarkeit ${props.oee.availabilityPercent}, Leistung ${props.oee.performancePercent}, Qualität ${props.oee.qualityPercent} Prozent. Die Verluste der drei Faktoren und die OEE ergeben zusammen 100 Prozent der Planbelegungszeit.`,
);
</script>

<template>
  <figure class="rp-chart mc-oee">
    <figcaption class="visually-hidden">{{ title }}</figcaption>
    <div class="mc-stack" role="img" :aria-label="description" :aria-describedby="`${id}-table`">
      <span
        v-for="segment in segments"
        :key="segment.key"
        :class="['mc-seg', `is-${segment.key}`]"
        :style="{ flexGrow: Math.max(segment.percent, 0.5) }"
        >{{ segment.percent >= 8 ? formatNumber(segment.percent, 0) : '' }}</span
      >
    </div>
    <ul class="mc-stack-key" aria-label="Legende">
      <li v-for="segment in segments" :key="segment.key">
        <span :class="['mc-swatch', `is-${segment.key}`]" aria-hidden="true"></span
        >{{ segment.label }} <strong>{{ formatNumber(segment.percent, 1) }} %</strong>
      </li>
    </ul>
    <dl class="mc-factors">
      <div>
        <dt>Verfügbarkeit</dt>
        <dd>{{ oee.availabilityPercent }} %</dd>
      </div>
      <div>
        <dt>Leistung</dt>
        <dd>{{ oee.performancePercent }} %</dd>
      </div>
      <div>
        <dt>Qualität</dt>
        <dd>{{ oee.qualityPercent }} %</dd>
      </div>
    </dl>
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
              <th scope="col">Anteil an der Planbelegungszeit</th>
              <th scope="col" class="num">Wert</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="segment in segments" :key="segment.key">
              <th scope="row">{{ segment.label }}</th>
              <td class="num">{{ formatNumber(segment.percent, 1) }} %</td>
            </tr>
          </tbody>
        </table>
      </div>
    </details>
  </figure>
</template>
