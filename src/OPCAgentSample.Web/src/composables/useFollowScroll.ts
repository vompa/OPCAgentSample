// Mobil: Der Verlauf läuft mit, während eine Antwort einläuft, solange der Nutzer nicht nach oben gescrollt hat.
// Das Ziel ist das Ende des Inhalts, aber nie weiter, als dass der Anfang der letzten Frage am oberen Rand steht:
// Eine lange Antwort beginnt also immer mit ihrer Frage.
import { onBeforeUnmount, onMounted, type Ref } from 'vue';

/** Abstand über der letzten Frage, den das Ausrichten freihält. */
const TURN_MARGIN = 8;
/** So nah am Ende gilt der Verlauf wieder als „unten“ und folgt erneut. */
const BOTTOM_TOLERANCE = 8;
const SCROLL_NOISE = 2;

export function useFollowScroll(container: Ref<HTMLElement | null>) {
  let pinned = true;
  let lastTop = 0;
  let lastHeight = 0;
  let observer: ResizeObserver | undefined;

  function lastTurn(element: HTMLElement): HTMLElement | undefined {
    return Array.from(element.children)
      .filter((child): child is HTMLElement => child.classList.contains('turn'))
      .at(-1);
  }

  function target(element: HTMLElement): number {
    const bottom = Math.max(0, element.scrollHeight - element.clientHeight);
    const turn = lastTurn(element);
    if (!turn) return 0;
    return Math.min(bottom, Math.max(0, turn.offsetTop - TURN_MARGIN));
  }

  /** Folgt dem Inhalt (nur nach unten). `force` richtet neu aus: neue Frage oder anderer Verlauf. */
  function follow(force = false): void {
    const element = container.value;
    if (!element) return;
    if (force) pinned = true;
    if (!pinned) return;
    const next = force ? target(element) : Math.max(element.scrollTop, target(element));
    if (next !== element.scrollTop) element.scrollTop = next;
    lastTop = element.scrollTop;
    lastHeight = element.scrollHeight;
  }

  /** Nach oben scrollen löst das Mitlaufen; zurück ans Ende scrollen setzt es wieder. */
  function onScroll(): void {
    const element = container.value;
    if (!element) return;
    const { scrollTop, scrollHeight, clientHeight } = element;
    const shrank = scrollHeight < lastHeight; // der Browser klemmt die Position, das ist kein Wischen
    lastHeight = scrollHeight;
    if (!shrank) {
      if (scrollTop < lastTop - SCROLL_NOISE) pinned = false;
      else if (scrollTop >= scrollHeight - clientHeight - BOTTOM_TOLERANCE) pinned = true;
    }
    lastTop = scrollTop;
  }

  onMounted(() => {
    if (typeof ResizeObserver === 'undefined' || !container.value) return;
    // Größenänderungen des Verlaufs selbst (Tastatur, Drehen) behalten das Ende im Blick.
    observer = new ResizeObserver(() => follow());
    observer.observe(container.value);
  });
  onBeforeUnmount(() => observer?.disconnect());

  return { follow, onScroll };
}
