// Zuletzt gesehene Live-Daten je Rolle, hinter einer Schnittstelle (Browser: IndexedDB; Capacitor könnte eine Datei oder
// SQLite nutzen). Beim nächsten Besuch zeigt die Oberfläche sie statt der eingecheckten Momentaufnahme. Enthält nur
// Anlagen-, Postfach- und Meldungsdaten der Demo, nie den Zugangscode oder ein Token.

export interface DataCache {
  /** Gespeicherter Wert zum Schlüssel oder null (kein Eintrag, Speicher gesperrt, beschädigt). */
  get<T>(key: string): Promise<T | null>;
  set(key: string, value: unknown): Promise<void>;
}

const DATABASE = 'opc-agent-cache';
const STORE = 'live';

function open(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const request = globalThis.indexedDB.open(DATABASE, 1);
    request.onupgradeneeded = () => request.result.createObjectStore(STORE);
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error ?? new Error('IndexedDB nicht verfügbar'));
  });
}

function done<T>(request: IDBRequest<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error ?? new Error('IndexedDB-Fehler'));
  });
}

/** IndexedDB; ist sie nicht nutzbar (privates Fenster, gesperrt), bleibt der Wert nur für die Sitzung erhalten. */
export class IndexedDbCache implements DataCache {
  private readonly fallback = new Map<string, unknown>();

  async get<T>(key: string): Promise<T | null> {
    try {
      const database = await open();
      try {
        const value = await done(database.transaction(STORE).objectStore(STORE).get(key));
        return (value as T | undefined) ?? null;
      } finally {
        database.close();
      }
    } catch {
      return (this.fallback.get(key) as T | undefined) ?? null;
    }
  }

  async set(key: string, value: unknown): Promise<void> {
    try {
      const database = await open();
      try {
        await done(database.transaction(STORE, 'readwrite').objectStore(STORE).put(value, key));
      } finally {
        database.close();
      }
    } catch {
      this.fallback.set(key, value);
    }
  }
}
