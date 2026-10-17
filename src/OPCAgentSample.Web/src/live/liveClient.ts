// Live-Status per Server-Sent Events: ein Strom GET /api/live?role= statt Polling.
// Ereignisse: station (Anlagenübersicht), alarm, feed (neue Meldung, Id = Stream-Id), inbox (Zähler), clock.
// Der Client verwaltet Wiederverbinden mit wachsender Wartezeit und die Last-Event-ID, unabhängig vom Transport.
// Meldet der Server `idle` (Höchstdauer erreicht, damit die Anwendung einschlafen kann), schließt der Client die Verbindung und
// verbindet NICHT von selbst neu ("pausiert"); resume() (Eingabe, Sichtbarwerden) holt das nach. Dasselbe tut pause() für
// lange verborgene Tabs.
import { API_BASE, getAuthToken } from '@/config';
import { buildUrl } from '@/api/client';
import type {
  AlarmSummary,
  ClockView,
  FeedEntry,
  InboxSummary,
  OverviewResponse,
} from '@/api/types';
import {
  EventSourceConnection,
  FetchConnection,
  type ConnectionHandlers,
  type LiveConnection,
  type LiveFrame,
} from './connection';

export type LiveState = 'connecting' | 'open' | 'lost' | 'paused';

export interface LiveListener {
  state(state: LiveState): void;
  /** Erste Verbindung ohne Wiederaufnahme: Zwischenstand (Lücke zwischen Laden und Verbinden) abgleichen. */
  resync(): void;
  station(overview: OverviewResponse): void;
  alarm(alarms: AlarmSummary): void;
  feed(entry: FeedEntry): void;
  inbox(summary: InboxSummary): void;
  clock(clock: ClockView): void;
}

export const RETRY_MIN_MS = 2_000;
export const RETRY_MAX_MS = 30_000;

export class LiveClient {
  private connection: LiveConnection | null = null;
  private lastEventId = '';
  private retryTimer: ReturnType<typeof setTimeout> | undefined;
  private retryDelay = RETRY_MIN_MS;
  private generation = 0;
  private current: LiveState = 'connecting';
  private role = '';

  constructor(
    private readonly createConnection: () => LiveConnection,
    private readonly urlFor: (role: string) => string,
    private readonly listener: LiveListener,
  ) {}

  /** Verbindet (neu) für die Rolle; ein laufender Strom wird zuerst geschlossen. */
  start(role: string): void {
    this.teardown();
    this.role = role;
    this.lastEventId = '';
    this.retryDelay = RETRY_MIN_MS;
    this.setState('connecting', true);
    this.open(role, this.generation);
  }

  /** Pausiert: Verbindung schließen, nicht neu verbinden, bis resume() aufgerufen wird. Ohne laufenden Strom ohne Wirkung. */
  pause(): void {
    if (!this.role || this.current === 'paused') return;
    this.teardown();
    this.setState('paused');
  }

  /** Verbindet nach einer Pause wieder (mit der letzten Event-Id, verpasste Meldungen kommen nach). */
  resume(): void {
    if (!this.role || this.current !== 'paused') return;
    this.retryDelay = RETRY_MIN_MS;
    this.setState('connecting');
    this.open(this.role, this.generation);
  }

  /** Beendet den Strom endgültig (z. B. beim Abmelden); resume() hat danach keine Wirkung, bis start() aufgerufen wird. */
  stop(): void {
    this.teardown();
    this.role = '';
  }

  private teardown(): void {
    this.generation += 1; // verwirft Ereignisse und Zeitgeber des alten Stroms
    clearTimeout(this.retryTimer);
    this.connection?.close();
    this.connection = null;
  }

  private setState(next: LiveState, force = false): void {
    if (!force && this.current === next) return;
    this.current = next;
    this.listener.state(next);
  }

  private open(role: string, token: number): void {
    const connection = this.createConnection();
    this.connection = connection;
    const handlers: ConnectionHandlers = {
      onOpen: () => {
        if (token !== this.generation) return;
        this.retryDelay = RETRY_MIN_MS;
        this.setState('open');
        if (!this.lastEventId) this.listener.resync();
      },
      onFrame: (frame) => {
        if (token === this.generation) this.dispatch(frame);
      },
      onError: (fatal) => {
        if (token !== this.generation) return;
        this.setState('lost');
        if (fatal) this.scheduleRetry(role, token);
      },
    };
    connection.connect(this.urlFor(role), this.lastEventId, handlers);
  }

  private dispatch(frame: LiveFrame): void {
    if (frame.id) this.lastEventId = frame.id;
    let data: unknown;
    try {
      data = JSON.parse(frame.data);
    } catch {
      return; // unlesbares Ereignis ignorieren
    }
    switch (frame.event) {
      case 'station':
        this.listener.station(data as OverviewResponse);
        break;
      case 'alarm':
        this.listener.alarm(data as AlarmSummary);
        break;
      case 'feed':
        this.listener.feed(data as FeedEntry);
        break;
      case 'inbox':
        this.listener.inbox(data as InboxSummary);
        break;
      case 'clock':
        this.listener.clock(data as ClockView);
        break;
      case 'idle':
        this.pause();
        break;
    }
  }

  private scheduleRetry(role: string, token: number): void {
    clearTimeout(this.retryTimer);
    this.retryTimer = setTimeout(() => {
      if (token !== this.generation) return;
      this.connection?.close();
      this.setState('connecting');
      this.open(role, token);
    }, this.retryDelay);
    this.retryDelay = Math.min(this.retryDelay * 2, RETRY_MAX_MS);
  }
}

/** Mit Zugangstoken (Header nötig) fetch, sonst der native EventSource. */
export function createDefaultConnection(): LiveConnection {
  return getAuthToken() ? new FetchConnection(getAuthToken) : new EventSourceConnection();
}

export function liveUrl(role: string): string {
  return buildUrl(API_BASE, '/live', { role });
}
