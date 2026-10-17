// Pfeil hoch/runter im Eingabefeld blättert durch frühere Fragen (neueste zuerst), wie in Claude Code.
import type { Ref } from 'vue';

export function useHistoryNavigation(text: Ref<string>, questions: () => string[]) {
  // Index in der Liste der letzten Fragen; -1 = eigener Entwurf.
  let index = -1;
  let draft = '';

  function reset(): void {
    index = -1;
    draft = '';
  }

  /** Verarbeitet eine Taste; gibt true zurück, wenn sie verbraucht wurde (Standardverhalten unterdrücken). */
  function onKeydown(key: string): boolean {
    const list = questions();
    if (key === 'ArrowUp' && list.length > 0 && index < list.length - 1) {
      if (index === -1) draft = text.value;
      index += 1;
      text.value = list[index] ?? '';
      return true;
    }
    if (key === 'ArrowDown' && index >= 0) {
      index -= 1;
      text.value = index === -1 ? draft : (list[index] ?? '');
      return true;
    }
    return false;
  }

  /** Eigene Eingabe beendet die Navigation. */
  function onInput(): void {
    index = -1;
  }

  return { reset, onKeydown, onInput };
}
