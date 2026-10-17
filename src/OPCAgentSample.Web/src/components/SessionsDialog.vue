<script setup lang="ts">
import { ref, watch } from 'vue';
import type { ChatSessionSummary } from '@/api/types';
import { useConversationStore } from '@/stores/conversation';
import { useUiStore } from '@/stores/ui';
import { dateTimeText } from '@/utils/format';
import CloseButton from './CloseButton.vue';

const ui = useUiStore();
const conversation = useConversationStore();
const dialog = ref<HTMLDialogElement | null>(null);
const sessions = ref<ChatSessionSummary[] | null>(null);

watch(
  () => ui.sessionsOpen,
  async (open) => {
    const element = dialog.value;
    if (!element) return;
    if (!open) {
      if (element.open) element.close();
      return;
    }
    sessions.value = null;
    element.showModal();
    try {
      sessions.value = await conversation.listSessions();
    } catch {
      sessions.value = [];
    }
  },
);

function choose(id: string): void {
  ui.sessionsOpen = false;
  void conversation.openSession(id);
}

function onBackdrop(event: MouseEvent): void {
  if (event.target === dialog.value) ui.sessionsOpen = false; // Klick auf den Hintergrund
}
</script>

<template>
  <dialog
    id="sessions-dialog"
    ref="dialog"
    class="sessions-dialog"
    aria-labelledby="sessions-title"
    @close="ui.sessionsOpen = false"
    @click="onBackdrop"
  >
    <p v-if="sessions === null" class="empty-note">Sitzungen werden geladen …</p>
    <template v-else>
      <div class="panel-head is-centered">
        <h2 id="sessions-title" class="panel-title">Frühere Sitzungen</h2>
        <CloseButton @click="ui.sessionsOpen = false" />
      </div>
      <p v-if="sessions.length === 0" class="empty-note">
        Es gibt noch keine abgeschlossenen Sitzungen.
      </p>
      <ul v-else class="session-list">
        <li v-for="session in sessions" :key="session.id">
          <button
            type="button"
            class="session-btn"
            :aria-current="session.id === conversation.sessionId"
            @click="choose(session.id)"
          >
            <span class="session-title">
              {{ session.title }}
              <span v-if="session.id === conversation.sessionId" class="session-badge"
                >Aktuell</span
              >
            </span>
            <span class="session-meta">
              {{ dateTimeText(session.startedAtUtc) }} · {{ session.turnCount }}
              {{ session.turnCount === 1 ? 'Frage' : 'Fragen' }}
            </span>
          </button>
        </li>
      </ul>
    </template>
  </dialog>
</template>
