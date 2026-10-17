// Speicherung kleiner Einstellungen hinter einer Schnittstelle (Browser: localStorage, Capacitor: Preferences).

export interface KeyValueStorage {
  get(key: string): Promise<string | null>;
  set(key: string, value: string): Promise<void>;
}

/** Browser-Speicher; ist er gesperrt (z. B. privates Fenster), bleibt die Einstellung nur für die Sitzung erhalten. */
export class BrowserStorage implements KeyValueStorage {
  private readonly fallback = new Map<string, string>();

  async get(key: string): Promise<string | null> {
    try {
      return globalThis.localStorage.getItem(key);
    } catch {
      return this.fallback.get(key) ?? null;
    }
  }

  async set(key: string, value: string): Promise<void> {
    try {
      globalThis.localStorage.setItem(key, value);
    } catch {
      this.fallback.set(key, value);
    }
  }
}
