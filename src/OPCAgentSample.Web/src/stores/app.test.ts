import { createPinia, setActivePinia } from 'pinia';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ConnectionHandlers, LiveConnection } from '@/live/connection';
import { apiMock, resetApiMock } from '@/test/apiMock';
import { FAVORITES, ME, sessionPackage } from '@/test/fixtures';
import { useAuthStore } from './auth';
import { LOADING_POLL_MS, setLiveConnectionFactory, useAppStore } from './app';
import { useIdentityStore } from './identity';
import { useLiveStore } from './live';

vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  api: apiMock,
}));

class FakeConnection implements LiveConnection {
  static instances: FakeConnection[] = [];
  handlers!: ConnectionHandlers;
  closed = false;

  constructor() {
    FakeConnection.instances.push(this);
  }

  connect(_url: string, _lastEventId: string, handlers: ConnectionHandlers): void {
    this.handlers = handlers;
  }

  close(): void {
    this.closed = true;
  }
}

const loadingMe = { ...ME, status: { ...ME.status, historyLoading: true } };

function mockApi(me = ME): void {
  apiMock.me.mockResolvedValue(me);
  apiMock.overview.mockResolvedValue({ scope: 'hall', groups: [] });
  apiMock.favorites.mockResolvedValue(FAVORITES);
  apiMock.inbox.mockResolvedValue([]);
  apiMock.feed.mockResolvedValue({ filterText: '', items: [] });
  apiMock.latestHistory.mockResolvedValue(sessionPackage());
  apiMock.recentQuestions.mockResolvedValue([]);
}

beforeEach(() => {
  vi.useFakeTimers();
  setActivePinia(createPinia());
  resetApiMock();
  FakeConnection.instances = [];
  setLiveConnectionFactory(() => new FakeConnection());
});

afterEach(() => {
  vi.useRealTimers();
});

describe('Kaltstart: Historie wird geladen', () => {
  it('fragt nach, aktualisiert den Hinweis und lädt alles neu, sobald die Historie da ist', async () => {
    mockApi(loadingMe);
    const app = useAppStore();
    await app.init();
    expect(useIdentityStore().me?.status.historyLoading).toBe(true);
    expect(apiMock.overview).toHaveBeenCalledTimes(1);

    // Weiter am Laden: nur der Status wird nachgeführt, die Anzeige wird nicht neu aufgebaut.
    await vi.advanceTimersByTimeAsync(LOADING_POLL_MS);
    expect(apiMock.me).toHaveBeenCalledTimes(2);
    expect(apiMock.overview).toHaveBeenCalledTimes(1);

    // Fertig: Der Status liefert historyLoading=false, danach lädt die Oberfläche alles einmal neu.
    apiMock.me.mockResolvedValue(ME);
    await vi.advanceTimersByTimeAsync(LOADING_POLL_MS);
    await vi.advanceTimersByTimeAsync(0);
    expect(apiMock.overview).toHaveBeenCalledTimes(2);
    expect(useIdentityStore().me?.status.historyLoading).toBe(false);

    // Danach keine weitere Nachfrage.
    const calls = apiMock.me.mock.calls.length;
    await vi.advanceTimersByTimeAsync(LOADING_POLL_MS * 5);
    expect(apiMock.me.mock.calls.length).toBe(calls);
    app.stopLive();
  });

  it('fragt ohne Kaltstart gar nicht erst nach', async () => {
    mockApi(ME);
    const app = useAppStore();
    await app.init();

    await vi.advanceTimersByTimeAsync(LOADING_POLL_MS * 3);

    expect(apiMock.me).toHaveBeenCalledTimes(1);
    app.stopLive();
  });

  it('reset() beendet Nachfragen und Live-Strom', async () => {
    mockApi(loadingMe);
    const app = useAppStore();
    await app.init();

    app.reset();
    await vi.advanceTimersByTimeAsync(LOADING_POLL_MS * 3);

    expect(apiMock.me).toHaveBeenCalledTimes(1);
    expect(FakeConnection.instances[0]!.closed).toBe(true);
    expect(app.loaded).toBe(false);
  });
});

describe('Live-Strom und Aktivität', () => {
  it('pausiert bei idle und verbindet erst bei der nächsten Eingabe neu', async () => {
    mockApi(ME);
    const app = useAppStore();
    await app.init();
    const live = useLiveStore();
    FakeConnection.instances[0]!.handlers.onOpen();
    expect(live.connection).toBe('open');

    FakeConnection.instances[0]!.handlers.onFrame({ event: 'idle', data: '{}', id: '' });
    expect(live.connection).toBe('paused');
    await vi.advanceTimersByTimeAsync(120_000);
    expect(FakeConnection.instances).toHaveLength(1);

    document.dispatchEvent(new Event('pointerdown'));
    expect(FakeConnection.instances).toHaveLength(2);
    expect(live.connection).toBe('connecting');
    app.stopLive();
  });

  it('prüft bei verlorener Verbindung die Sitzung und zeigt bei Ablauf die Anmeldung', async () => {
    mockApi(ME);
    const auth = useAuthStore();
    auth.enabled = true;
    auth.authenticated = true;
    auth.checked = true;
    const app = useAppStore();
    await app.init();

    apiMock.demoSession.mockResolvedValue({
      enabled: true,
      authenticated: false,
      expiresAtUtc: null,
    });
    FakeConnection.instances[0]!.handlers.onError(true);
    await vi.advanceTimersByTimeAsync(0);

    expect(auth.needsLogin).toBe(true);
    app.stopLive();
  });

  it('nach reset() verbindet eine Eingabe nicht wieder', async () => {
    mockApi(ME);
    const app = useAppStore();
    await app.init();
    FakeConnection.instances[0]!.handlers.onFrame({ event: 'idle', data: '{}', id: '' });

    app.reset();
    document.dispatchEvent(new Event('keydown'));

    expect(FakeConnection.instances).toHaveLength(1);
  });
});
