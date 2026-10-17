<script setup lang="ts">
import { useAppStore } from '@/stores/app';
import { useIdentityStore } from '@/stores/identity';
import { useUiStore } from '@/stores/ui';
import MobileBar from './MobileBar.vue';

const identity = useIdentityStore();
const app = useAppStore();
const ui = useUiStore();

function select(id: string): void {
  if (id !== identity.role) void app.loadRole(id);
}
</script>

<template>
  <header class="app-header">
    <div class="brand">
      <div class="brand-logo" aria-hidden="true">
        <svg
          width="18"
          height="18"
          viewBox="0 0 24 24"
          fill="none"
          stroke="#FFFFFF"
          stroke-width="2"
        >
          <circle cx="12" cy="12" r="9" />
          <circle cx="12" cy="12" r="2.5" />
          <path d="M12 3v4M12 17v4M3 12h4M17 12h4" />
        </svg>
      </div>
      <div class="brand-text">
        <h1 class="brand-title">
          <span class="long">Schleifwerk · KI-Leitstand</span
          ><span class="short">KI-Leitstand</span>
        </h1>
        <p class="brand-sub long">OPC UA → MCP-Server → Claude</p>
      </div>
    </div>
    <div class="header-right">
      <div id="role-switch" class="role-switch" role="group" aria-label="Ansicht für">
        <button
          v-for="role in identity.me?.roles ?? []"
          :key="role.id"
          type="button"
          class="role-btn"
          :aria-pressed="role.id === identity.role"
          @click="select(role.id)"
        >
          <span class="long">{{ role.label }}</span
          ><span class="short">{{ role.shortLabel }}</span>
        </button>
      </div>
      <span class="sim-chip long">Simulierte Daten</span>
      <button type="button" class="header-about long" @click="ui.openAbout()">Über die Demo</button>
    </div>
    <MobileBar />
  </header>
</template>
