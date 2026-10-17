<script setup lang="ts">
import { useConversationStore } from '@/stores/conversation';
import { useUiStore } from '@/stores/ui';
import IconSvg from './IconSvg.vue';
import RoleMenu from './RoleMenu.vue';

const ui = useUiStore();
const conversation = useConversationStore();

/** Eine leere Sitzung wird nicht doppelt angelegt: dann genügt der Wechsel zu „Fragen“. */
async function newSession(): Promise<void> {
  ui.showAsk();
  if (conversation.turns.length > 0 || conversation.pending) await conversation.startNewSession();
}
</script>

<template>
  <!-- Nur mobil: Seitenleiste, Rollenauswahl und neue Sitzung wie in der Claude-App (siehe mobile.css). -->
  <div class="m-bar short">
    <button
      id="sidebar-btn"
      type="button"
      class="m-bar-btn"
      aria-label="Sitzungen und Benutzer öffnen"
      aria-haspopup="dialog"
      aria-controls="m-sidebar"
      :aria-expanded="ui.sidebarOpen"
      @click="ui.sidebarOpen = true"
    >
      <IconSvg name="menu" :size="24" />
    </button>
    <RoleMenu />
    <button
      id="new-session-btn"
      type="button"
      class="m-bar-btn"
      aria-label="Neue Sitzung"
      @click="newSession"
    >
      <IconSvg name="compose" :size="22" />
    </button>
  </div>
</template>
