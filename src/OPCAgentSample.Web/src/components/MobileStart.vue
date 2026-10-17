<script setup lang="ts">
import { computed } from 'vue';
import { useConversationStore } from '@/stores/conversation';
import { useIdentityStore } from '@/stores/identity';
import { useInboxStore } from '@/stores/inbox';
import { usePlantStore } from '@/stores/plant';
import { useUiStore } from '@/stores/ui';
import ClimateLine from './ClimateLine.vue';
import CounterBoxes from './CounterBoxes.vue';
import StatusBand from './StatusBand.vue';

const identity = useIdentityStore();
const plant = usePlantStore();
const inbox = useInboxStore();
const ui = useUiStore();
const conversation = useConversationStore();

const urgent = computed(() => inbox.openItems.slice(0, 2));

function askStation(id: string): void {
  void conversation.askFrom(`Wie ist der Zustand von ${id}?`);
}
</script>

<template>
  <div id="mview-start" class="mview" aria-label="Übersicht">
    <template v-if="identity.me && plant.overview">
      <div>
        <div class="m-greeting">
          {{ identity.me.clock.greeting }}, {{ identity.me.user.firstName }}
        </div>
        <div class="m-greeting-sub">{{ identity.me.user.mobileTitle }}</div>
        <StatusBand />
      </div>
      <ClimateLine
        v-if="plant.overview.climate"
        :climate="plant.overview.climate"
        variant="m-climate"
      />
      <div class="m-counters" role="group" aria-label="Zähler">
        <CounterBoxes :counts="plant.overview.counts" variant="m-counter" />
      </div>
      <div class="m-section-head">
        <h2 class="m-section-title">Jetzt zu tun</h2>
        <button type="button" class="m-link" @click="ui.setMobileView('inbox')">
          Postfach ({{ inbox.openCount }})
        </button>
      </div>
      <template v-if="urgent.length > 0">
        <div v-for="item in urgent" :key="item.id" class="m-todo">
          <div class="inbox-item-top">
            <span :class="['badge', item.level === 'crit' ? 'badge-crit' : 'badge-warn']">{{
              item.priorityText
            }}</span>
            <span class="inbox-due">{{ item.due }}</span>
          </div>
          <div class="m-todo-title">{{ item.title }}</div>
          <button
            v-if="item.question"
            type="button"
            class="m-btn"
            @click="conversation.askFrom(item.question)"
          >
            Mit Claude analysieren
          </button>
        </div>
      </template>
      <p v-else class="empty-note">Nichts offen.</p>
      <div v-for="group in plant.overview.groups" :key="group.id">
        <div class="m-section-head">
          <h2 class="m-section-title">{{ group.title }}</h2>
          <span class="m-section-kpi">{{ group.kpiLabel }}</span>
        </div>
        <ul class="m-stations">
          <li v-for="station in group.stations" :key="station.id">
            <!-- Mit Daten: Tippen fragt Claude nach der Station; ohne Daten gibt es nichts zu fragen. -->
            <component
              :is="station.status === 'nodata' ? 'div' : 'button'"
              :type="station.status === 'nodata' ? undefined : 'button'"
              class="m-station"
              @click="station.status !== 'nodata' && askStation(station.id)"
            >
              <span :class="['pill', `pill-${station.status}`]">{{ station.statusWord }}</span>
              <span class="m-station-body">
                <span class="m-station-id">{{ station.id }}</span>
                <span class="m-station-note">{{ station.message }}</span>
              </span>
              <span v-if="station.status !== 'nodata'" class="m-station-kpi">{{
                station.kpi.value
              }}</span>
            </component>
          </li>
        </ul>
      </div>
    </template>
  </div>
</template>
