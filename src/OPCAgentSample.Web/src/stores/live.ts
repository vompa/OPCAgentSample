// Zustand des Live-Stroms und aktive Alarme.
import { defineStore } from 'pinia';
import { ref } from 'vue';
import type { AlarmSummary } from '@/api/types';
import type { LiveState } from '@/live/liveClient';

export const useLiveStore = defineStore('live', () => {
  const connection = ref<LiveState>('connecting');
  const alarms = ref<AlarmSummary | null>(null);
  return { connection, alarms };
});
