// Gewählte Rolle und der Benutzerkontext (GET /api/me, inklusive Simulationsuhr).
import { defineStore } from 'pinia';
import { ref } from 'vue';
import type { ClockView, MeResponse } from '@/api/types';
import { platform } from '@/platform';

const ROLE_KEY = 'opc.role';
export const DEFAULT_ROLE = 'shift_lead';

export const useIdentityStore = defineStore('identity', () => {
  const role = ref(DEFAULT_ROLE);
  const me = ref<MeResponse | null>(null);

  async function init(): Promise<void> {
    role.value = (await platform.storage.get(ROLE_KEY)) ?? DEFAULT_ROLE;
  }

  function setRole(next: string): void {
    role.value = next;
    void platform.storage.set(ROLE_KEY, next);
  }

  function setClock(clock: ClockView): void {
    if (me.value) me.value = { ...me.value, clock };
  }

  return { role, me, init, setRole, setClock };
});
