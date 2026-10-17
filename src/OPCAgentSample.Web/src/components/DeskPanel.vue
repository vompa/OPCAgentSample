<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useFeedStore } from '@/stores/feed';
import { useIdentityStore } from '@/stores/identity';
import { useUiStore, type DeskPanel } from '@/stores/ui';
import { inertAttrs } from '@/utils/inert';
import CloseButton from './CloseButton.vue';
import FeedList from './FeedList.vue';
import InboxList from './InboxList.vue';
import UserCard from './UserCard.vue';

const INBOX_NOTE =
  'Einträge entstehen aus Eskalationsregeln im MCP-Server. Claude liefert die Analyse, die Entscheidung bleibt beim Menschen.';
/** Ausblenden dauert 110 ms; danach wird der Inhalt entfernt (Esc: 80 ms, siehe components.css). */
const EXIT_MS = 120;
const CARET_MARGIN = 20;

const ui = useUiStore();
const identity = useIdentityStore();
const feed = useFeedStore();
const host = ref<HTMLElement | null>(null);

/** Das Fenster bleibt beim Ausblenden mit seinem Inhalt stehen, bis die Bewegung zu Ende ist. */
const shown = ref<DeskPanel | null>(null);
let exitTimer: ReturnType<typeof setTimeout> | undefined;

const open = computed(() => ui.panel !== null && identity.me !== null);
const label = computed(
  () =>
    ({ inbox: 'Postfach', feed: 'Aktuelle Meldungen', user: 'Aktueller Benutzer' })[
      shown.value ?? 'user'
    ],
);

function triggerOf(panel: DeskPanel): HTMLElement | null {
  return document.querySelector<HTMLElement>(`[data-focus-key="sb-${panel}"]`);
}

/** Der Pfeil des Fensters zeigt auf die Mitte des auslösenden Knopfes der Statusleiste. */
function pointAtTrigger(panel: DeskPanel): void {
  const element = host.value;
  const trigger = triggerOf(panel);
  const wrap = element?.offsetParent;
  if (!element || !trigger || !wrap) return;
  const triggerCenter = trigger.getBoundingClientRect().left + trigger.offsetWidth / 2;
  const x = triggerCenter - wrap.getBoundingClientRect().left - element.offsetLeft;
  const clamped = Math.min(Math.max(x, CARET_MARGIN), element.offsetWidth - CARET_MARGIN);
  element.style.setProperty('--caret-x', `${Math.round(clamped)}px`);
}

/** Fokus ins Fenster, wenn es sich öffnet; beim Schließen zurück zum Auslöser, falls der Fokus nicht schon woanders ist. */
watch(
  () => ui.panel,
  async (panel, previous) => {
    clearTimeout(exitTimer);
    if (panel) {
      shown.value = panel;
      await nextTick();
      pointAtTrigger(panel);
      host.value?.querySelector<HTMLElement>('[tabindex="-1"]')?.focus({ preventScroll: true });
      return;
    }
    exitTimer = setTimeout(() => (shown.value = null), EXIT_MS);
    await nextTick();
    const active = document.activeElement;
    const focusIsLost = !active || active === document.body || host.value?.contains(active);
    if (previous && focusIsLost) triggerOf(previous)?.focus({ preventScroll: true });
  },
);

/** Klick außerhalb schließt das Fenster; die Knöpfe der Statusleiste schalten selbst um. */
function onPointerDown(event: PointerEvent): void {
  if (!ui.panel) return;
  const target = event.target as Element | null;
  if (!target || host.value?.contains(target) || target.closest('[data-focus-key^="sb-"]')) return;
  ui.closePanel();
}

function onResize(): void {
  if (ui.panel) pointAtTrigger(ui.panel);
}

onMounted(() => {
  document.addEventListener('pointerdown', onPointerDown);
  window.addEventListener('resize', onResize);
});
onBeforeUnmount(() => {
  clearTimeout(exitTimer);
  document.removeEventListener('pointerdown', onPointerDown);
  window.removeEventListener('resize', onResize);
});
</script>

<template>
  <div
    id="desk-panel"
    ref="host"
    :class="['desk-panel', { 'is-open': open, 'is-instant': ui.panelClosedByKeyboard }]"
    role="region"
    :aria-label="label"
    :data-panel="shown ?? undefined"
    v-bind="inertAttrs(!open)"
  >
    <div v-if="shown && identity.me" class="desk-panel-body">
      <template v-if="shown === 'inbox'">
        <div class="panel-head">
          <h2 class="panel-title" tabindex="-1">Postfach · Eingriff erforderlich</h2>
          <CloseButton @click="ui.closePanel()" />
        </div>
        <InboxList variant="desk" />
        <p class="panel-note">{{ INBOX_NOTE }}</p>
      </template>
      <template v-else-if="shown === 'feed'">
        <div class="panel-head">
          <div>
            <h2 class="panel-title" tabindex="-1">Aktuelle Meldungen</h2>
            <div class="panel-sub">{{ feed.feed?.filterText ?? '' }}</div>
          </div>
          <CloseButton @click="ui.closePanel()" />
        </div>
        <FeedList variant="desk" />
      </template>
      <template v-else>
        <CloseButton class="panel-close-corner" @click="ui.closePanel()" />
        <UserCard />
      </template>
    </div>
  </div>
</template>
