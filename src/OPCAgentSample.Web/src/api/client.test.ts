import { afterEach, describe, expect, it, vi } from 'vitest';
import { REPORTS_SUMMARY } from '@/test/fixtures';
import { ApiError, buildUrl, createApi, isStartingUp } from './client';
import { setUnauthorizedHandler } from './session';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function streamResponse(chunks: string[]): Response {
  const encoder = new TextEncoder();
  const body = new ReadableStream<Uint8Array>({
    start(controller) {
      for (const chunk of chunks) controller.enqueue(encoder.encode(chunk));
      controller.close();
    },
  });
  return new Response(body, { status: 200, headers: { 'Content-Type': 'text/event-stream' } });
}

afterEach(() => vi.unstubAllGlobals());

describe('buildUrl', () => {
  it('hängt Basisadresse und Query an', () => {
    expect(buildUrl('', '/me', { role: 'shift_lead' })).toBe('/api/me?role=shift_lead');
    expect(buildUrl('https://opc.example.com', '/reports')).toBe(
      'https://opc.example.com/api/reports',
    );
  });
});

describe('createApi', () => {
  it('nutzt die Basisadresse und sendet das Zugangstoken als Authorization-Header', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ ok: true }));
    vi.stubGlobal('fetch', fetchMock);
    const api = createApi({ baseUrl: 'https://opc.example.com', getToken: () => 'abc' });

    await api.me('hall_lead');

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('https://opc.example.com/api/me?role=hall_lead');
    expect((init.headers as Record<string, string>).Authorization).toBe('Bearer abc');
  });

  it('sendet ohne Token keinen Authorization-Header', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse([]));
    vi.stubGlobal('fetch', fetchMock);
    await createApi({ baseUrl: '', getToken: () => null }).inbox('shift_lead');
    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(init.headers).not.toHaveProperty('Authorization');
  });

  it('wirft ApiError mit der Detailmeldung des Servers', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(jsonResponse({ detail: 'Unbekannte Rolle' }, 400)),
    );
    const failure = createApi({ baseUrl: '' }).me('x');
    await expect(failure).rejects.toBeInstanceOf(ApiError);
    await expect(failure).rejects.toMatchObject({ status: 400, message: 'Unbekannte Rolle' });
  });

  it('übernimmt den Code des Problem-JSON in den ApiError', async () => {
    vi.stubGlobal(
      'fetch',
      vi
        .fn()
        .mockResolvedValue(jsonResponse({ detail: 'Sitzung weg', code: 'session-not-found' }, 404)),
    );
    await expect(createApi({ baseUrl: '' }).me('x')).rejects.toMatchObject({
      status: 404,
      code: 'session-not-found',
    });
  });

  it('liest die Zusammenfassung der Reports von /api/reports/summary', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(REPORTS_SUMMARY));
    vi.stubGlobal('fetch', fetchMock);

    const summary = await createApi({ baseUrl: '' }).reportsSummary();

    expect(summary.questions.total).toBe(REPORTS_SUMMARY.questions.total);
    expect(String(fetchMock.mock.calls[0]![0])).toBe('/api/reports/summary');
  });

  it('reicht die Ereignisse von /api/ask der Reihe nach an die Handler', async () => {
    const events: string[] = [];
    const sse =
      'event: tool_call\ndata: {"tool":"t"}\n\nevent: answer_draft\ndata: {"headline":"h","todo":["a"]}\n\n' +
      'event: answer\ndata: {"headline":"h"}\n\n' +
      'event: done\ndata: {"sessionId":"s","sequence":2,"dataAsOf":"2026-10-15T07:42:00Z"}\n\n';
    // Das Ende des ersten Blocks fällt mitten in ein Ereignis.
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(streamResponse([sse.slice(0, 40), sse.slice(40)])),
    );

    await createApi({ baseUrl: '' }).ask(
      { role: 'shift_lead', question: 'Frage' },
      {
        toolCall: (call) => events.push(`tool:${call.tool}`),
        answerDraft: (draft) => events.push(`draft:${draft.headline}:${draft.todo.length}`),
        answer: (card) => events.push(`answer:${card.headline}`),
        done: (done) => events.push(`done:${done.sequence}`),
      },
    );

    expect(events).toEqual(['tool:t', 'draft:h:1', 'answer:h', 'done:2']);
  });
});

describe('Anmeldung der Live-Demo', () => {
  afterEach(() => setUnauthorizedHandler(null));

  it('ruft bei 401 den Handler auf, nicht aber für die Anmelde-Aufrufe selbst', async () => {
    const handler = vi.fn();
    setUnauthorizedHandler(handler);
    const unauthorized = (url: string) => {
      const response = jsonResponse({ detail: 'Nicht angemeldet' }, 401);
      Object.defineProperty(response, 'url', { value: url });
      return response;
    };

    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(unauthorized('http://x/api/me?role=a')));
    await expect(createApi({ baseUrl: '' }).me('a')).rejects.toMatchObject({ status: 401 });
    expect(handler).toHaveBeenCalledTimes(1);

    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(unauthorized('http://x/api/demo/login')));
    await expect(createApi({ baseUrl: '' }).demoLogin('falsch')).rejects.toMatchObject({
      status: 401,
    });
    expect(handler).toHaveBeenCalledTimes(1);
  });

  it('liest die Wartezeit bei 429 aus Retry-After, sonst aus dem Text der Antwort', async () => {
    const api = createApi({ baseUrl: '' });
    const tooMany = new Response(JSON.stringify({ detail: 'Zu viele', retryAfterSeconds: 99 }), {
      status: 429,
      headers: { 'Content-Type': 'application/json', 'Retry-After': '120' },
    });
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(tooMany));
    await expect(api.demoLogin('x')).rejects.toMatchObject({ status: 429, retryAfterSeconds: 120 });

    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(jsonResponse({ detail: 'Zu viele', retryAfterSeconds: 45 }, 429)),
    );
    await expect(api.demoLogin('x')).rejects.toMatchObject({ retryAfterSeconds: 45 });
  });

  it('sendet den Zugangscode als JSON an /api/demo/login und liest die Sitzung', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse({ token: 't', expiresAtUtc: '2026-10-15T16:00:00Z' }))
      .mockResolvedValueOnce(jsonResponse({ enabled: true, authenticated: false }));
    vi.stubGlobal('fetch', fetchMock);
    const api = createApi({ baseUrl: '', getToken: () => null });

    await expect(api.demoLogin('geheim')).resolves.toMatchObject({ token: 't' });
    await expect(api.demoSession()).resolves.toMatchObject({ enabled: true });

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('/api/demo/login');
    expect(init.method).toBe('POST');
    expect(init.body).toBe('{"code":"geheim"}');
    expect((fetchMock.mock.calls[1] as [string])[0]).toBe('/api/demo/session');
  });
});

describe('getrennte Herkunft und Aufwachen', () => {
  it('sendet bei getrennter Herkunft Cookies mit (credentials include)', async () => {
    const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(jsonResponse({})));
    vi.stubGlobal('fetch', fetchMock);
    const api = createApi({
      baseUrl: 'https://api.opc.vompa.dev',
      getToken: () => null,
      credentials: 'include',
    });

    await api.me('shift_lead');
    await api.health();

    for (const [, init] of fetchMock.mock.calls as [string, RequestInit][]) {
      expect(init.credentials).toBe('include');
    }
    expect(fetchMock.mock.calls[1]![0]).toBe('https://api.opc.vompa.dev/api/health');
  });

  it('erkennt ein schlafendes Backend an 502/503/504 und Netzwerkfehlern, nicht an 401 oder 429', () => {
    expect(isStartingUp(new ApiError(502, 'x'))).toBe(true);
    expect(isStartingUp(new ApiError(503, 'x'))).toBe(true);
    expect(isStartingUp(new ApiError(504, 'x'))).toBe(true);
    expect(isStartingUp(new TypeError('Failed to fetch'))).toBe(true);
    expect(isStartingUp(new DOMException('timeout', 'TimeoutError'))).toBe(true);
    expect(isStartingUp(new ApiError(401, 'x'))).toBe(false);
    expect(isStartingUp(new ApiError(429, 'x'))).toBe(false);
    expect(isStartingUp(new ApiError(500, 'x'))).toBe(false);
  });
});
