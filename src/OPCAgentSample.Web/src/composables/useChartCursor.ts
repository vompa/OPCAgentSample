// Gemeinsamer „Cursor“ der Diagramme: Maus, Fokus und Pfeiltasten wählen einen Wert, dessen Angaben eine Zeile über dem Diagramm nennt.
import { computed, ref, type Ref } from 'vue';

export function useChartCursor(count: Ref<number>, fallback?: Ref<number>) {
  const active = ref<number | null>(null);
  /** Ohne Auswahl gilt `fallback` (z. B. der Spitzenwert), sonst der jüngste (letzte) Wert. */
  const shown = computed(() =>
    Math.max(0, Math.min(count.value - 1, active.value ?? fallback?.value ?? count.value - 1)),
  );

  function select(index: number): void {
    active.value = Math.max(0, Math.min(count.value - 1, index));
  }

  function clear(): void {
    active.value = null;
  }

  function onKeydown(event: KeyboardEvent): void {
    const last = count.value - 1;
    let next: number;
    if (event.key === 'ArrowRight') next = shown.value + 1;
    else if (event.key === 'ArrowLeft') next = shown.value - 1;
    else if (event.key === 'Home') next = 0;
    else if (event.key === 'End') next = last;
    else return;
    event.preventDefault();
    select(next);
  }

  return { active, shown, select, clear, onKeydown };
}
