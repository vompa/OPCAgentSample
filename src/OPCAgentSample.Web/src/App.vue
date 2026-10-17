<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, watch, watchEffect } from 'vue';
import AboutView from '@/components/AboutView.vue';
import AppHeader from '@/components/AppHeader.vue';
import ComposerBar from '@/components/ComposerBar.vue';
import ConnectionNotice from '@/components/ConnectionNotice.vue';
import ConversationView from '@/components/ConversationView.vue';
import DataNotice from '@/components/DataNotice.vue';
import DeskPanel from '@/components/DeskPanel.vue';
import FavoritesBand from '@/components/FavoritesBand.vue';
import MobileFeed from '@/components/MobileFeed.vue';
import MobileInbox from '@/components/MobileInbox.vue';
import MobileNav from '@/components/MobileNav.vue';
import MobileSidebar from '@/components/MobileSidebar.vue';
import MobileStart from '@/components/MobileStart.vue';
import PlantColumn from '@/components/PlantColumn.vue';
import ReportsView from '@/components/ReportsView.vue';
import SessionsDialog from '@/components/SessionsDialog.vue';
import StatusBar from '@/components/StatusBar.vue';
import ToastHost from '@/components/ToastHost.vue';
import TracePanel from '@/components/TracePanel.vue';
import { useScrollMemory } from '@/composables/useScrollMemory';
import { useViewportHeight } from '@/composables/useViewportHeight';
import { useAppStore } from '@/stores/app';
import { useAuthStore } from '@/stores/auth';
import { useUiStore } from '@/stores/ui';

const app = useAppStore();
const auth = useAuthStore();
const ui = useUiStore();

/** Die Erklärseite ersetzt die Anmeldung; angemeldet öffnet „Über die Demo“ sie über der weiterlaufenden Anwendung. */
const showAbout = computed(() => auth.needsLogin || (auth.ready && ui.aboutOpen));

// Die mobilen Ansichten schaltet das CSS über body[data-mview] um.
watchEffect(() => {
  document.body.dataset.mview = ui.mobileView;
});
// Eingabefeld im Fokus (Tastatur offen): mobil weichen Kopf und Navigation, siehe mobile.css.
watchEffect(() => {
  document.body.classList.toggle('kb-open', ui.composerFocused);
});
useViewportHeight();
useScrollMemory();

function onEscape(event: KeyboardEvent): void {
  if (event.key === 'Escape' && ui.panel) ui.closePanel(true);
}

// Die Anwendung lädt erst, wenn die Anmeldung geklärt ist (Demo aus oder angemeldet); Abmelden und eine abgelaufene
// Sitzung beenden den Live-Strom und zeigen wieder die Anmeldeansicht.
watch(
  () => auth.ready,
  (ready, wasReady) => {
    if (ready) void app.init();
    else if (wasReady) {
      app.reset();
      ui.closeAbout();
    }
  },
);

onMounted(() => {
  document.addEventListener('keydown', onEscape);
  void auth.check();
});
onBeforeUnmount(() => {
  document.removeEventListener('keydown', onEscape);
  app.stopLive();
});
</script>

<template>
  <AboutView v-if="showAbout" />
  <template v-if="auth.checked && !auth.needsLogin">
    <a v-show="!showAbout" class="skip-link" href="#conv">Zum Verlauf springen</a>
    <div v-show="!showAbout" id="app-root" :class="['app', { 'is-reports': ui.deskReports }]">
      <AppHeader />
      <DataNotice />
      <ConnectionNotice />

      <div class="app-body">
        <PlantColumn />

        <main id="main" class="col-main" aria-label="Prompts und Antworten">
          <FavoritesBand />
          <ConversationView />
          <ComposerBar />
        </main>

        <TracePanel />

        <MobileStart />
        <MobileInbox />
        <MobileFeed />
        <ReportsView />
      </div>

      <div class="statusbar-wrap">
        <DeskPanel />
        <StatusBar />
      </div>

      <MobileNav />
    </div>

    <SessionsDialog />
    <MobileSidebar />
    <ToastHost />
  </template>
</template>
