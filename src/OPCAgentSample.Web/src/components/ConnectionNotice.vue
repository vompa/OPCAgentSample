<script setup lang="ts">
import { computed } from 'vue';
import { useIdentityStore } from '@/stores/identity';
import { useLiveStore } from '@/stores/live';

const identity = useIdentityStore();
const live = useLiveStore();

/** Mobil gibt es keine Statusleiste: Eine Störung der Verbindung steht als eigener Hinweis unter dem Kopf. */
const text = computed(() => {
  const parts: string[] = [];
  if (identity.me && !identity.me.status.opcConnected) parts.push('OPC getrennt');
  if (live.connection === 'lost') parts.push('Live getrennt');
  else if (live.connection === 'paused') parts.push('Live pausiert');
  return parts.join(' · ');
});
</script>

<template>
  <div v-if="text" class="conn-notice" role="status">
    <span class="data-notice-dot" aria-hidden="true"></span>
    <span>{{ text }}</span>
  </div>
</template>
