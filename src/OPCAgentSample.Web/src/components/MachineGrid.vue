<script setup lang="ts">
import type { MachineStation } from '@/api/types';
import RatingMark from '@/components/RatingMark.vue';
import { formatNumber } from '@/utils/format';
import { deltaOf, euroText, minutesText, percentValue, RATING_WORDS } from '@/utils/machineFormat';

defineProps<{
  stations: MachineStation[];
  selectedId: string | null;
  compare: boolean;
  previousLabel: string | null;
  showCosts: boolean;
}>();

const emit = defineEmits<{ select: [id: string] }>();

/** Hauptkennzahl der Kachel: OEE, bei Öfen die Belegung. */
function mainValue(station: MachineStation): number | null {
  return station.type === 'oven' ? station.occupancyPercent : (station.oee?.oeePercent ?? null);
}

function mainDelta(station: MachineStation) {
  const previous =
    station.type === 'oven' ? station.previous?.occupancyPercent : station.previous?.oeePercent;
  return deltaOf(mainValue(station), previous, true);
}

function ariaLabel(station: MachineStation): string {
  const label = station.type === 'oven' ? 'Belegung' : 'OEE';
  const value = mainValue(station);
  return `${station.id}, ${station.name}: ${label} ${value === null ? 'ohne Daten' : `${value} Prozent`}, ${RATING_WORDS[station.rating]}. Details öffnen.`;
}
</script>

<template>
  <ul class="mc-grid" aria-label="Stationen der Halle A">
    <li v-for="station in stations" :key="station.id">
      <button
        type="button"
        :class="['mc-tile', `is-${station.rating}`, { 'is-selected': station.id === selectedId }]"
        :aria-current="station.id === selectedId ? 'true' : undefined"
        :aria-label="ariaLabel(station)"
        @click="emit('select', station.id)"
      >
        <span class="mc-tile-head">
          <span class="mc-tile-id">{{ station.id }}</span>
          <span class="mc-tile-name">{{ station.name }}</span>
          <RatingMark :rating="station.rating" />
        </span>
        <span class="mc-tile-main">
          <span class="mc-tile-label">{{ station.type === 'oven' ? 'Belegung' : 'OEE' }}</span>
          <span class="mc-tile-value">{{ percentValue(mainValue(station)) }}</span>
          <template v-if="compare && mainDelta(station)">
            <span
              :class="[
                'mc-delta',
                `is-${mainDelta(station)!.direction}`,
                {
                  'is-better': mainDelta(station)!.better === true,
                  'is-worse': mainDelta(station)!.better === false,
                },
              ]"
              ><span class="mc-arrow" aria-hidden="true"></span>{{ mainDelta(station)!.text }}
              <span class="visually-hidden">gegenüber {{ previousLabel }}</span></span
            >
          </template>
        </span>
        <dl class="mc-tile-facts">
          <div v-if="station.type !== 'oven'">
            <dt>Ist / Soll</dt>
            <dd>
              {{ formatNumber(station.actual) }} / {{ formatNumber(station.planned) }}
              <span v-if="station.planPercent !== null" class="mc-muted"
                >({{ station.planPercent }} %)</span
              >
            </dd>
          </div>
          <div v-if="station.type !== 'oven'">
            <dt>Ausschuss</dt>
            <dd>
              {{ percentValue(station.scrapPercent, 1) }}
              <span v-if="compare && station.previous?.scrapPercent != null" class="mc-muted"
                >(vorher {{ percentValue(station.previous.scrapPercent, 1) }})</span
              >
            </dd>
          </div>
          <div>
            <dt>Stillstand</dt>
            <dd>
              {{ minutesText(station.downtimeMinutes) }}
              <span v-if="compare && station.previous" class="mc-muted"
                >({{ percentValue(station.downtimeSharePercent, 1) }} statt
                {{ percentValue(station.previous.downtimeSharePercent, 1) }})</span
              >
            </dd>
          </div>
          <div v-if="showCosts">
            <dt>Verlust</dt>
            <dd>{{ euroText(station.downtimeEuro + station.scrapEuro) }}</dd>
          </div>
        </dl>
      </button>
    </li>
  </ul>
</template>
