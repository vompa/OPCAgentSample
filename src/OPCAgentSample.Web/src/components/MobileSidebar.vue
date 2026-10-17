<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue';
import type { ChatSessionSummary } from '@/api/types';
import { useIsMobile } from '@/composables/useIsMobile';
import { useAuthStore } from '@/stores/auth';
import { useConversationStore } from '@/stores/conversation';
import { useIdentityStore } from '@/stores/identity';
import { useUiStore } from '@/stores/ui';
import { dateText, dayKey, timeText } from '@/utils/format';
import CloseButton from './CloseButton.vue';
import IconSvg from './IconSvg.vue';
import StatusBand from './StatusBand.vue';

/** Ab dieser Strecke nach links (oder schneller) schließt das Wischen die Seitenleiste. */
const SWIPE_DISTANCE = 72;
const SWIPE_VELOCITY = 0.4; // Pixel je Millisekunde
/** Ein schneller Wisch schließt erst ab dieser Strecke (sonst zählt jedes Zucken). */
const FLICK_MIN_DISTANCE = 24;
const DAY_MS = 86_400_000;

interface SessionGroup {
  key: string;
  label: string;
  sessions: ChatSessionSummary[];
}

const ui = useUiStore();
const conversation = useConversationStore();
const identity = useIdentityStore();
const auth = useAuthStore();
const mobile = useIsMobile();

const dialog = ref<HTMLDialogElement | null>(null);
const sessions = ref<ChatSessionSummary[] | null>(null);
const failed = ref(false);
let opener: HTMLElement | null = null;

/** „Heute“ und „Gestern“ gelten nach der Uhr des Geräts; die Sitzungszeiten sind Serverzeiten, keine Simulationszeit. */
const groups = computed<SessionGroup[]>(() => {
  const now = Date.now();
  const today = dayKey(new Date(now).toISOString());
  const yesterday = dayKey(new Date(now - DAY_MS).toISOString());
  const result: SessionGroup[] = [];
  const ordered = [...(sessions.value ?? [])].sort((a, b) =>
    b.lastActivityUtc.localeCompare(a.lastActivityUtc),
  );
  for (const session of ordered) {
    const key = dayKey(session.lastActivityUtc);
    let group = result.find((entry) => entry.key === key);
    if (!group) {
      const label =
        key === today ? 'Heute' : key === yesterday ? 'Gestern' : dateText(session.lastActivityUtc);
      group = { key, label, sessions: [] };
      result.push(group);
    }
    group.sessions.push(session);
  }
  return result;
});

async function loadSessions(): Promise<void> {
  failed.value = false;
  try {
    sessions.value = await conversation.listSessions();
  } catch {
    failed.value = true;
    sessions.value ??= [];
  }
}

watch(
  () => ui.sidebarOpen,
  async (open) => {
    const element = dialog.value;
    if (!element) return;
    if (!open) {
      if (element.open) element.close();
      opener?.focus();
      opener = null;
      return;
    }
    opener = document.activeElement as HTMLElement | null;
    if (!element.open) element.showModal();
    await nextTick();
    element.querySelector<HTMLElement>('.close-btn')?.focus();
    await loadSessions();
  },
);

// Wächst das Fenster über die Handy-Breite, gibt es die Seitenleiste nicht mehr.
watch(mobile, (isMobile) => {
  if (!isMobile) ui.sidebarOpen = false;
});

function close(): void {
  ui.sidebarOpen = false;
}

function onKeydown(event: KeyboardEvent): void {
  if (event.key !== 'Escape') return;
  event.preventDefault();
  close();
}

function onBackdrop(event: MouseEvent): void {
  if (event.target === dialog.value) close();
}

/** Eine leere Sitzung wird nicht doppelt angelegt: dann genügt der Wechsel zu „Fragen“. */
async function newSession(): Promise<void> {
  close();
  ui.showAsk();
  if (conversation.turns.length > 0 || conversation.pending) await conversation.startNewSession();
}

function choose(id: string): void {
  close();
  if (id === conversation.sessionId) {
    ui.showAsk();
    return;
  }
  void conversation.openSession(id);
}

function openReports(): void {
  close();
  ui.setMobileView('reports');
}

function openAbout(): void {
  ui.openAbout();
}

function logout(): void {
  close();
  void auth.logout();
}

function questionCount(count: number): string {
  return `${count} ${count === 1 ? 'Frage' : 'Fragen'}`;
}

// ---- Wischen nach links schließt: Die Leiste folgt dem Finger, Loslassen entscheidet nach Strecke und Tempo. ----
interface Drag {
  x: number;
  y: number;
  time: number;
  dx: number;
  active: boolean;
}
let drag: Drag | null = null;

function onPointerDown(event: PointerEvent): void {
  if (event.pointerType === 'mouse' || !event.isPrimary) return;
  drag = { x: event.clientX, y: event.clientY, time: event.timeStamp, dx: 0, active: false };
}

function onPointerMove(event: PointerEvent): void {
  const element = dialog.value;
  if (!drag || !element) return;
  const dx = event.clientX - drag.x;
  const dy = event.clientY - drag.y;
  if (!drag.active) {
    if (Math.abs(dy) > Math.abs(dx) || Math.abs(dx) < 8) return; // senkrecht: die Liste scrollt
    drag.active = true;
    element.setPointerCapture?.(event.pointerId);
    element.style.transition = 'none';
  }
  drag.dx = Math.min(0, dx); // nach rechts gibt es nichts zu ziehen
  element.style.transform = `translateX(${drag.dx}px)`;
}

function endDrag(event: PointerEvent, cancelled: boolean): void {
  const element = dialog.value;
  const current = drag;
  drag = null;
  if (!current?.active || !element) return;
  element.releasePointerCapture?.(event.pointerId);
  element.style.removeProperty('transition');
  element.style.removeProperty('transform');
  const velocity = Math.abs(current.dx) / Math.max(1, event.timeStamp - current.time);
  const farEnough = current.dx <= -SWIPE_DISTANCE;
  const quickEnough = current.dx <= -FLICK_MIN_DISTANCE && velocity > SWIPE_VELOCITY;
  if (!cancelled && (farEnough || quickEnough)) close();
}

onBeforeUnmount(() => {
  drag = null;
});
</script>

<template>
  <dialog
    id="m-sidebar"
    ref="dialog"
    class="m-sidebar"
    aria-labelledby="m-sidebar-title"
    @keydown="onKeydown"
    @close="close"
    @click="onBackdrop"
    @pointerdown="onPointerDown"
    @pointermove="onPointerMove"
    @pointerup="endDrag($event, false)"
    @pointercancel="endDrag($event, true)"
  >
    <div class="m-side-head">
      <h2 id="m-sidebar-title" class="m-side-title">Sitzungen</h2>
      <CloseButton @click="close" />
    </div>
    <button type="button" class="m-side-new" @click="newSession">
      <IconSvg name="compose" :size="20" />Neue Sitzung
    </button>

    <div class="m-side-scroll">
      <p v-if="sessions === null" class="m-side-note" role="status">Sitzungen werden geladen …</p>
      <p v-else-if="failed" class="m-side-note" role="alert">
        Die Sitzungen konnten nicht geladen werden.
      </p>
      <p v-else-if="groups.length === 0" class="m-side-note">Noch keine Sitzungen.</p>
      <section v-for="group in groups" :key="group.key" class="m-side-group">
        <h3 class="m-side-day">{{ group.label }}</h3>
        <ul class="m-side-list">
          <li v-for="session in group.sessions" :key="session.id">
            <button
              type="button"
              class="m-session"
              :aria-current="session.id === conversation.sessionId ? 'true' : undefined"
              @click="choose(session.id)"
            >
              <span class="m-session-title">{{ session.title || 'Neue Sitzung' }}</span>
              <span class="m-session-meta">
                {{ timeText(session.lastActivityUtc) }} · {{ questionCount(session.turnCount) }}
                <span v-if="session.id === conversation.sessionId" class="m-session-badge"
                  >aktuell</span
                >
              </span>
            </button>
          </li>
        </ul>
      </section>
    </div>

    <div v-if="identity.me" class="m-side-foot">
      <div class="m-side-user">
        <div class="user-avatar" aria-hidden="true">{{ identity.me.user.initials }}</div>
        <div class="m-side-user-text">
          <div class="user-name">{{ identity.me.user.displayName }}</div>
          <div class="user-title">{{ identity.me.role.label }}</div>
        </div>
      </div>
      <StatusBand />
      <div class="m-side-actions">
        <button type="button" class="m-side-action" @click="openReports">
          <IconSvg name="chart" :size="18" />Reports
        </button>
        <button v-if="auth.enabled" type="button" class="m-side-action" @click="logout">
          Abmelden
        </button>
      </div>
      <button type="button" class="m-side-action m-side-about" @click="openAbout">
        <IconSvg name="info" :size="18" />Über die Demo
      </button>
    </div>
  </dialog>
</template>
