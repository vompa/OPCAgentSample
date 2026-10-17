<script setup lang="ts">
import { computed } from 'vue';
import type { MachineShift } from '@/api/types';
import { formatNumber } from '@/utils/format';
import { minutesText } from '@/utils/machineFormat';

const props = defineProps<{ shifts: MachineShift[]; basis: string }>();

const measured = computed(() => props.shifts.filter((s) => s.oee !== null));
const description = computed(
  () =>
    `Schichtvergleich der OEE: ${measured.value.map((s) => `${s.shift} ${s.oee!.oeePercent} Prozent`).join(', ')}.`,
);

function minutesOrDash(value: number | null): string {
  return value === null ? '–' : minutesText(value);
}
</script>

<template>
  <figure class="rp-chart mc-shifts">
    <figcaption class="rp-chart-title">OEE nach Schicht und Anlaufverluste</figcaption>
    <p class="rp-tile-note">{{ basis }}</p>
    <p v-if="measured.length === 0" class="empty-note">
      Noch keine Schicht im Zeitraum abgeschlossen.
    </p>
    <div
      v-else
      class="rp-table-scroll mc-shift-scroll"
      tabindex="0"
      role="region"
      aria-label="Schichtvergleich als Tabelle"
    >
      <table class="rp-table mc-shift-table" :aria-description="description">
        <thead>
          <tr>
            <th scope="col">Schicht</th>
            <th scope="col" class="num">OEE</th>
            <th scope="col" class="num">Verfügbarkeit</th>
            <th scope="col" class="num">Leistung</th>
            <th scope="col" class="num">Stillstand</th>
            <th scope="col" class="num">Anlaufverlust nach Wechsel</th>
            <th scope="col" class="num">MTTR</th>
            <th scope="col" class="num">Reaktionszeit Instandhaltung</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="shift in shifts" :key="shift.shift">
            <th scope="row">{{ shift.shift }}</th>
            <td class="num mc-oee-cell" data-label="OEE">
              <template v-if="shift.oee">
                <span class="mc-hbar" aria-hidden="true"
                  ><span class="mc-hbar-fill" :style="{ width: `${shift.oee.oeePercent}%` }"></span
                ></span>
                <strong>{{ shift.oee.oeePercent }} %</strong>
              </template>
              <template v-else>–</template>
            </td>
            <td class="num" data-label="Verfügbarkeit">
              {{ shift.oee ? `${shift.oee.availabilityPercent} %` : '–' }}
            </td>
            <td class="num" data-label="Leistung">
              {{ shift.oee ? `${shift.oee.performancePercent} %` : '–' }}
            </td>
            <td class="num" data-label="Stillstand">
              {{ shift.oee ? minutesText(shift.downtimeMinutes) : '–' }}
            </td>
            <td class="num" data-label="Anlaufverlust">
              <template v-if="shift.startupLossCount > 0"
                >{{ formatNumber(shift.startupLossPieces) }} Stück ·
                {{ minutesText(shift.startupLossMinutes) }}</template
              >
              <template v-else>–</template>
            </td>
            <td class="num" data-label="MTTR">{{ minutesOrDash(shift.mttrMinutes) }}</td>
            <td class="num" data-label="Reaktionszeit">
              {{ minutesOrDash(shift.reactionMinutes) }}
            </td>
          </tr>
        </tbody>
      </table>
    </div>
    <p class="rp-tile-note">
      Anlaufverlust: 15 bis 20 min nach jedem Schichtwechsel läuft jede Station langsamer. Die
      Reaktionszeit der Instandhaltung wird nur nachts (Bereitschaft) erfasst.
    </p>
  </figure>
</template>
