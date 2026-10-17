// Vorschau und Aufwachen: Die Oberfläche zeigt sofort Daten (Momentaufnahme oder zuletzt gesehene Live-Daten), weckt das
// Backend erst nach der Anmeldung und ersetzt die Vorschau durch Live-Daten, sobald es antwortet.
import { createPinia, setActivePinia } from 'pinia';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '@/api/errors';
import type { ConnectionHandlers, LiveConnection } from '@/live/connection';
import { configurePlatform } from '@/platform';
import type { DataCache } from '@/platform/cache';
import type { KeyValueStorage } from '@/platform/storage';
import { SNAPSHOT_VERSION, type RoleSnapshot } from '@/preview/snapshot';
import { apiMock, resetApiMock } from '@/test/apiMock';
import { CARD, FAVORITES, ME, REPORTS_SUMMARY, TOOL_CALLS, sessionPackage } from '@/test/fixtures';
import { setSleep } from '@/utils/sleep';
import { PREVIEW_NOTICE, useConversationStore } from './conversation';
import { setLiveConnectionFactory, useAppStore } from './app';
import { useAuthStore } from './auth';
import { WAKE_DEADLINE_MS, useBackendStore, waitDelay } from './backend';
import { useFavoritesStore } from './favorites';
import { useIdentityStore } from './identity';
import { useInboxStore } from './inbox';
import { useReportsStore } from './reports';
import { SESSION_HINT_KEY } from './sessionHint';

vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  api: apiMock,
}));

vi.mock('@/config', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/config')>()),
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

class MemoryCache implements DataCache {
  readonly values = new Map<string, unknown>();
  async get<T>(key: string): Promise<T | null> {
    return (this.values.get(key) as T | undefined) ?? null;
  }
  async set(key: string, value: unknown): Promise<void> {
    this.values.set(key, value);
  }
}

class FakeConnection implements LiveConnection {
  static instances: FakeConnection[] = [];
  handlers!: ConnectionHandlers;
  constructor() {
    FakeConnection.instances.push(this);
  }
  connect(_url: string, _lastEventId: string, handlers: ConnectionHandlers): void {
    this.handlers = handlers;
  }
  close(): void {}
}

const FAVORITE_QUESTION = FAVORITES.defaults[0]!.question;
const SNAPSHOT_ME = { ...ME, clock: { ...ME.clock, localTime: '09:42' } };

const SNAPSHOT: RoleSnapshot = {
  version: SNAPSHOT_VERSION,
  asOfUtc: '2026-10-15T07:42:00Z',
  me: SNAPSHOT_ME,
  overview: {
    scope: 'hall',
    id: 'Halle A',
    title: 'Halle A',
    liveText: 'live 09:42',
    climate: null,
    counts: { problem: 0, warning: 0, running: 1 },
    groups: [],
  },
  favorites: FAVORITES,
  inbox: [],
  feed: { filterText: 'alle', items: [] },
  history: sessionPackage(),
  answers: {
    [FAVORITE_QUESTION]: {
      toolCalls: TOOL_CALLS,
      card: { ...CARD, notice: 'Vorbereitete Antwort' },
    },
  },
};

let storage: MemoryStorage;
let cache: MemoryCache;
let fetchMock: ReturnType<typeof vi.fn>;
let delays: number[];

function mockLiveApi(): void {
  apiMock.me.mockResolvedValue({ ...ME, user: { ...ME.user, firstName: 'Live' } });
  apiMock.overview.mockResolvedValue({
    scope: 'hall',
    groups: [],
    counts: { problem: 0, warning: 0, running: 0 },
  });
  apiMock.favorites.mockResolvedValue(FAVORITES);
  apiMock.inbox.mockResolvedValue([]);
  apiMock.feed.mockResolvedValue({ filterText: '', items: [] });
  apiMock.latestHistory.mockResolvedValue(sessionPackage());
  apiMock.recentQuestions.mockResolvedValue([]);
}

beforeEach(() => {
  setActivePinia(createPinia());
  resetApiMock();
  storage = new MemoryStorage();
  cache = new MemoryCache();
  configurePlatform({ storage, cache });
  FakeConnection.instances = [];
  setLiveConnectionFactory(() => new FakeConnection());
  delays = [];
  setSleep((ms) => {
    delays.push(ms);
    return Promise.resolve();
  });
  fetchMock = vi.fn((url: string) =>
    Promise.resolve(
      url.endsWith('reports.json')
        ? new Response(JSON.stringify(REPORTS_SUMMARY))
        : new Response(JSON.stringify(SNAPSHOT)),
    ),
  );
  vi.stubGlobal('fetch', fetchMock);
  storage.values.set(SESSION_HINT_KEY, '2999-01-01T00:00:00Z'); // Wiederkehrer
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

async function signedIn(): Promise<void> {
  const auth = useAuthStore();
  await auth.check();
  expect(auth.ready).toBe(true);
}

describe('Aufwachen des Backends', () => {
  it('ruft vor der Anmeldung keinen API-Endpunkt auf', async () => {
    storage.values.delete(SESSION_HINT_KEY); // erster Besuch
    const auth = useAuthStore();
    await auth.check();
    await useBackendStore().wake();

    expect(auth.needsLogin).toBe(true);
    for (const call of Object.values(apiMock)) {
      if (typeof call === 'function') expect(call).not.toHaveBeenCalled();
    }
  });

  it('fragt /api/health mit wachsendem Abstand, bis das Backend antwortet (502/504/Netz = startet noch)', async () => {
    await signedIn();
    apiMock.health
      .mockRejectedValueOnce(new ApiError(502, 'Bad Gateway'))
      .mockRejectedValueOnce(new ApiError(504, 'Gateway Timeout'))
      .mockRejectedValueOnce(new TypeError('Failed to fetch'))
      .mockResolvedValueOnce({ status: 'unhealthy' })
      .mockResolvedValue({ status: 'healthy' });
    const backend = useBackendStore();

    const woke = await backend.wake();

    expect(woke).toBe(true);
    expect(backend.phase).toBe('ready');
    expect(apiMock.health).toHaveBeenCalledTimes(5);
    expect(delays).toEqual([waitDelay(0), waitDelay(1), waitDelay(2), waitDelay(3)]);
    expect(delays.every((ms) => ms > 0)).toBe(true);
  });

  it('gibt nach der Frist auf (Phase „failed“) und kann erneut versucht werden', async () => {
    await signedIn();
    vi.useFakeTimers();
    try {
      apiMock.health.mockImplementation(() => {
        vi.setSystemTime(Date.now() + WAKE_DEADLINE_MS + 1);
        return Promise.reject(new ApiError(503, 'Service Unavailable'));
      });
      const backend = useBackendStore();

      expect(await backend.wake()).toBe(false);
      expect(backend.phase).toBe('failed');

      apiMock.health.mockResolvedValue({ status: 'healthy' });
      expect(await backend.wake()).toBe(true);
    } finally {
      vi.useRealTimers();
    }
  });

  it('wartet einen laufenden Anmeldeversuch ab und wertet einen ungültigen Code als Abbruch', async () => {
    const auth = useAuthStore();
    await auth.check(); // keine Sitzung
    apiMock.demoLogin.mockRejectedValue(new ApiError(401, 'x'));
    const backend = useBackendStore();

    const login = auth.login('falsch');
    const woke = await backend.wake();
    await login;

    expect(woke).toBe(false);
    expect(apiMock.health).not.toHaveBeenCalled();
    expect(backend.phase).toBe('asleep');
  });
});

describe('Vorschau und Wechsel auf Live-Daten', () => {
  it('zeigt sofort die Momentaufnahme, weckt das Backend und ersetzt sie durch Live-Daten', async () => {
    await signedIn();
    mockLiveApi();
    let releaseHealth: () => void = () => {};
    apiMock.health.mockImplementation(
      () => new Promise((resolve) => (releaseHealth = () => resolve({ status: 'healthy' }))),
    );
    const app = useAppStore();
    const backend = useBackendStore();
    const identity = useIdentityStore();

    await app.init();

    // Vorschau steht, das Backend wurde nur über /api/health angesprochen.
    expect(backend.source).toBe('preview');
    expect(backend.stamp).toBe('09:42');
    expect(identity.me?.user.firstName).toBe(ME.user.firstName);
    expect(fetchMock).toHaveBeenCalledWith('/snapshot/shift_lead.json', expect.anything());
    expect(apiMock.me).not.toHaveBeenCalled();
    expect(apiMock.demoLogin).not.toHaveBeenCalled();
    expect(useConversationStore().turns.every((turn) => turn.preview)).toBe(true);

    releaseHealth();
    await vi.waitFor(() => expect(backend.source).toBe('live'));

    expect(identity.me?.user.firstName).toBe('Live');
    expect(FakeConnection.instances).toHaveLength(1); // Live-Strom gestartet
    expect(useConversationStore().turns.some((turn) => turn.preview)).toBe(false);
    expect(cache.values.has('live:shift_lead')).toBe(true);
    app.stopLive();
  });

  it('zeigt beim nächsten Besuch die zuletzt gesehenen Live-Daten statt der Momentaufnahme', async () => {
    await signedIn();
    mockLiveApi();
    cache.values.set('live:shift_lead', {
      version: SNAPSHOT_VERSION,
      data: {
        ...SNAPSHOT,
        me: {
          ...SNAPSHOT_ME,
          user: { ...SNAPSHOT_ME.user, firstName: 'Gemerkt' },
          clock: { ...ME.clock, localTime: '09:47' },
        },
      },
    });
    apiMock.health.mockReturnValue(new Promise(() => {})); // Backend startet noch
    const app = useAppStore();

    await app.init();

    expect(useBackendStore().source).toBe('cached');
    expect(useBackendStore().stamp).toBe('09:47');
    expect(useIdentityStore().me?.user.firstName).toBe('Gemerkt');
  });

  it('beantwortet Standard-Favoriten sofort mit der vorbereiteten Antwort als Vorschau', async () => {
    await signedIn();
    apiMock.health.mockReturnValue(new Promise(() => {}));
    const app = useAppStore();
    await app.init();
    const conversation = useConversationStore();
    const before = conversation.turns.length;

    await conversation.ask(FAVORITE_QUESTION);

    expect(apiMock.ask).not.toHaveBeenCalled();
    expect(conversation.turns).toHaveLength(before + 1);
    const turn = conversation.turns.at(-1)!;
    expect(turn.preview && turn.local).toBe(true);
    expect(turn.answerCard.notice).toBe(PREVIEW_NOTICE);
  });

  it('stellt freie Fragen in die Warteschlange und sendet sie, sobald die Halle bereit ist', async () => {
    await signedIn();
    mockLiveApi();
    let releaseHealth: () => void = () => {};
    apiMock.health.mockImplementation(
      () => new Promise((resolve) => (releaseHealth = () => resolve({ status: 'healthy' }))),
    );
    apiMock.ask.mockImplementation(async (_body, handlers) => {
      handlers.answer?.(CARD);
      handlers.done?.({
        sessionId: 's1',
        sequence: 5,
        dataAsOf: '2026-10-15T07:50:00Z',
        dataAsOfText: '09:50',
        tokens: { input: 1, output: 1 },
      });
    });
    const app = useAppStore();
    await app.init();
    const conversation = useConversationStore();

    await conversation.ask('Eine ganz freie Frage?');

    expect(conversation.pending?.queued).toBe(true);
    expect(conversation.busy).toBe(true);
    expect(apiMock.ask).not.toHaveBeenCalled();

    releaseHealth();
    await vi.waitFor(() => expect(apiMock.ask).toHaveBeenCalledTimes(1));
    expect(apiMock.ask.mock.calls[0]![0]).toMatchObject({ question: 'Eine ganz freie Frage?' });
    await vi.waitFor(() => expect(conversation.pending).toBeNull());
    expect(conversation.turns.at(-1)?.question).toBe('Eine ganz freie Frage?');
    app.stopLive();
  });

  it('behält im Browser beantwortete Vorschau-Antworten, wenn die Live-Daten kommen', async () => {
    await signedIn();
    mockLiveApi();
    let releaseHealth: () => void = () => {};
    apiMock.health.mockImplementation(
      () => new Promise((resolve) => (releaseHealth = () => resolve({ status: 'healthy' }))),
    );
    const app = useAppStore();
    await app.init();
    const conversation = useConversationStore();
    await conversation.ask(FAVORITE_QUESTION);

    releaseHealth();
    await vi.waitFor(() => expect(useBackendStore().source).toBe('live'));

    const kept = conversation.turns.filter((turn) => turn.local);
    expect(kept).toHaveLength(1);
    expect(new Set(conversation.turns.map((turn) => turn.sequence)).size).toBe(
      conversation.turns.length,
    );
    app.stopLive();
  });

  it('nummeriert Verlauf eindeutig, wenn eine Vorschau-Antwort und eine wartende Frage zusammentreffen', async () => {
    await signedIn();
    mockLiveApi();
    let releaseHealth: () => void = () => {};
    apiMock.health.mockImplementation(
      () => new Promise((resolve) => (releaseHealth = () => resolve({ status: 'healthy' }))),
    );
    apiMock.ask.mockImplementation(async (_body, handlers) => {
      handlers.answer?.(CARD);
      handlers.done?.({
        sessionId: 's1',
        sequence: 2, // der Server kennt die Vorschau-Antwort nicht und vergibt dieselbe Nummer
        dataAsOf: '2026-10-15T07:50:00Z',
        dataAsOfText: '09:50',
        tokens: { input: 1, output: 1 },
      });
    });
    const app = useAppStore();
    await app.init();
    const conversation = useConversationStore();
    await conversation.ask(FAVORITE_QUESTION);
    await conversation.ask('Eine ganz freie Frage?');

    releaseHealth();
    await vi.waitFor(() =>
      expect(conversation.turns.at(-1)?.question).toBe('Eine ganz freie Frage?'),
    );

    const sequences = conversation.turns.map((turn) => turn.sequence);
    expect(new Set(sequences).size).toBe(sequences.length);
    expect(sequences).toEqual([...sequences].sort((a, b) => a - b));
    app.stopLive();
  });

  it('ändert in der Vorschau nichts am Server (Postfach, Favoriten, Sitzungen)', async () => {
    await signedIn();
    apiMock.health.mockReturnValue(new Promise(() => {}));
    const app = useAppStore();
    await app.init();

    await useInboxStore().take('x');
    useFavoritesStore().remove(0);
    await useConversationStore().startNewSession();

    expect(apiMock.takeInboxItem).not.toHaveBeenCalled();
    expect(apiMock.saveFavorites).not.toHaveBeenCalled();
    expect(apiMock.newSession).not.toHaveBeenCalled();
  });

  it('zeigt Reports in der Vorschau als Beispielwerte und lädt später live', async () => {
    await signedIn();
    apiMock.health.mockReturnValue(new Promise(() => {}));
    await useAppStore().init();
    const reports = useReportsStore();

    await reports.load();

    expect(reports.sample).toBe(true);
    expect(apiMock.reportsSummary).not.toHaveBeenCalled();
  });

  it('kehrt bei 401 nach dem Wecken zur Anmeldung zurück und löscht den Hinweis', async () => {
    await signedIn();
    apiMock.health.mockResolvedValue({ status: 'healthy' });
    apiMock.me.mockImplementation(async () => {
      const { notifyUnauthorized } = await import('@/api/session');
      notifyUnauthorized();
      throw new ApiError(401, 'Nicht angemeldet');
    });
    apiMock.overview.mockResolvedValue({});
    apiMock.favorites.mockResolvedValue(FAVORITES);
    apiMock.inbox.mockResolvedValue([]);
    apiMock.feed.mockResolvedValue({ filterText: '', items: [] });
    apiMock.latestHistory.mockResolvedValue(sessionPackage());
    apiMock.recentQuestions.mockResolvedValue([]);
    const auth = useAuthStore();

    await useAppStore().init();
    await vi.waitFor(() => expect(auth.needsLogin).toBe(true));

    expect(auth.expired).toBe(true);
    await vi.waitFor(() => expect(storage.values.get(SESSION_HINT_KEY)).toBe(''));
  });
});
