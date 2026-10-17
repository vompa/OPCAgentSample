// Postfach der Rolle (GET /api/inbox, Übernehmen).
import { defineStore } from 'pinia';
import { computed, ref } from 'vue';
import { api } from '@/api/client';
import type { InboxItem } from '@/api/types';
import { useBackendStore } from './backend';
import { useIdentityStore } from './identity';
import { useUiStore } from './ui';

export const useInboxStore = defineStore('inbox', () => {
  const items = ref<InboxItem[]>([]);
  const openItems = computed(() => items.value.filter((item) => item.takenBy == null));
  const openCount = computed(() => openItems.value.length);
  const hasUrgent = computed(() => openItems.value.some((item) => item.level === 'crit'));

  async function take(id: string): Promise<void> {
    const identity = useIdentityStore();
    if (!useBackendStore().ready) {
      useUiStore().toast('Übernehmen ist möglich, sobald die Halle bereit ist.');
      return;
    }
    try {
      const item = await api.takeInboxItem(identity.role, id);
      items.value = items.value.map((entry) => (entry.id === id ? item : entry));
      identity.me = await api.me(identity.role);
    } catch (error) {
      useUiStore().toast(
        `Übernehmen fehlgeschlagen: ${error instanceof Error ? error.message : String(error)}`,
      );
    }
  }

  return { items, openItems, openCount, hasUrgent, take };
});
