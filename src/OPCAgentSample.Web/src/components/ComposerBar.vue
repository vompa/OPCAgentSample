<script setup lang="ts">
import { computed, nextTick, ref } from 'vue';
import { useHistoryNavigation } from '@/composables/useHistoryNavigation';
import { useIsMobile } from '@/composables/useIsMobile';
import { platform } from '@/platform';
import type { SpeechSession } from '@/platform/speech';
import { useConversationStore } from '@/stores/conversation';
import { useFavoritesStore } from '@/stores/favorites';
import { useUiStore } from '@/stores/ui';
import IconSvg from './IconSvg.vue';

const conversation = useConversationStore();
const favorites = useFavoritesStore();
const ui = useUiStore();

const PLACEHOLDER = 'Frage an Claude, z. B. Welche Wartungen stehen an?';
/** Mobil ist das Feld schmal: ein kurzer Text, der nicht abgeschnitten wird. */
const PLACEHOLDER_COMPACT = 'Frage an Claude stellen';
const RECENT_LIMIT = 5;

const text = ref('');
const input = ref<HTMLInputElement | null>(null);
const history = useHistoryNavigation(text, () => conversation.recentQuestions);
const speechSupported = platform.speech.supported;
const listening = ref(false);
let speech: SpeechSession | null = null;

const mobile = useIsMobile();
const placeholder = computed(() => (mobile.value ? PLACEHOLDER_COMPACT : PLACEHOLDER));
const hasText = computed(() => text.value.trim().length > 0);
/** Mobil: Mikrofon und Senden tauschen den Platz; ohne Spracheingabe bleibt „Senden“ immer sichtbar. */
const showSend = computed(() => hasText.value || !speechSupported);

const recentOpen = ref(false);
/** Die laufende oder zuletzt gestellte Frage steht schon im Verlauf und gehört nicht in „Zuletzt gefragt“. */
const currentQuestion = computed(
  () => conversation.pending?.question ?? conversation.turns.at(-1)?.question ?? null,
);
const olderQuestions = computed(() =>
  conversation.recentQuestions.filter((q) => q !== currentQuestion.value).slice(0, RECENT_LIMIT),
);

function submit(): void {
  const question = text.value;
  if (!question.trim() || conversation.busy) return;
  text.value = '';
  history.reset();
  // Mobil schließt die Tastatur, damit die Antwort sichtbar wird.
  if (mobile.value) input.value?.blur();
  void conversation.ask(question);
}

function askRecent(question: string): void {
  recentOpen.value = false;
  void conversation.ask(question);
}

async function onKeydown(event: KeyboardEvent): Promise<void> {
  if (!history.onKeydown(event.key)) return;
  event.preventDefault();
  await nextTick();
  const length = text.value.length;
  input.value?.setSelectionRange(length, length);
}

function pin(): void {
  if (favorites.add('', text.value)) {
    text.value = '';
    ui.toast('Als Favorit gespeichert.');
  }
}

function toggleVoice(): void {
  if (speech) {
    speech.stop();
    return;
  }
  listening.value = true;
  speech = platform.speech.start({
    onResult: (result) => {
      text.value = result;
      input.value?.focus();
    },
    onError: () => ui.toast('Die Spracheingabe war nicht möglich.'),
    onEnd: () => {
      speech = null;
      listening.value = false;
    },
  });
}
</script>

<template>
  <div id="composer-wrap" class="composer-wrap">
    <form id="composer" class="composer" autocomplete="off" @submit.prevent="submit">
      <span class="composer-prompt" aria-hidden="true">❯</span>
      <label class="visually-hidden" for="question-input">Frage an Claude</label>
      <input
        id="question-input"
        ref="input"
        v-model="text"
        class="composer-input"
        type="text"
        maxlength="500"
        enterkeyhint="send"
        :placeholder="placeholder"
        @input="history.onInput"
        @keydown="onKeydown"
        @focus="ui.composerFocused = true"
        @blur="ui.composerFocused = false"
      />
      <button
        v-if="speechSupported"
        id="mic-btn"
        type="button"
        :class="['icon-btn', 'mic-btn', { 'is-listening': listening, 'is-swapped': hasText }]"
        :aria-label="listening ? 'Spracheingabe beenden' : 'Spracheingabe'"
        @mousedown.prevent
        @click="toggleVoice"
      >
        <IconSvg name="mic" :size="18" />
      </button>
      <button
        id="pin-btn"
        type="button"
        class="icon-btn pin-btn"
        aria-label="Als Favorit speichern"
        title="Als Favorit speichern"
        @click="pin"
      >
        <IconSvg name="pin" :size="16" />
      </button>
      <button
        id="send-btn"
        type="submit"
        :class="['send-btn', { 'is-idle': !showSend }]"
        :disabled="conversation.busy"
        :aria-busy="conversation.busy"
        @mousedown.prevent
      >
        <IconSvg class="send-icon" name="send" :size="22" :stroke-width="2.4" /><span
          class="send-label"
          >{{ conversation.busy ? 'Läuft …' : 'Senden' }}</span
        >
      </button>
    </form>
    <div class="composer-hint">
      <span class="hint-text"
        >Enter sendet · Stecknadel speichert die Frage als Favorit (max. 10)</span
      >
      <button
        v-if="olderQuestions.length > 0"
        type="button"
        class="recent-toggle"
        :aria-expanded="recentOpen"
        aria-controls="recent"
        @click="recentOpen = !recentOpen"
      >
        <IconSvg name="chevron" :size="14" :stroke-width="3" />Zuletzt gefragt
        <span class="recent-count">{{ olderQuestions.length }}</span>
      </button>
      <span class="session-actions">
        <button type="button" class="link-btn" @click="conversation.startNewSession()">
          <IconSvg name="plus" :size="14" /><span class="link-label">Neue Sitzung</span>
        </button>
        <button type="button" class="link-btn" @click="ui.sessionsOpen = true">
          <IconSvg name="history" :size="14" /><span class="link-label">Sitzungen</span>
        </button>
      </span>
    </div>
    <section id="recent" class="recent" aria-label="Zuletzt gefragt">
      <template v-if="recentOpen">
        <button
          v-for="question in olderQuestions"
          :key="question"
          type="button"
          class="recent-btn"
          @click="askRecent(question)"
        >
          {{ question }}
        </button>
      </template>
    </section>
  </div>
</template>
