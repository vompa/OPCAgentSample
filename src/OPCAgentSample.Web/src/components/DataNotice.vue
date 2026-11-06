<script setup lang="ts">
import { computed } from 'vue';
import { useAppStore } from '@/stores/app';
import { useAuthStore } from '@/stores/auth';
import { useBackendStore } from '@/stores/backend';
import { useIdentityStore } from '@/stores/identity';

const LOADING_TEXT = 'Historie wird geladen … Kennzahlen sind bis dahin unvollständig.';
/** Start dauert ungewöhnlich lang oder ist gescheitert: Die Demo ist vermutlich pausiert (z. B. Kostengrenze erreicht). */
const SLOW_TEXT =
  'Die Halle startet ungewöhnlich lange. Die Demo ist möglicherweise gerade pausiert.';
const FAILED_TEXT = 'Die Halle antwortet nicht. Die Demo ist vermutlich gerade pausiert.';

const identity = useIdentityStore();
const backend = useBackendStore();
const auth = useAuthStore();
const app = useAppStore();

/** Vorschau, solange das Backend startet: Stand der gezeigten Daten, Hinweis auf den Start, bei Fristablauf ein Knopf. */
const previewText = computed(() => {
  if (backend.live) return null;
  const waking = backend.slow
    ? SLOW_TEXT
    : auth.starting
      ? 'Halle wird gestartet … Live-Daten folgen.'
      : null;
  if (backend.phase === 'failed') {
    return `Stand ${backend.stamp} · ${FAILED_TEXT}`;
  }
  if (backend.source === 'preview') {
    return `Vorschau · Stand ${backend.stamp} · ${waking ?? 'Live-Daten werden geladen …'}`;
  }
  if (backend.source === 'cached') {
    return `Stand ${backend.stamp} · ${waking ?? 'wird aktualisiert …'}`;
  }
  return null;
});

/** Vorschau oder Kaltstart oder Zeitbasis-Hinweis des Servers; sonst kein Banner. */
const notice = computed(() => {
  if (previewText.value) {
    const warning = backend.phase === 'failed' || backend.slow;
    return { kind: warning ? 'warning' : 'loading', text: previewText.value };
  }
  const status = identity.me?.status;
  if (!status) return null;
  if (status.historyLoading) return { kind: 'loading', text: LOADING_TEXT };
  if (status.dataWarning) return { kind: 'warning', text: status.dataWarning };
  return null;
});
</script>

<template>
  <div v-if="notice" :class="['data-notice', `is-${notice.kind}`]" role="status">
    <span class="data-notice-dot" aria-hidden="true"></span>
    <span>{{ notice.text }}</span>
    <button
      v-if="backend.phase === 'failed'"
      type="button"
      class="data-notice-action"
      @click="app.retryWake()"
    >
      Erneut versuchen
    </button>
  </div>
</template>
