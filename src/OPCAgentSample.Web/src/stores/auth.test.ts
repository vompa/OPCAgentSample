import { createPinia, setActivePinia } from 'pinia';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '@/api/errors';
import { notifyUnauthorized, setUnauthorizedHandler } from '@/api/session';
import { getAuthToken, setAuthToken } from '@/config';
import { configurePlatform } from '@/platform';
import type { KeyValueStorage } from '@/platform/storage';
import { apiMock, resetApiMock } from '@/test/apiMock';
import { setSleep } from '@/utils/sleep';
import { useBackendStore } from './backend';
import { SESSION_HINT_KEY } from './sessionHint';
import {
  EXPIRED_TEXT,
  LOGIN_DEADLINE_MS,
  STARTING_TEXT,
  INVALID_CODE_TEXT,
  TOKEN_KEY,
  UNREACHABLE_TEXT,
  loginErrorText,
  useAuthStore,
  waitText,
} from './auth';

vi.mock('@/api/client', () => ({ api: apiMock }));

// Capacitor-Modus: Token im Speicher ablegen und als Bearer senden.
vi.mock('@/config', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/config')>()),
  USE_BEARER_TOKEN: true,
  DEMO_ACCESS: true,
}));

class MemoryStorage implements KeyValueStorage {
  readonly values = new Map<string, string>();
  async get(key: string): Promise<string | null> {
    return this.values.get(key) ?? null;
  }
  async set(key: string, value: string): Promise<void> {
    this.values.set(key, value);
  }
}

let storage: MemoryStorage;

beforeEach(() => {
  setSleep(() => Promise.resolve());
  setActivePinia(createPinia());
  resetApiMock();
  storage = new MemoryStorage();
  configurePlatform({ storage });
  setAuthToken(null);
});

afterEach(() => {
  setUnauthorizedHandler(null);
  setAuthToken(null);
});

describe('Texte', () => {
  it('rundet Wartezeiten auf', () => {
    expect(waitText(1)).toBe('1 Sekunden');
    expect(waitText(45.2)).toBe('46 Sekunden');
    expect(waitText(60)).toBe('1 Minute');
    expect(waitText(361)).toBe('7 Minuten');
  });

  it('übersetzt Fehler der Anmeldung', () => {
    expect(loginErrorText(new ApiError(401, 'x'))).toBe(INVALID_CODE_TEXT);
    expect(loginErrorText(new ApiError(429, 'x', 330))).toBe(
      'Zu viele Fehlversuche. Bitte in 6 Minuten noch einmal versuchen.',
    );
    expect(loginErrorText(new ApiError(429, 'x'))).toContain('später');
    expect(loginErrorText(new ApiError(500, 'Boom'))).toBe('Anmeldung fehlgeschlagen: Boom');
    expect(loginErrorText(new TypeError('Failed to fetch'))).toBe(UNREACHABLE_TEXT);
  });
});

const FUTURE = '2999-01-01T00:00:00Z';

describe('check', () => {
  it('ruft ohne Anmeldung keinen API-Endpunkt auf und verlangt die Anmeldung', async () => {
    const auth = useAuthStore();

    await auth.check();

    expect(auth.needsLogin).toBe(true);
    expect(auth.ready).toBe(false);
    for (const call of Object.values(apiMock)) {
      if (typeof call === 'function') expect(call).not.toHaveBeenCalled();
    }
  });

  it('startet mit gültigem lokalem Hinweis angemeldet, ohne API-Aufruf', async () => {
    storage.values.set(SESSION_HINT_KEY, FUTURE);
    const auth = useAuthStore();

    await auth.check();

    expect(auth.ready).toBe(true);
    expect(apiMock.demoSession).not.toHaveBeenCalled();
    expect(apiMock.demoLogin).not.toHaveBeenCalled();
  });

  it('verwirft einen abgelaufenen Hinweis', async () => {
    storage.values.set(SESSION_HINT_KEY, '2000-01-01T00:00:00Z');
    const auth = useAuthStore();

    await auth.check();

    expect(auth.needsLogin).toBe(true);
    expect(storage.values.get(SESSION_HINT_KEY)).toBe('');
  });

  it('stellt ein gespeichertes, gültiges Token wieder her und verwirft ein abgelaufenes', async () => {
    storage.values.set(TOKEN_KEY, JSON.stringify({ token: 'gespeichert', expiresAtUtc: FUTURE }));
    await useAuthStore().check();
    expect(getAuthToken()).toBe('gespeichert');

    setActivePinia(createPinia());
    setAuthToken(null);
    storage.values.set(
      TOKEN_KEY,
      JSON.stringify({ token: 'alt', expiresAtUtc: '2000-01-01T00:00:00Z' }),
    );
    await useAuthStore().check();
    expect(getAuthToken()).toBeNull();
  });
});

describe('login', () => {
  it('meldet an, speichert Token und Hinweis (nie den Code) und setzt den Bearer', async () => {
    const result = { token: 'tok', expiresAtUtc: FUTURE };
    apiMock.demoLogin.mockResolvedValue(result);
    const auth = useAuthStore();
    await auth.check();

    const ok = await auth.login('Code-123');

    expect(ok).toBe(true);
    expect(apiMock.demoLogin).toHaveBeenCalledWith('Code-123');
    expect(auth.ready).toBe(true);
    expect(auth.error).toBe('');
    expect(getAuthToken()).toBe('tok');
    expect(JSON.parse(storage.values.get(TOKEN_KEY)!)).toEqual(result);
    expect(storage.values.get(SESSION_HINT_KEY)).toBe(FUTURE);
    expect([...storage.values.values()].join('')).not.toContain('Code-123');
  });

  it('zeigt bei falschem Code den Fehlertext und bleibt abgemeldet', async () => {
    apiMock.demoLogin.mockRejectedValue(new ApiError(401, 'Der Zugangscode ist nicht gültig.'));
    const auth = useAuthStore();
    await auth.check();

    const ok = await auth.login('falsch');

    expect(ok).toBe(false);
    expect(auth.error).toBe(INVALID_CODE_TEXT);
    expect(auth.needsLogin).toBe(true);
    expect(auth.busy).toBe(false);
    expect(apiMock.demoLogin).toHaveBeenCalledTimes(1);
    expect(getAuthToken()).toBeNull();
  });

  it('nennt bei 429 die Wartezeit und wiederholt nicht', async () => {
    apiMock.demoLogin.mockRejectedValue(new ApiError(429, 'Zu viele', 90));
    const auth = useAuthStore();
    await auth.check();

    await auth.login('x');

    expect(auth.error).toBe('Zu viele Fehlversuche. Bitte in 2 Minuten noch einmal versuchen.');
    expect(apiMock.demoLogin).toHaveBeenCalledTimes(1);
  });

  it('wiederholt die Anmeldung gegen ein schlafendes Backend (502/504/Netz), ohne es als falschen Code zu werten', async () => {
    apiMock.demoLogin
      .mockRejectedValueOnce(new ApiError(502, 'Bad Gateway'))
      .mockRejectedValueOnce(new ApiError(504, 'Gateway Timeout'))
      .mockRejectedValueOnce(new TypeError('Failed to fetch'))
      .mockResolvedValue({ token: 'tok', expiresAtUtc: FUTURE });
    const auth = useAuthStore();
    await auth.check();

    const states: boolean[] = [];
    setSleep(() => {
      states.push(auth.starting && auth.ready); // vorläufig angemeldet: Die Oberfläche zeigt die Vorschau
      return Promise.resolve();
    });
    expect(await auth.login('Code')).toBe(true);

    expect(states).toEqual([true, true, true]);
    expect(STARTING_TEXT).toBe('Halle wird gestartet …');
    expect(apiMock.demoLogin).toHaveBeenCalledTimes(4);
    expect(auth.starting).toBe(false);
    expect(auth.error).toBe('');
    expect(storage.values.get(SESSION_HINT_KEY)).toBe(FUTURE);
  });

  it('kehrt zur Anmeldung zurück, wenn der Code nach dem Aufwachen ungültig ist', async () => {
    apiMock.demoLogin
      .mockRejectedValueOnce(new ApiError(504, 'Gateway Timeout'))
      .mockRejectedValue(new ApiError(401, 'Der Zugangscode ist nicht gültig.'));
    const auth = useAuthStore();
    await auth.check();

    expect(await auth.login('falsch')).toBe(false);

    expect(auth.needsLogin).toBe(true);
    expect(auth.starting).toBe(false);
    expect(auth.error).toBe(INVALID_CODE_TEXT);
  });

  it('gibt nach der Frist auf und meldet den Fehler', async () => {
    const auth = useAuthStore();
    await auth.check();
    apiMock.demoLogin.mockRejectedValue(new ApiError(502, 'Bad Gateway'));
    setSleep(() => {
      vi.setSystemTime(Date.now() + LOGIN_DEADLINE_MS + 1);
      return Promise.resolve();
    });
    vi.useFakeTimers();
    try {
      expect(await auth.login('Code')).toBe(false);
    } finally {
      vi.useRealTimers();
    }

    expect(auth.needsLogin).toBe(true);
    expect(auth.starting).toBe(false);
    expect(auth.error).toContain('Anmeldung fehlgeschlagen');
  });

  it('Abmelden verwirft Token, Hinweis und Sitzung; ein schlafendes Backend wird dafür nicht geweckt', async () => {
    apiMock.demoLogin.mockResolvedValue({ token: 'tok', expiresAtUtc: FUTURE });
    const auth = useAuthStore();
    await auth.check();
    await auth.login('ok');

    await auth.logout();

    expect(apiMock.demoLogout).not.toHaveBeenCalled();
    expect(auth.needsLogin).toBe(true);
    expect(getAuthToken()).toBeNull();
    expect(storage.values.get(TOKEN_KEY)).toBe('');
    expect(storage.values.get(SESSION_HINT_KEY)).toBe('');
  });

  it('Abmelden ruft den Server auf, wenn das Backend läuft', async () => {
    apiMock.demoLogin.mockResolvedValue({ token: 'tok', expiresAtUtc: FUTURE });
    apiMock.demoLogout.mockResolvedValue(null);
    apiMock.health.mockResolvedValue({ status: 'healthy' });
    const auth = useAuthStore();
    await auth.check();
    await auth.login('ok');
    await useBackendStore().wake();

    await auth.logout();

    expect(apiMock.demoLogout).toHaveBeenCalledTimes(1);
  });
});

describe('Einladungslink', () => {
  afterEach(() => window.history.replaceState(null, '', '/'));

  it('entfernt den Key aus der Adresse und meldet automatisch an, ohne ihn zu speichern', async () => {
    window.history.replaceState(null, '', '/?x=1#code=Einladung-1');
    apiMock.demoLogin.mockResolvedValue({ token: 'tok', expiresAtUtc: FUTURE });
    const auth = useAuthStore();

    await auth.check();

    expect(window.location.hash).toBe('');
    expect(window.location.search).toBe('?x=1');
    expect(apiMock.demoLogin).toHaveBeenCalledExactlyOnceWith('Einladung-1');
    expect(auth.ready).toBe(true);
    expect([...storage.values.values()].join('')).not.toContain('Einladung-1');
    expect(window.localStorage.length).toBe(0);
  });

  it('zeigt bei falschem Key die Anmeldeansicht mit der üblichen Fehlermeldung', async () => {
    window.history.replaceState(null, '', '/#code=falsch');
    apiMock.demoLogin.mockRejectedValue(new ApiError(401, 'Der Zugangscode ist nicht gültig.'));
    const auth = useAuthStore();

    await auth.check();

    expect(window.location.hash).toBe('');
    expect(auth.needsLogin).toBe(true);
    expect(auth.error).toBe(INVALID_CODE_TEXT);
  });

  it('nennt bei 429 die Wartezeit', async () => {
    window.history.replaceState(null, '', '/#code=x');
    apiMock.demoLogin.mockRejectedValue(new ApiError(429, 'Zu viele', 90));
    const auth = useAuthStore();

    await auth.check();

    expect(auth.needsLogin).toBe(true);
    expect(auth.error).toBe('Zu viele Fehlversuche. Bitte in 2 Minuten noch einmal versuchen.');
  });

  it('meldet ohne Key nicht an, auch mit bestehendem Hinweis', async () => {
    window.history.replaceState(null, '', '/#anderes=1');
    storage.values.set(SESSION_HINT_KEY, FUTURE);
    const auth = useAuthStore();

    await auth.check();

    expect(apiMock.demoLogin).not.toHaveBeenCalled();
    expect(auth.ready).toBe(true);
  });
});

describe('verify', () => {
  it('führt bei verlorener Sitzung zur Anmeldung und lässt eine gültige Sitzung in Ruhe', async () => {
    storage.values.set(SESSION_HINT_KEY, FUTURE);
    apiMock.demoSession.mockResolvedValue({
      enabled: true,
      authenticated: true,
      expiresAtUtc: null,
    });
    const auth = useAuthStore();
    await auth.check();

    await auth.verify();
    expect(auth.ready).toBe(true);

    apiMock.demoSession.mockResolvedValue({
      enabled: true,
      authenticated: false,
      expiresAtUtc: null,
    });
    await auth.verify();
    expect(auth.needsLogin).toBe(true);
    expect(auth.expired).toBe(true);
    await vi.waitFor(() => expect(storage.values.get(SESSION_HINT_KEY)).toBe(''));
  });

  it('hält die Sitzung, wenn der Server nicht erreichbar ist', async () => {
    storage.values.set(SESSION_HINT_KEY, FUTURE);
    const auth = useAuthStore();
    await auth.check();
    apiMock.demoSession.mockRejectedValue(new TypeError('offline'));

    await auth.verify();

    expect(auth.ready).toBe(true);
  });
});

describe('abgelaufene Sitzung', () => {
  it('führt ein 401 mitten in der Sitzung zurück zur Anmeldung mit Hinweis und löscht den Hinweis', async () => {
    storage.values.set(SESSION_HINT_KEY, FUTURE);
    const auth = useAuthStore();
    await auth.check();
    expect(auth.ready).toBe(true);

    notifyUnauthorized();

    expect(auth.needsLogin).toBe(true);
    expect(auth.expired).toBe(true);
    expect(auth.error).toBe(EXPIRED_TEXT);
    await vi.waitFor(() => expect(storage.values.get(SESSION_HINT_KEY)).toBe(''));
  });

  it('ignoriert ein 401, solange die Anmeldeansicht schon offen ist', async () => {
    const auth = useAuthStore();
    await auth.check();

    notifyUnauthorized();

    expect(auth.expired).toBe(false);
    expect(auth.error).toBe('');
  });
});
