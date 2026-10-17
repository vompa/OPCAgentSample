// Mobil: Jeder Reiter merkt sich seine Scrollposition. Die Ansichten sind getrennte Container mit `display: none`; der
// Browser setzt deren Scrollposition beim Ausblenden zurück.
import { nextTick, watch } from 'vue';
import { useConversationStore } from '@/stores/conversation';
import { useUiStore, type MobileView } from '@/stores/ui';

/** Der scrollende Container je Reiter. */
export const SCROLLERS: Record<MobileView, string> = {
  start: '#mview-start',
  ask: '#conv',
  inbox: '#mview-inbox',
  feed: '#mview-feed',
  reports: '#mview-reports',
};

interface SavedPosition {
  top: number;
  /** Zustand des Verlaufs beim Verlassen: Kam eine Frage dazu, richtet der Verlauf sich selbst neu aus. */
  signature: string;
}

export function useScrollMemory(): void {
  const ui = useUiStore();
  const conversation = useConversationStore();
  const saved = new Map<MobileView, SavedPosition>();

  const signature = (): string =>
    `${conversation.sessionId}|${conversation.turns.length}|${conversation.pending ? 1 : 0}`;
  const scrollerOf = (view: MobileView): HTMLElement | null =>
    document.querySelector<HTMLElement>(SCROLLERS[view]);

  // Synchron: Zum Zeitpunkt des Wechsels ist die bisherige Ansicht noch sichtbar und ihre Position lesbar.
  watch(
    () => ui.mobileView,
    async (next, previous) => {
      const leaving = scrollerOf(previous);
      if (leaving) saved.set(previous, { top: leaving.scrollTop, signature: signature() });
      await nextTick();
      const entry = saved.get(next);
      const entering = scrollerOf(next);
      if (!entry || !entering) return;
      if (next === 'ask' && entry.signature !== signature()) return;
      entering.scrollTop = entry.top;
    },
    { flush: 'sync' },
  );
}
