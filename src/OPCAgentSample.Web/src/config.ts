// Laufzeit-Konfiguration. Alles Browser-/Geräteabhängige steht hinter kleinen Schnittstellen (siehe platform/).

/** Basisadresse der API; leer = gleiche Herkunft. Für eine Capacitor-App z. B. https://opc.example.com. */
export const API_BASE: string = (import.meta.env.VITE_API_BASE ?? '').replace(/\/+$/, '');

/**
 * Build-Schalter „Demo-Zugang an“ (`VITE_DEMO_ACCESS=true`): Die Oberfläche kennt den Zugang per Zugangscode schon beim Bauen und
 * ruft deshalb ohne Anmeldung KEINEN API-Endpunkt auf (das Backend schläft und wird erst nach der Anmeldung geweckt). Ohne den
 * Schalter (lokal, `npm run dev`) gilt der Server als offen und die Anwendung lädt sofort live.
 */
export const DEMO_ACCESS: boolean = import.meta.env.VITE_DEMO_ACCESS === 'true';

/**
 * Cookies mitsenden: bei getrennter Herkunft (Oberfläche auf opc.vompa.dev, API auf api.opc.vompa.dev) per `include`, sonst
 * nur bei gleicher Herkunft. Das Cookie bleibt `SameSite=Strict`, weil beide Herkünfte dieselbe Site (vompa.dev) sind.
 */
export const FETCH_CREDENTIALS: RequestCredentials = API_BASE ? 'include' : 'same-origin';

/** Ordner der eingecheckten Momentaufnahme (Vorschau-Daten), von derselben Herkunft wie die Oberfläche (statisch, ohne Backend). */
export const SNAPSHOT_BASE = '/snapshot';

/**
 * Zugangstoken als `Authorization: Bearer` senden (Capacitor-App, andere Herkunft als der Server) statt des Cookies.
 * Im Browser bleibt das Cookie `demo_session` Standard; das Token wird dann nicht gespeichert.
 */
export const USE_BEARER_TOKEN: boolean = import.meta.env.VITE_AUTH_TOKEN === 'true';

let authToken: string | null = null;

/** Zugangstoken für den Header `Authorization: Bearer …` (Capacitor oder Zugangscode); null = Anmeldung per Cookie/lokal. */
export function setAuthToken(token: string | null): void {
  authToken = token;
}

export function getAuthToken(): string | null {
  return authToken;
}
