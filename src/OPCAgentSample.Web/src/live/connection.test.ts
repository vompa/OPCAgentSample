import { afterEach, describe, expect, it, vi } from 'vitest';
import { EventSourceConnection, FetchConnection } from './connection';

class FakeEventSource {
  static instances: FakeEventSource[] = [];
  static readonly CLOSED = 2;
  readyState = 0;
  listeners = new Map<string, (event: unknown) => void>();
  closed = false;

  constructor(readonly url: string) {
    FakeEventSource.instances.push(this);
  }

  addEventListener(name: string, listener: (event: unknown) => void): void {
    this.listeners.set(name, listener);
  }

  close(): void {
    this.closed = true;
  }
}

const makeHandlers = () => ({ onOpen: vi.fn(), onFrame: vi.fn(), onError: vi.fn() });

afterEach(() => {
  vi.unstubAllGlobals();
  FakeEventSource.instances = [];
});

describe('EventSourceConnection', () => {
  it('hängt die Event-Id als Parameter an und meldet Rahmen', () => {
    vi.stubGlobal('EventSource', FakeEventSource);
    const handlers = makeHandlers();
    new EventSourceConnection().connect('/api/live?role=shift_lead', '17-0', handlers);

    const source = FakeEventSource.instances[0]!;
    expect(source.url).toBe('/api/live?role=shift_lead&lastEventId=17-0');
    source.listeners.get('feed')!({ data: '{"a":1}', lastEventId: '18-0' });
    expect(handlers.onFrame).toHaveBeenCalledWith({ event: 'feed', data: '{"a":1}', id: '18-0' });
  });

  it('lauscht auch auf das Ereignis idle', () => {
    vi.stubGlobal('EventSource', FakeEventSource);
    const handlers = makeHandlers();
    new EventSourceConnection().connect('/api/live', '', handlers);

    FakeEventSource.instances[0]!.listeners.get('idle')!({ data: '{}', lastEventId: '' });

    expect(handlers.onFrame).toHaveBeenCalledWith({ event: 'idle', data: '{}', id: '' });
  });

  it('meldet einen Abbruch nur dann als fatal, wenn der Browser nicht selbst neu verbindet', () => {
    vi.stubGlobal('EventSource', FakeEventSource);
    const handlers = makeHandlers();
    new EventSourceConnection().connect('/api/live', '', handlers);
    const source = FakeEventSource.instances[0]!;

    source.readyState = 0; // CONNECTING: der Browser versucht es selbst
    source.listeners.get('error')!({});
    source.readyState = FakeEventSource.CLOSED; // abgewiesen (z. B. 429)
    source.listeners.get('error')!({});

    expect(handlers.onError.mock.calls).toEqual([[false], [true]]);
  });
});

describe('FetchConnection', () => {
  it('sendet Authorization und Last-Event-ID als Header und liest den Strom', async () => {
    const encoder = new TextEncoder();
    const body = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(encoder.encode('retry: 3000\n\nevent: clock\nid: 5\ndata: {"t":1}\n\n'));
        controller.close();
      },
    });
    const fetchMock = vi.fn().mockResolvedValue(new Response(body, { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);
    const handlers = makeHandlers();

    new FetchConnection(() => 'tok').connect('/api/live?role=x', '4', handlers);
    await vi.waitFor(() => expect(handlers.onError).toHaveBeenCalled());

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(init.headers).toMatchObject({ Authorization: 'Bearer tok', 'Last-Event-ID': '4' });
    expect(handlers.onOpen).toHaveBeenCalled();
    expect(handlers.onFrame).toHaveBeenCalledWith({ event: 'clock', data: '{"t":1}', id: '5' });
    expect(handlers.onError).toHaveBeenCalledWith(true); // Ende des Stroms: der Client verbindet neu
  });

  it('meldet abgewiesene Verbindungen als fatal', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('', { status: 429 })));
    const handlers = makeHandlers();
    new FetchConnection(() => null).connect('/api/live', '', handlers);
    await vi.waitFor(() => expect(handlers.onError).toHaveBeenCalledWith(true));
    expect(handlers.onOpen).not.toHaveBeenCalled();
  });
});
