// Ablauf der Anwendung: Zuerst Vorschau-Daten (zuletzt gesehene Live-Daten oder eingecheckte Momentaufnahme), während das
// Backend geweckt wird; sobald es antwortet, die Rolle live laden (alle Bereiche parallel) und den Live-Strom halten.
import { defineStore } from 'pinia';
import { ref } from 'vue';
import { api } from '@/api/client';
import { platform } from '@/platform';
import {
  SNAPSHOT_VERSION,
  loadSnapshot,
  type RoleData,
  type RoleSnapshot,
} from '@/preview/snapshot';
import { LiveActivity } from '@/live/activity';
import { LiveClient, createDefaultConnection, liveUrl, type LiveListener } from '@/live/liveClient';
import type { LiveConnection } from '@/live/connection';
import { useAuthStore } from './auth';
import { useBackendStore } from './backend';
import { useConversationStore } from './conversation';
import { useFavoritesStore } from './favorites';
import { useFeedStore } from './feed';
import { useIdentityStore } from './identity';
import { useInboxStore } from './inbox';
import { useLiveStore } from './live';
import { usePlantStore } from './plant';
import { useReportsStore } from './reports';
import { useUiStore } from './ui';

function describe(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

/** Eintrag im Zwischenspeicher: die zuletzt gesehenen Live-Daten einer Rolle. */
interface CachedRole {
  version: string;
  data: RoleData;
}

function cacheKey(role: string): string {
  return `live:${role}`;
}

/** Abstand, in dem die Oberfläche während des Kaltstarts („Historie wird geladen …“) nachfragt. */
export const LOADING_POLL_MS = 4_000;

let connectionFactory: () => LiveConnection = createDefaultConnection;

/** Für Tests: eigene Verbindung für den Live-Strom. */
export function setLiveConnectionFactory(factory: () => LiveConnection): void {
  connectionFactory = factory;
}

export const useAppStore = defineStore('app', () => {
  const identity = useIdentityStore();
  const plant = usePlantStore();
  const favorites = useFavoritesStore();
  const inbox = useInboxStore();
  const feed = useFeedStore();
  const conversation = useConversationStore();
  const live = useLiveStore();
  const ui = useUiStore();
  const backend = useBackendStore();

  const loaded = ref(false);
  let loadToken = 0;
  let loadingTimer: ReturnType<typeof setTimeout> | undefined;
  let activityAttached = false;

  async function resyncFeed(role: string): Promise<void> {
    try {
      const [me, feedResponse] = await Promise.all([api.me(role), api.feed(role)]);
      if (role !== identity.role) return;
      identity.me = me;
      feed.feed = feedResponse;
    } catch {
      // Der Strom liefert weitere Meldungen ohnehin.
    }
  }

  /** Das Postfach ändert sich selten; bei einer Änderung holt die Oberfläche die Liste einmal (kein Takt). */
  async function refreshInbox(open: number, hasUrgent: boolean): Promise<void> {
    const role = identity.role;
    try {
      const items = await api.inbox(role);
      if (role !== identity.role) return;
      inbox.items = items;
    } catch {
      return; // der nächste Anlass versucht es erneut
    }
    if (identity.me) {
      identity.me = {
        ...identity.me,
        counters: { ...identity.me.counters, inboxOpen: open, inboxHasUrgent: hasUrgent },
      };
    }
  }

  const listener: LiveListener = {
    state: (state) => {
      live.connection = state;
      if (state === 'lost') void recheckAfterLoss(); // abgelaufene Sitzung? Backend eingeschlafen?
    },
    resync: () => void resyncFeed(identity.role),
    station: (overview) => {
      plant.overview = overview;
    },
    alarm: (alarms) => {
      live.alarms = alarms;
    },
    feed: (entry) => {
      if (!feed.addLive(entry) || !identity.me) return;
      identity.me = {
        ...identity.me,
        counters: {
          ...identity.me.counters,
          feedCount: feed.feed?.items.length ?? 0,
          latestFeed: entry,
        },
      };
    },
    inbox: (summary) => void refreshInbox(summary.open, summary.hasUrgent),
    clock: (clock) => identity.setClock(clock),
  };

  const client = new LiveClient(() => connectionFactory(), liveUrl, listener);
  const activity = new LiveActivity(client);

  /**
   * Kaltstart: Solange der Server die Historie noch aufnimmt, sind Zahlen unvollständig. Die Oberfläche zeigt den Hinweis,
   * fragt in größeren Abständen nach und lädt alles neu, sobald die Historie da ist (kein Dauer-Polling danach).
   */
  function watchHistoryLoading(role: string): void {
    clearTimeout(loadingTimer);
    if (!identity.me?.status.historyLoading) return;

    loadingTimer = setTimeout(async () => {
      try {
        const me = await api.me(role);
        if (role !== identity.role) return;
        if (!me.status.historyLoading) {
          await loadRole(role);
          return;
        }
        if (identity.me) identity.me = { ...identity.me, status: me.status };
      } catch {
        // der nächste Versuch folgt
      }
      watchHistoryLoading(role);
    }, LOADING_POLL_MS);
  }

  /** Zeigt die Daten einer Rolle als Vorschau: zuletzt gesehene Live-Daten, sonst die Momentaufnahme. */
  async function showPreview(role: string, token: number): Promise<void> {
    const [cached, snapshot] = await Promise.all([
      platform.cache.get<CachedRole>(cacheKey(role)),
      loadSnapshot(role).catch((): RoleSnapshot | null => null),
    ]);
    if (token !== loadToken || backend.ready) return; // inzwischen andere Rolle gewählt oder schon live
    const usable = cached?.version === SNAPSHOT_VERSION ? cached.data : null;
    const data = usable ?? snapshot;
    if (!data) {
      ui.toast('Die Vorschau ist nicht verfügbar.');
      return;
    }
    identity.me = { ...data.me, status: { ...data.me.status, historyLoading: false } };
    plant.overview = data.overview;
    favorites.favorites = data.favorites;
    inbox.items = data.inbox;
    feed.feed = data.feed;
    conversation.previewAnswers = snapshot?.answers ?? {};
    conversation.hydrate(data.history, [], true);
    backend.show(usable ? 'cached' : 'preview', data.me.clock.localTime);
    loaded.value = true;
  }

  /** Merkt die Live-Daten für den nächsten Besuch (Fehler des Speichers sind unwichtig). */
  async function remember(role: string, data: RoleData): Promise<void> {
    if (data.me.status.historyLoading) return; // unvollständige Zahlen nicht als „zuletzt gesehen“ merken
    try {
      const entry: CachedRole = { version: SNAPSHOT_VERSION, data };
      await platform.cache.set(cacheKey(role), entry);
    } catch {
      // Der Zwischenspeicher ist eine Bequemlichkeit.
    }
  }

  /** Lädt alles für die Rolle vom Backend; ersetzt die Vorschau-Daten durch Live-Daten. */
  async function loadLive(role: string, token: number): Promise<void> {
    try {
      const [me, overview, favs, items, feedResponse, latest, recent] = await Promise.all([
        api.me(role),
        api.overview(role),
        api.favorites(role),
        api.inbox(role),
        api.feed(role),
        api.latestHistory(role),
        api.recentQuestions(role),
      ]);
      if (token !== loadToken) return; // inzwischen andere Rolle gewählt
      identity.me = me;
      plant.overview = overview;
      favorites.favorites = favs;
      inbox.items = items;
      feed.feed = feedResponse;
      conversation.hydrate(latest, recent);
      conversation.previewAnswers = {};
      const data: RoleData = {
        me,
        overview,
        favorites: favs,
        inbox: items,
        feed: feedResponse,
        history: latest,
      };
      void remember(role, data);
    } catch (error) {
      ui.toast(`Laden fehlgeschlagen: ${describe(error)}`);
      return;
    }
    backend.show('live');
    loaded.value = true;
    watchHistoryLoading(role);
    client.start(role); // ab hier hält der Live-Strom die Anzeige aktuell (kein Polling)
    const reports = useReportsStore();
    if (reports.sample) void reports.load();
    void conversation.flushQueue(); // eine wartende Frage der Vorschau geht jetzt raus
  }

  /** Lädt alles für die gewählte Rolle neu (Start und Rollenwechsel): live, sobald das Backend bereit ist, sonst als Vorschau. */
  async function loadRole(role: string): Promise<void> {
    identity.setRole(role);
    const token = ++loadToken;
    favorites.setEditing(false);
    ui.closePanel();

    if (backend.ready) await loadLive(role, token);
    else await showPreview(role, token);
  }

  /** Weckt das Backend und lädt danach live (die Vorschau steht dann schon). */
  async function connect(): Promise<void> {
    if (!(await backend.wake())) return;
    await loadLive(identity.role, ++loadToken);
  }

  /** Nach „Erneut versuchen“, wenn das Backend nicht innerhalb der Frist antwortete. */
  function retryWake(): void {
    void connect();
  }

  /**
   * Der Live-Strom ist abgerissen: Ist die Sitzung abgelaufen (401, dann Anmeldung) oder ist das Backend eingeschlafen
   * (Cooldown nach einer Stunde ohne Aufrufe)? Dann neu wecken und live laden.
   */
  async function recheckAfterLoss(): Promise<void> {
    await useAuthStore().verify();
    if (!backend.ready) return;
    try {
      await api.health();
    } catch {
      backend.markAsleep();
      void connect();
    }
  }

  /** Einstieg: gespeicherte Einstellungen lesen, Backend wecken (parallel zur Vorschau), dann die Rolle laden. */
  async function init(): Promise<void> {
    await Promise.all([identity.init(), ui.init()]);
    if (!activityAttached) {
      activity.attach(); // Live pausiert bei langer Inaktivität und verbindet bei Eingabe wieder
      activityAttached = true;
    }
    if (!backend.ready) void connect();
    await loadRole(identity.role);
  }

  function stopLive(): void {
    clearTimeout(loadingTimer);
    client.stop();
    activity.detach();
    activityAttached = false;
  }

  /** Zurück in den Zustand vor dem Laden (Abmelden, abgelaufene Sitzung): Strom beenden, nichts mehr nachladen. */
  function reset(): void {
    stopLive();
    loadToken += 1; // laufende Ladevorgänge verwerfen
    loaded.value = false;
    backend.reset();
    conversation.clear(); // Sitzung und Fragen gehören dem bisherigen Besitzer; nach einer neuen Anmeldung beginnt der Verlauf neu
  }

  return { loaded, init, loadRole, retryWake, stopLive, reset };
});
