import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ConnectionHandlers, LiveConnection } from './connection';
import { LiveClient, RETRY_MAX_MS, RETRY_MIN_MS, type LiveListener } from './liveClient';

class FakeConnection implements LiveConnection {
  static instances: FakeConnection[] = [];
  handlers!: ConnectionHandlers;
  url = '';
  lastEventId = '';
  closed = false;

  constructor() {
    FakeConnection.instances.push(this);
  }

  connect(url: string, lastEventId: string, handlers: ConnectionHandlers): void {
    this.url = url;
    this.lastEventId = lastEventId;
    this.handlers = handlers;
  }

  close(): void {
    this.closed = true;
  }
}

function makeListener() {
  return {
    state: vi.fn(),
    resync: vi.fn(),
    station: vi.fn(),
    alarm: vi.fn(),
    feed: vi.fn(),
    inbox: vi.fn(),
    clock: vi.fn(),
  } satisfies LiveListener;
}

const last = () => FakeConnection.instances[FakeConnection.instances.length - 1]!;
const create = (listener: LiveListener, urlFor = (role: string) => `/live?role=${role}`) =>
  new LiveClient(() => new FakeConnection(), urlFor, listener);

describe('LiveClient', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    FakeConnection.instances = [];
  });
  afterEach(() => vi.useRealTimers());

  it('verbindet für die Rolle, meldet open und gleicht beim ersten Mal die Meldungen ab', () => {
    const listener = makeListener();
    create(listener).start('hall_lead');

    expect(last().url).toBe('/live?role=hall_lead');
    expect(listener.state).toHaveBeenLastCalledWith('connecting');

    last().handlers.onOpen();
    expect(listener.state).toHaveBeenLastCalledWith('open');
    expect(listener.resync).toHaveBeenCalledTimes(1);
  });

  it('verteilt Ereignisse typisiert und ignoriert unlesbare Daten', () => {
    const listener = makeListener();
    create(listener).start('shift_lead');

    last().handlers.onFrame({ event: 'clock', data: '{"localTime":"09:43"}', id: '' });
    last().handlers.onFrame({ event: 'station', data: 'kaputt', id: '' });
    last().handlers.onFrame({ event: 'inbox', data: '{"open":2,"hasUrgent":true}', id: '' });

    expect(listener.clock).toHaveBeenCalledWith({ localTime: '09:43' });
    expect(listener.station).not.toHaveBeenCalled();
    expect(listener.inbox).toHaveBeenCalledWith({ open: 2, hasUrgent: true });
  });

  it('verbindet nach einem Abbruch mit wachsender Wartezeit und der letzten Event-Id neu', () => {
    const listener = makeListener();
    create(listener).start('shift_lead');
    last().handlers.onOpen();
    last().handlers.onFrame({ event: 'feed', data: '{"text":"a"}', id: '1760000000000-0' });

    last().handlers.onError(true);
    expect(listener.state).toHaveBeenLastCalledWith('lost');
    expect(FakeConnection.instances).toHaveLength(1);

    vi.advanceTimersByTime(RETRY_MIN_MS);
    expect(FakeConnection.instances).toHaveLength(2);
    expect(last().lastEventId).toBe('1760000000000-0');

    // Die nächste Wartezeit verdoppelt sich.
    last().handlers.onError(true);
    vi.advanceTimersByTime(RETRY_MIN_MS * 2 - 1);
    expect(FakeConnection.instances).toHaveLength(2);
    vi.advanceTimersByTime(1);
    expect(FakeConnection.instances).toHaveLength(3);

    // Nach erfolgreicher Verbindung beginnt die Wartezeit von vorn; mit Event-Id kein erneuter Abgleich.
    last().handlers.onOpen();
    expect(listener.resync).toHaveBeenCalledTimes(1);
    last().handlers.onError(true);
    vi.advanceTimersByTime(RETRY_MIN_MS);
    expect(FakeConnection.instances).toHaveLength(4);
  });

  it('begrenzt die Wartezeit auf RETRY_MAX_MS', () => {
    create(makeListener()).start('shift_lead');
    for (let i = 0; i < 8; i += 1) {
      last().handlers.onError(true);
      vi.advanceTimersByTime(RETRY_MAX_MS);
    }
    const before = FakeConnection.instances.length;
    last().handlers.onError(true);
    vi.advanceTimersByTime(RETRY_MAX_MS);
    expect(FakeConnection.instances).toHaveLength(before + 1);
  });

  it('wartet bei nicht fatalen Fehlern auf das eigene Wiederverbinden des Transports', () => {
    const listener = makeListener();
    create(listener).start('shift_lead');
    last().handlers.onError(false);
    vi.advanceTimersByTime(RETRY_MAX_MS * 2);
    expect(FakeConnection.instances).toHaveLength(1);
    expect(listener.state).toHaveBeenLastCalledWith('lost');
  });

  it('verwirft Ereignisse einer alten Verbindung nach einem Rollenwechsel', () => {
    const listener = makeListener();
    const client = create(listener);
    client.start('shift_lead');
    const old = last();
    client.start('hall_lead');

    expect(old.closed).toBe(true);
    old.handlers.onFrame({ event: 'clock', data: '{}', id: '' });
    old.handlers.onError(true);
    vi.advanceTimersByTime(RETRY_MAX_MS);
    expect(listener.clock).not.toHaveBeenCalled();
    expect(FakeConnection.instances).toHaveLength(2);
    expect(last().lastEventId).toBe('');
  });
  it('schließt bei idle die Verbindung, verbindet nicht neu und meldet "paused"', () => {
    const listener = makeListener();
    create(listener).start('shift_lead');
    last().handlers.onOpen();
    const first = last();

    first.handlers.onFrame({ event: 'idle', data: '{"reason":"max-duration"}', id: '' });
    expect(first.closed).toBe(true);
    expect(listener.state).toHaveBeenLastCalledWith('paused');

    // Auch das Ende des Stroms (Fehler) und lange Wartezeiten führen nicht zu einer neuen Verbindung.
    first.handlers.onError(true);
    vi.advanceTimersByTime(RETRY_MAX_MS * 4);
    expect(FakeConnection.instances).toHaveLength(1);
    expect(listener.state).toHaveBeenLastCalledWith('paused');
  });

  it('verbindet bei resume() mit der letzten Event-Id neu und gleicht nicht erneut ab', () => {
    const listener = makeListener();
    const client = create(listener);
    client.start('hall_lead');
    last().handlers.onOpen();
    last().handlers.onFrame({ event: 'feed', data: '{"text":"a"}', id: '1760000000000-0' });
    last().handlers.onFrame({ event: 'idle', data: '{}', id: '' });

    client.resume();

    expect(FakeConnection.instances).toHaveLength(2);
    expect(last().url).toBe('/live?role=hall_lead');
    expect(last().lastEventId).toBe('1760000000000-0');
    expect(listener.state).toHaveBeenLastCalledWith('connecting');
    last().handlers.onOpen();
    expect(listener.state).toHaveBeenLastCalledWith('open');
    expect(listener.resync).toHaveBeenCalledTimes(1);
  });

  it('ignoriert resume() und pause() ohne laufenden Strom und nach stop()', () => {
    const client = create(makeListener());
    client.resume();
    client.pause();
    expect(FakeConnection.instances).toHaveLength(0);

    client.start('shift_lead');
    client.pause();
    client.stop();
    client.resume();
    expect(FakeConnection.instances).toHaveLength(1);
  });

  it('pausiert auch von außen (verborgener Tab) und verbindet danach wieder', () => {
    const listener = makeListener();
    const client = create(listener);
    client.start('shift_lead');
    last().handlers.onOpen();

    client.pause();
    client.pause(); // zweimal ist harmlos
    expect(last().closed).toBe(true);
    expect(listener.state).toHaveBeenLastCalledWith('paused');

    client.resume();
    client.resume(); // zweiter Aufruf ohne Wirkung
    expect(FakeConnection.instances).toHaveLength(2);
  });

  it('startet nach einer Pause mit start() (Rollenwechsel) normal neu', () => {
    const listener = makeListener();
    const client = create(listener);
    client.start('shift_lead');
    client.pause();

    client.start('hall_lead');

    expect(last().url).toBe('/live?role=hall_lead');
    expect(listener.state).toHaveBeenLastCalledWith('connecting');
  });
});
