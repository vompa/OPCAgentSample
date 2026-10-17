// Anmeldung der Live-Demo mit Zugangscode. Im Browser hält das HttpOnly-Cookie die Sitzung; für Apps (Capacitor) liefert der
// Server dasselbe Token zusätzlich im Text der Antwort, es wird über die Speicher-Schnittstelle abgelegt und als Bearer gesendet.
import { defineStore } from 'pinia';
import { computed, ref, shallowRef } from 'vue';
import { api } from '@/api/client';
import { ApiError, isStartingUp } from '@/api/errors';
import { setUnauthorizedHandler } from '@/api/session';
import type { DemoLoginResult } from '@/api/types';
import { DEMO_ACCESS, USE_BEARER_TOKEN, setAuthToken } from '@/config';
import { platform } from '@/platform';
import { takeInviteCode } from '@/utils/inviteLink';
import { sleep } from '@/utils/sleep';
import { useBackendStore } from './backend';
import { clearSessionHint, readSessionHint, writeSessionHint } from './sessionHint';

export const TOKEN_KEY = 'opc.demoToken';

export const INVALID_CODE_TEXT = 'Der Zugangscode ist nicht gültig.';
export const EXPIRED_TEXT = 'Die Sitzung ist abgelaufen. Bitte erneut anmelden.';
export const UNREACHABLE_TEXT =
  'Der Server ist nicht erreichbar. Bitte später noch einmal versuchen.';

/** Hinweis, solange das schlafende Backend startet und der Anmeldeversuch wiederholt wird. */
export const STARTING_TEXT = 'Halle wird gestartet …';

/** So lange wiederholt die Anmeldung gegen ein schlafendes Backend (Kaltstart: typisch etwa 40 s, im schlechtesten Fall Minuten). */
export const LOGIN_DEADLINE_MS = 5 * 60_000;
/** Wartezeiten zwischen den Anmeldeversuchen gegen ein schlafendes Backend; die letzte gilt für alle weiteren. */
export const LOGIN_RETRY_DELAYS_MS: readonly number[] = [2_000, 3_000, 5_000, 5_000, 8_000, 10_000];

/** „in 45 Sekunden“ bzw. „in 6 Minuten“ (aufgerundet). */
export function waitText(seconds: number): string {
  if (seconds < 60) return `${Math.max(1, Math.ceil(seconds))} Sekunden`;
  const minutes = Math.ceil(seconds / 60);
  return minutes === 1 ? '1 Minute' : `${minutes} Minuten`;
}

/** Fehlertext der Anmeldeansicht zu einem fehlgeschlagenen Anmelden. */
export function loginErrorText(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.status === 401) return INVALID_CODE_TEXT;
    if (error.status === 429) {
      return error.retryAfterSeconds
        ? `Zu viele Fehlversuche. Bitte in ${waitText(error.retryAfterSeconds)} noch einmal versuchen.`
        : 'Zu viele Fehlversuche. Bitte später noch einmal versuchen.';
    }
    return `Anmeldung fehlgeschlagen: ${error.message}`;
  }
  return UNREACHABLE_TEXT;
}

function parseStored(raw: string | null): DemoLoginResult | null {
  if (!raw) return null;
  try {
    const stored = JSON.parse(raw) as Partial<DemoLoginResult>;
    if (typeof stored.token !== 'string' || typeof stored.expiresAtUtc !== 'string') return null;
    return Date.parse(stored.expiresAtUtc) > Date.now() ? (stored as DemoLoginResult) : null;
  } catch {
    return null;
  }
}

export const useAuthStore = defineStore('auth', () => {
  /** Der Zugang per Zugangscode ist an (Build-Schalter `VITE_DEMO_ACCESS`; die Oberfläche fragt dafür keinen Endpunkt). */
  const enabled = ref(DEMO_ACCESS);
  const authenticated = ref(false);
  /** Der Zustand wurde einmal bestimmt (lokaler Hinweis und Einladungslink; ohne API-Aufruf). */
  const checked = ref(false);
  const busy = ref(false);
  const error = ref('');
  const expired = ref(false);
  /** Die Anmeldung wird gegen ein schlafendes Backend wiederholt („Halle wird gestartet …“). */
  const starting = ref(false);
  /** Laufender Anmeldeversuch (inklusive Wiederholungen); das Wecken des Backends wartet darauf. */
  const pendingLogin = shallowRef<Promise<boolean> | null>(null);

  /** Anmeldeansicht anzeigen. */
  const needsLogin = computed(() => checked.value && enabled.value && !authenticated.value);
  /** Die Anwendung darf laden (Demo aus oder angemeldet). */
  const ready = computed(() => checked.value && (!enabled.value || authenticated.value));

  async function rememberToken(result: DemoLoginResult | null): Promise<void> {
    if (!USE_BEARER_TOKEN) return;
    setAuthToken(result?.token ?? null);
    await platform.storage.set(TOKEN_KEY, result ? JSON.stringify(result) : '');
  }

  async function restoreToken(): Promise<void> {
    if (!USE_BEARER_TOKEN) return;
    const stored = parseStored(await platform.storage.get(TOKEN_KEY));
    setAuthToken(stored?.token ?? null);
  }

  /** Eine Antwort 401 mitten in der Sitzung (abgelaufen): zurück zur Anmeldung. */
  function onUnauthorized(): void {
    if (!checked.value || !authenticated.value) return;
    enabled.value = true;
    authenticated.value = false;
    expired.value = true;
    error.value = EXPIRED_TEXT;
    void rememberToken(null);
    void clearSessionHint();
  }

  /**
   * Beim Start OHNE API-Aufruf: Einladungs-Key aus der Adresse nehmen (und dort entfernen), gespeichertes Token laden und den
   * lokalen Hinweis „angemeldet bis …“ prüfen. Gilt er noch, startet die Anwendung angemeldet (und weckt das Backend, siehe
   * App-Store); ob die Sitzung wirklich gilt, zeigt erst die erste Antwort (401 löscht den Hinweis). Mit Key wird automatisch
   * angemeldet. Der Key wird nicht gespeichert.
   */
  async function check(): Promise<void> {
    const inviteCode = takeInviteCode();
    setUnauthorizedHandler(onUnauthorized);
    await restoreToken();
    if (enabled.value) {
      if (inviteCode) await login(inviteCode);
      else if (await readSessionHint()) authenticated.value = true;
    }
    checked.value = true;
  }

  /**
   * Fragt den Server, ob die Sitzung noch gilt. Der Live-Strom (EventSource) kann ein 401 nicht von einem Netzfehler
   * unterscheiden; bei verlorener Verbindung prüft die Oberfläche deshalb hier und zeigt gegebenenfalls die Anmeldung.
   */
  async function verify(): Promise<void> {
    if (!enabled.value || !authenticated.value) return;
    try {
      const state = await api.demoSession();
      if (!state.authenticated) onUnauthorized();
    } catch {
      // Server nicht erreichbar: kein Beleg für eine abgelaufene Sitzung.
    }
  }

  /** Wartezeit vor dem n-ten Wiederholen der Anmeldung. */
  function retryDelay(attempt: number): number {
    return LOGIN_RETRY_DELAYS_MS[Math.min(attempt, LOGIN_RETRY_DELAYS_MS.length - 1)]!;
  }

  async function attemptLogin(code: string): Promise<boolean> {
    const deadline = Date.now() + LOGIN_DEADLINE_MS;
    for (let attempt = 0; ; attempt++) {
      try {
        const result = await api.demoLogin(code);
        await rememberToken(result);
        await writeSessionHint(result.expiresAtUtc);
        enabled.value = true;
        authenticated.value = true;
        expired.value = false;
        starting.value = false;
        return true;
      } catch (failure) {
        // 502/503/504, Netzwerkfehler, Zeitüberschreitung: das Backend schläft noch. Kein falscher Code, also wiederholen.
        if (isStartingUp(failure) && Date.now() < deadline) {
          if (!starting.value) {
            starting.value = true;
            authenticated.value = true; // vorläufig: Die Oberfläche zeigt die Vorschau, bis die Anmeldung gelingt
            checked.value = true;
          }
          await sleep(retryDelay(attempt));
          continue;
        }
        starting.value = false;
        authenticated.value = false;
        error.value = loginErrorText(failure);
        return false;
      }
    }
  }

  async function login(code: string): Promise<boolean> {
    busy.value = true;
    error.value = '';
    const attempt = attemptLogin(code);
    pendingLogin.value = attempt;
    try {
      return await attempt;
    } finally {
      busy.value = false;
      pendingLogin.value = null;
    }
  }

  async function logout(): Promise<void> {
    // Ein schlafendes Backend wird dafür nicht geweckt: Das Cookie ist signiert und läuft von selbst ab, der lokale Hinweis entfällt.
    if (useBackendStore().ready) {
      try {
        await api.demoLogout();
      } catch {
        // Das Token wird auf jeden Fall lokal verworfen.
      }
    }
    await rememberToken(null);
    await clearSessionHint();
    authenticated.value = false;
    expired.value = false;
    error.value = '';
  }

  return {
    enabled,
    authenticated,
    checked,
    busy,
    error,
    expired,
    starting,
    pendingLogin,
    needsLogin,
    ready,
    check,
    verify,
    login,
    logout,
  };
});
