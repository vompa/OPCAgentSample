<script setup lang="ts">
import { computed, useId } from 'vue';
import type { MachinePareto } from '@/api/types';
import { formatNumber } from '@/utils/format';
import { euroText, minutesText } from '@/utils/machineFormat';

const props = defineProps<{ title: string; items: MachinePareto[]; showEuro: boolean }>();

const id = useId();
const longest = computed(() => Math.max(1, ...props.items.map((item) => item.minutes)));
const totalMinutes = computed(() => props.items.reduce((sum, item) => sum + item.minutes, 0));
const description = computed(
  () =>
    `${props.title}: ${props.items.length} Gründe, zusammen ${minutesText(totalMinutes.value)}. Größter Grund: ${props.items[0]?.reason ?? 'keiner'}.`,
);
</script>

<template>
  <figure class="rp-chart mc-pareto">
    <figcaption class="visually-hidden">{{ title }}</figcaption>
    <p v-if="items.length === 0" class="empty-note">Kein ungeplanter Stillstand im Zeitraum.</p>
    <template v-else>
      <ul class="mc-bars" role="img" :aria-label="description" :aria-describedby="`${id}-table`">
        <li v-for="item in items" :key="item.reason" class="mc-bar-row">
          <span class="mc-bar-label">{{ item.reason }}</span>
          <span class="mc-bar-track" aria-hidden="true"
            ><span
              class="mc-bar-fill"
              :style="{ width: `${(item.minutes / longest) * 100}%` }"
            ></span
          ></span>
          <span class="mc-bar-value"
            >{{ minutesText(item.minutes) }}
            <span class="mc-bar-sub"
              >{{ item.count }}×<template v-if="showEuro">
                · {{ euroText(item.euro) }}</template
              ></span
            >
          </span>
        </li>
      </ul>
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
                <th scope="col">Grund</th>
                <th scope="col" class="num">Dauer (min)</th>
                <th scope="col" class="num">Anzahl</th>
                <th v-if="showEuro" scope="col" class="num">Kosten</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="item in items" :key="item.reason">
                <th scope="row">{{ item.reason }}</th>
                <td class="num" data-label="Dauer (min)">{{ formatNumber(item.minutes, 1) }}</td>
                <td class="num" data-label="Anzahl">{{ item.count }}</td>
                <td v-if="showEuro" class="num" data-label="Kosten">{{ euroText(item.euro) }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </details>
    </template>
  </figure>
</template>
