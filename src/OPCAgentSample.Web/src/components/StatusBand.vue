<script setup lang="ts">
import { useIdentityStore } from '@/stores/identity';
import { useLiveStore } from '@/stores/live';

const identity = useIdentityStore();
const live = useLiveStore();
</script>

<template>
  <div class="status-band short" role="group" aria-label="Status">
    <template v-if="identity.me">
      <span class="band-opc"
        ><span class="band-dot"></span
        >{{ identity.me.status.opcConnected ? 'OPC' : 'OPC getrennt' }}</span
      >
      <span class="band-scope">{{ identity.me.status.scopeText }}</span>
      <span v-if="live.connection === 'lost'" class="band-live">Live getrennt</span>
      <span v-else-if="live.connection === 'paused'" class="band-live">Live pausiert</span>
      <span class="band-time">{{ identity.me.clock.localTime }}</span>
    </template>
  </div>
</template>
