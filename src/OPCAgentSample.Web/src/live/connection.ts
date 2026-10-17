// Verbindung des Live-Stroms (LiveConnection) hinter einer Schnittstelle: nativer EventSource (Standard) oder fetch (Ausweichweg mit
// Authorization-Header, z. B. für Capacitor oder Zugangstoken). Wiederverbinden und Last-Event-ID regelt LiveClient.
import { buildHeaders } from '@/api/client';
import { FETCH_CREDENTIALS } from '@/config';
import { notifyUnauthorized } from '@/api/session';
import { SseParser } from '@/api/sse';

export interface LiveFrame {
  event: string;
  data: string;
  /** Ereignis-Id (Redis-Stream-Id der feed-Ereignisse), sonst leer. */
  id: string;
}

export interface ConnectionHandlers {
  onOpen(): void;
  onFrame(frame: LiveFrame): void;
  /** `fatal`: Der Transport verbindet nicht selbst neu; der Aufrufer muss es tun. */
  onError(fatal: boolean): void;
}

export interface LiveConnection {
  connect(url: string, lastEventId: string, handlers: ConnectionHandlers): void;
  close(): void;
}

/** Ereignisse, die der Server sendet (siehe LiveStreamService). */
export const LIVE_EVENT_NAMES = ['station', 'alarm', 'feed', 'inbox', 'clock', 'idle'] as const;

/** Nativer EventSource: verbindet bei Abbruch selbst neu (mit Last-Event-ID), außer die Verbindung wurde abgewiesen. */
export class EventSourceConnection implements LiveConnection {
  private source: EventSource | null = null;

  connect(url: string, lastEventId: string, handlers: ConnectionHandlers): void {
    this.close();
    // EventSource sendet Last-Event-ID nur beim eigenen Wiederverbinden; bei einem Neustart reicht der Parameter.
    const target = lastEventId
      ? `${url}${url.includes('?') ? '&' : '?'}lastEventId=${encodeURIComponent(lastEventId)}`
      : url;
    // Bei getrennter Herkunft (API auf eigener Subdomain) nur mit Cookie; sonst ändert `withCredentials` nichts.
    const source = new EventSource(target, { withCredentials: FETCH_CREDENTIALS === 'include' });
    this.source = source;
    source.addEventListener('open', () => handlers.onOpen());
    for (const name of LIVE_EVENT_NAMES) {
      source.addEventListener(name, (event) => {
        const message = event as MessageEvent<string>;
        handlers.onFrame({ event: name, data: message.data, id: message.lastEventId });
      });
    }
    source.addEventListener('error', () =>
      handlers.onError(source.readyState === EventSource.CLOSED),
    );
  }

  close(): void {
    this.source?.close();
    this.source = null;
  }
}

/** fetch-basierter Client: kann Header senden, verbindet aber nicht selbst neu (alles fatal). */
export class FetchConnection implements LiveConnection {
  private controller: AbortController | null = null;

  constructor(private readonly getToken: () => string | null) {}

  connect(url: string, lastEventId: string, handlers: ConnectionHandlers): void {
    this.close();
    const controller = new AbortController();
    this.controller = controller;
    void this.run(url, lastEventId, handlers, controller);
  }

  close(): void {
    this.controller?.abort();
    this.controller = null;
  }

  private async run(
    url: string,
    lastEventId: string,
    handlers: ConnectionHandlers,
    controller: AbortController,
  ): Promise<void> {
    try {
      const response = await fetch(url, {
        headers: buildHeaders(this.getToken(), {
          Accept: 'text/event-stream',
          ...(lastEventId ? { 'Last-Event-ID': lastEventId } : {}),
        }),
        credentials: FETCH_CREDENTIALS,
        signal: controller.signal,
      });
      if (!response.ok || !response.body) {
        if (response.status === 401) notifyUnauthorized(); // Zugangstoken abgelaufen
        handlers.onError(true);
        return;
      }
      handlers.onOpen();
      const reader = response.body.getReader();
      const decoder = new TextDecoder();
      const parser = new SseParser();
      for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        for (const message of parser.push(decoder.decode(value, { stream: true }))) {
          if (message.data)
            handlers.onFrame({ event: message.event, data: message.data, id: message.id });
        }
      }
      if (!controller.signal.aborted) handlers.onError(true); // Server hat den Strom beendet
    } catch {
      if (!controller.signal.aborted) handlers.onError(true);
    }
  }
}
