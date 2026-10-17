// Anlagenübersicht der Rolle (GET /api/overview, Live-Ereignis station).
import { defineStore } from 'pinia';
import { ref } from 'vue';
import type { OverviewResponse } from '@/api/types';

export const usePlantStore = defineStore('plant', () => {
  const overview = ref<OverviewResponse | null>(null);
  /** Eingeklappte Kategorien (Schlüssel = Gruppen-Id). */
  const collapsed = ref<Record<string, boolean>>({});

  function toggleGroup(id: string): void {
    collapsed.value = { ...collapsed.value, [id]: !collapsed.value[id] };
  }

  return { overview, collapsed, toggleGroup };
});
