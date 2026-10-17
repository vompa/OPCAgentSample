import { createPinia, setActivePinia } from 'pinia';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiMock, resetApiMock } from '@/test/apiMock';
import { FAVORITES } from '@/test/fixtures';
import { useFavoritesStore } from './favorites';
import { useUiStore } from './ui';

vi.mock('@/api/client', () => ({ api: apiMock }));

beforeEach(() => {
  setActivePinia(createPinia());
  resetApiMock();
});

function loaded() {
  const store = useFavoritesStore();
  store.favorites = structuredClone(FAVORITES);
  return store;
}

describe('Favoriten', () => {
  it('fügt einen Favoriten mit Kurztitel aus der Frage hinzu und speichert die ganze Liste', async () => {
    const store = loaded();
    apiMock.saveFavorites.mockResolvedValue({ ...FAVORITES, max: 5 });

    expect(store.add('', 'Wie lange reicht das Gewebe an RT-02?')).toBe(true);
    await vi.waitFor(() => expect(apiMock.saveFavorites).toHaveBeenCalledTimes(1));

    const [role, items] = apiMock.saveFavorites.mock.calls[0] as [string, unknown[]];
    expect(role).toBe('shift_lead');
    expect(items).toHaveLength(3);
    expect(items[2]).toEqual({
      shortTitle: 'Wie lange reicht das …',
      question: 'Wie lange reicht das Gewebe an RT-02?',
    });
  });

  it('lehnt Duplikate und volle Listen mit Hinweis ab', () => {
    const store = loaded();
    const ui = useUiStore();

    expect(store.add('x', FAVORITES.items[0]!.question)).toBe(false);
    expect(ui.toastMessage).toBe('Diese Frage ist schon ein Favorit.');

    store.favorites!.items.push({
      shortTitle: 'Dritter',
      question: 'Dritte Frage?',
      isDefault: false,
    });
    expect(store.add('y', 'Vierte Frage?')).toBe(false);
    expect(ui.toastMessage).toContain('Alle 3 Favoritenplätze sind belegt');
    expect(apiMock.saveFavorites).not.toHaveBeenCalled();
  });

  it('speichert Änderungen nacheinander, damit keine verloren geht', async () => {
    const store = loaded();
    const order: string[] = [];
    apiMock.saveFavorites.mockImplementation(async (_role: string, items: unknown[]) => {
      order.push(`start:${items.length}`);
      await Promise.resolve();
      order.push(`ende:${items.length}`);
      return { ...FAVORITES, items: FAVORITES.items.slice(0, items.length) };
    });

    store.remove(0);
    store.remove(1);
    await store.save([]);

    // Jeder Speichervorgang endet, bevor der nächste beginnt.
    expect(order).toEqual(['start:1', 'ende:1', 'start:1', 'ende:1', 'start:0', 'ende:0']);
  });

  it('lädt bei einem Speicherfehler den Serverstand neu und meldet den Fehler', async () => {
    const store = loaded();
    apiMock.saveFavorites.mockRejectedValue(new Error('Verbindung'));
    apiMock.favorites.mockResolvedValue(structuredClone(FAVORITES));

    await store.save([]);

    expect(useUiStore().toastMessage).toContain(
      'Favoriten konnten nicht gespeichert werden: Verbindung',
    );
    expect(store.favorites?.items).toHaveLength(2);
  });
});
