<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue';
import { useFollowScroll } from '@/composables/useFollowScroll';
import { useIsMobile } from '@/composables/useIsMobile';
import { useConversationStore } from '@/stores/conversation';
import { useFavoritesStore } from '@/stores/favorites';
import { useIdentityStore } from '@/stores/identity';
import { timeText } from '@/utils/format';
import AnswerCardView from './AnswerCardView.vue';
import KeepIds from './KeepIds.vue';

const conversation = useConversationStore();
const favorites = useFavoritesStore();
const identity = useIdentityStore();
const container = ref<HTMLElement | null>(null);
const mobile = useIsMobile();
const follow = useFollowScroll(container);

/** Weniger Bewegung gewünscht: dann springt der Verlauf, statt zu gleiten. */
function prefersReducedMotion(): boolean {
  return globalThis.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;
}

/**
 * Richtet den Anfang der letzten Frage am oberen Rand des Verlaufs aus (Frage und Kopf der Antwort sind sofort
 * lesbar, Rest darunter). `scroll-margin-top` der Turns hält den Innenabstand. Beim ersten Laden springt der Verlauf,
 * bei einer neuen Frage gleitet er.
 */
function alignLatestTurn(smooth: boolean): void {
  const behavior: ScrollBehavior = smooth && !prefersReducedMotion() ? 'smooth' : 'auto';
  container.value?.lastElementChild?.scrollIntoView({ block: 'start', behavior });
}

watch(
  () =>
    [
      conversation.turns.length,
      !!conversation.pending,
      conversation.pending?.error,
      conversation.sessionId,
      conversation.pending?.toolCalls.length,
      !!conversation.pending?.card,
      !!conversation.pending?.draft,
    ] as const,
  async (
    [turns, pending, error, sessionId],
    [previousTurns, previousPending, previousError, previousSessionId],
  ) => {
    await nextTick();
    // Gleiten nur, wenn in derselben Sitzung eine Frage oder Antwort hinzukommt; Laden und Wechsel springen.
    const sameSession = sessionId === previousSessionId;
    const grew = sameSession && (turns > previousTurns || (pending && !previousPending));
    if (mobile.value) {
      // Handy: neue Frage oder anderer Verlauf richten neu aus, weitere Schritte der Antwort laufen mit.
      follow.follow(grew || !sameSession || (turns !== previousTurns && !pending));
    } else if (
      turns !== previousTurns ||
      pending !== previousPending ||
      error !== previousError ||
      !sameSession
    ) {
      alignLatestTurn(grew);
    }
  },
);

onMounted(() => {
  if (mobile.value) follow.follow(true);
  else alignLatestTurn(false);
});

const isEmpty = computed(() => conversation.turns.length === 0 && !conversation.pending);
const greeting = computed(() => {
  const me = identity.me;
  return me ? `${me.clock.greeting}, ${me.user.firstName}.` : '';
});
const greetingSub = computed(() => {
  const me = identity.me;
  return me ? `${me.status.scopeText} · ${me.clock.localTime}` : '';
});

/** Der Titel zeigt schon die ganze Frage? Dann braucht die Karte keine zweite Zeile. */
function repeatsTitle(title: string, question: string): boolean {
  const plain = (text: string) => text.trim().replace(/[?.!…]+$/, '');
  return plain(question) === plain(title);
}

function retry(question: string): void {
  conversation.pending = null;
  void conversation.ask(question);
}
</script>

<template>
  <div
    id="conv"
    ref="container"
    class="conv"
    role="log"
    aria-live="polite"
    aria-label="Verlauf"
    tabindex="-1"
    @scroll.passive="follow.onScroll"
  >
    <div v-if="conversation.dividerText" class="session-divider">
      {{ conversation.dividerText }}
    </div>
    <div v-for="turn in conversation.turns" :key="turn.sequence" class="turn">
      <div class="q-line">
        <span class="q-prompt" aria-hidden="true">❯</span>
        <span class="q-text">{{ turn.question }}</span>
        <span class="q-who"
          >{{ identity.me?.user.firstName ?? '' }} · {{ timeText(turn.dataAsOfUtc) }}</span
        >
      </div>
      <AnswerCardView :turn="turn" />
    </div>
    <div v-if="conversation.pending" class="turn is-pending">
      <div class="q-line">
        <span class="q-prompt" aria-hidden="true">❯</span>
        <span class="q-text">{{ conversation.pending.question }}</span>
        <span class="q-who">
          {{ identity.me?.user.firstName ?? '' }} · {{ identity.me?.clock.localTime ?? '' }}
        </span>
      </div>
      <article
        class="card is-pending"
        :class="{ 'has-draft': conversation.pending.draft && !conversation.pending.error }"
        :aria-busy="!conversation.pending.error"
      >
        <p v-if="conversation.pending.error" class="error-text" role="alert">
          Die Anfrage ist fehlgeschlagen: {{ conversation.pending.error }}
        </p>
        <div v-else-if="conversation.pending.queued" class="pending-list">
          <span class="pending-line"
            ><span class="pending-dot" aria-hidden="true"></span>Wird gesendet, sobald die Halle
            bereit ist …</span
          >
        </div>
        <template v-else>
          <!-- Entwurf: Überschrift und Was tun? stehen, sobald Claude sie geschrieben hat; der Rest folgt mit der geprüften Karte. -->
          <template v-if="conversation.pending.draft">
            <div class="card-meta"><span class="chip chip-claude">Claude</span></div>
            <p class="card-headline"><KeepIds :text="conversation.pending.draft.headline" /></p>
            <div v-if="conversation.pending.draft.todo.length > 0" class="todo">
              <span class="todo-label">Was tun?</span>
              <span
                v-for="(item, index) in conversation.pending.draft.todo"
                :key="index"
                class="todo-text"
                ><KeepIds :text="item"
              /></span>
            </div>
            <div class="draft-rest" aria-hidden="true">
              <span class="draft-bar"></span>
              <span class="draft-bar is-short"></span>
            </div>
          </template>
          <div class="pending-list">
            <span class="pending-line"
              ><span class="pending-dot" aria-hidden="true"></span
              >{{
                conversation.pending.draft
                  ? 'Claude schreibt Werte und Begründung …'
                  : 'Claude fragt den MCP-Server …'
              }}</span
            >
            <span v-for="(call, index) in conversation.pending.toolCalls" :key="index">
              <span class="pending-tool">{{ call.tool }}</span> · {{ call.durationMs ?? '–' }} ms
            </span>
          </div>
        </template>
        <button
          v-if="conversation.pending.error"
          type="button"
          class="requery-btn"
          @click="retry(conversation.pending.question)"
        >
          Erneut versuchen
        </button>
      </article>
    </div>
    <p v-if="isEmpty" class="empty-note long">
      Noch keine Fragen in dieser Sitzung. Wähle einen Favoriten oder stelle eine Frage.
    </p>
    <!-- Nur mobil: Begrüßung und Favoriten als Vorschläge, solange die Sitzung leer ist. -->
    <div v-if="isEmpty && identity.me" class="m-empty short">
      <div class="m-empty-head">
        <h2 class="m-empty-title">{{ greeting }}</h2>
        <p class="m-empty-sub">{{ greetingSub }}</p>
      </div>
      <ul v-if="favorites.favorites" class="m-suggest-list" aria-label="Vorschläge">
        <li v-for="item in favorites.favorites.items" :key="item.question">
          <button type="button" class="m-suggest" @click="conversation.askFrom(item.question)">
            <span class="m-suggest-title">{{ item.shortTitle }}</span>
            <span v-if="!repeatsTitle(item.shortTitle, item.question)" class="m-suggest-question">{{
              item.question
            }}</span>
          </button>
        </li>
      </ul>
    </div>
  </div>
</template>
