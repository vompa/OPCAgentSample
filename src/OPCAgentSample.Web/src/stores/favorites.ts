// Favoriten (max. 10) und Bearbeitungsmodus; Speichern läuft nacheinander, damit keine Änderung verloren geht.
import { defineStore } from 'pinia';
import { ref } from 'vue';
import { api } from '@/api/client';
import type { FavoriteInput, FavoritesResponse } from '@/api/types';
import { shortTitleOf } from '@/utils/format';
import { useBackendStore } from './backend';
import { useIdentityStore } from './identity';
import { useUiStore } from './ui';

function describe(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

export const useFavoritesStore = defineStore('favorites', () => {
  const favorites = ref<FavoritesResponse | null>(null);
  const editing = ref(false);
  let queue: Promise<void> = Promise.resolve();

  function asInputs(): FavoriteInput[] {
    return (favorites.value?.items ?? []).map(({ shortTitle, question }) => ({
      shortTitle,
      question,
    }));
  }

  /** Speichert die komplette Liste; bei Fehlern wird der Serverstand neu geladen. */
  function save(items: FavoriteInput[]): Promise<void> {
    if (!useBackendStore().ready) {
      useUiStore().toast('Favoriten lassen sich ändern, sobald die Halle bereit ist.');
      return Promise.resolve();
    }
    queue = queue.then(async () => {
      const { role } = useIdentityStore();
      try {
        favorites.value = await api.saveFavorites(role, items);
      } catch (error) {
        useUiStore().toast(`Favoriten konnten nicht gespeichert werden: ${describe(error)}`);
        favorites.value = await api.favorites(role).catch(() => favorites.value);
      }
    });
    return queue;
  }

  /** Fügt einen Favoriten hinzu (Stecknadel, Bearbeitungsfeld, Vorschlag). Gibt true zurück, wenn gespeichert wird. */
  function add(shortTitle: string, question: string): boolean {
    const current = favorites.value;
    const text = question.trim();
    if (!current || !text) return false;
    const ui = useUiStore();
    if (current.items.length >= current.max) {
      ui.toast(`Alle ${current.max} Favoritenplätze sind belegt. Entferne zuerst einen Favoriten.`);
      return false;
    }
    if (current.items.some((item) => item.question === text)) {
      ui.toast('Diese Frage ist schon ein Favorit.');
      return false;
    }
    const title = shortTitle.trim() || shortTitleOf(text, current.maxShortTitleLength);
    void save([...asInputs(), { shortTitle: title, question: text }]);
    return true;
  }

  function remove(index: number): void {
    void save(asInputs().filter((_, position) => position !== index));
  }

  async function reset(): Promise<void> {
    const ui = useUiStore();
    if (!useBackendStore().ready) {
      ui.toast('Favoriten lassen sich ändern, sobald die Halle bereit ist.');
      return;
    }
    try {
      favorites.value = await api.resetFavorites(useIdentityStore().role);
      ui.toast('Standard-Favoriten zurückgeholt.');
    } catch (error) {
      ui.toast(`Zurücksetzen fehlgeschlagen: ${describe(error)}`);
    }
  }

  function setEditing(value: boolean): void {
    editing.value = value;
  }

  return { favorites, editing, save, add, remove, reset, setEditing };
});
