// Typisierter Zugriff auf die HTTP-API unter /api (JSON, camelCase) und den SSE-Strom von /api/ask.
import { API_BASE, FETCH_CREDENTIALS, getAuthToken } from '@/config';
import { ApiError, isStartingUp } from './errors';
import { notifyUnauthorized } from './session';
import { SseParser } from './sse';
import type {
  AnswerCard,
  AnswerDraft,
  AskDone,
  ChatSessionPackage,
  ChatSessionSummary,
  DemoLoginResult,
  DemoSessionState,
  FavoriteInput,
  FavoritesResponse,
  FeedResponse,
  InboxItem,
  MachineRange,
  MachineReport,
  MeResponse,
  OverviewResponse,
  ReportRef,
  ReportsSummary,
  ToolCall,
} from './types';

export { ApiError, isStartingUp };

export interface ApiClientOptions {
  baseUrl?: string;
  getToken?: () => string | null;
  credentials?: RequestCredentials;
}

/** Antwort von `GET /api/health`. */
export interface HealthState {
  status: 'healthy' | 'degraded' | 'unhealthy';
}

/** Baut die Adresse zu einem API-Pfad; gesetzte Query-Werte werden angehängt. */
export function buildUrl(
  baseUrl: string,
  path: string,
  query: Record<string, string | undefined> = {},
): string {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) if (value) params.set(key, value);
  const text = params.toString();
  return `${baseUrl}/api${path}${text ? `?${text}` : ''}`;
}

/** Kopfzeilen für alle Aufrufe: optional das Zugangstoken (`Authorization: Bearer`) plus Zusätze. */
export function buildHeaders(
  token: string | null,
  extra: Record<string, string> = {},
): Record<string, string> {
  return { ...(token ? { Authorization: `Bearer ${token}` } : {}), ...extra };
}

/** Wartezeit aus `Retry-After` (Sekunden) oder, falls der Kopf fehlt, aus dem Feld `retryAfterSeconds` der Antwort. */
function retryAfterOf(response: Response, bodySeconds: unknown): number | undefined {
  const header = Number(response.headers?.get?.('Retry-After'));
  if (Number.isFinite(header) && header > 0) return header;
  return typeof bodySeconds === 'number' && bodySeconds > 0 ? bodySeconds : undefined;
}

async function errorOf(response: Response): Promise<ApiError> {
  let detail = response.statusText;
  let bodySeconds: unknown;
  let code: string | undefined;
  try {
    const body = (await response.json()) as {
      detail?: string;
      retryAfterSeconds?: unknown;
      code?: unknown;
    };
    detail = body.detail ?? detail;
    bodySeconds = body.retryAfterSeconds;
    code = typeof body.code === 'string' ? body.code : undefined;
  } catch {
    // Antwort ohne JSON: Statustext genügt.
  }
  const error = new ApiError(
    response.status,
    detail || `HTTP ${response.status}`,
    retryAfterOf(response, bodySeconds),
    code,
  );
  // Abgelaufene oder fehlende Sitzung der Live-Demo: Die Anmeldeansicht übernimmt (die Anmelde-Aufrufe selbst ausgenommen).
  if (response.status === 401 && !response.url?.includes('/api/demo/')) notifyUnauthorized();
  return error;
}

/** Zeitgrenzen für die Aufrufe, die ein schlafendes Backend wecken (Kaltstart: der Ingress hält die Anfrage, bis zu etwa 30 s). */
export const HEALTH_TIMEOUT_MS = 20_000;
export const LOGIN_TIMEOUT_MS = 30_000;

export interface AskHandlers {
  toolCall?: (call: ToolCall) => void;
  answerDraft?: (draft: AnswerDraft) => void;
  answer?: (card: AnswerCard) => void;
  done?: (done: AskDone) => void;
  error?: (error: { message: string }) => void;
}

export function createApi({
  baseUrl = API_BASE,
  getToken = getAuthToken,
  credentials = FETCH_CREDENTIALS,
}: ApiClientOptions = {}) {
  async function request<T>(
    path: string,
    role?: string,
    init: {
      method?: string;
      body?: unknown;
      timeoutMs?: number;
      query?: Record<string, string>;
    } = {},
  ): Promise<T> {
    const hasBody = init.body !== undefined;
    const response = await fetch(buildUrl(baseUrl, path, { role, ...init.query }), {
      method: init.method ?? 'GET',
      headers: buildHeaders(getToken(), {
        Accept: 'application/json',
        ...(hasBody ? { 'Content-Type': 'application/json' } : {}),
      }),
      body: hasBody ? JSON.stringify(init.body) : undefined,
      credentials,
      signal: init.timeoutMs ? AbortSignal.timeout(init.timeoutMs) : undefined,
    });
    if (!response.ok) throw await errorOf(response);
    return (response.status === 204 ? null : await response.json()) as T;
  }

  const enc = encodeURIComponent;

  return {
    baseUrl,
    me: (role: string) => request<MeResponse>('/me', role),
    overview: (role: string) => request<OverviewResponse>('/overview', role),
    favorites: (role: string) => request<FavoritesResponse>('/favorites', role),
    saveFavorites: (role: string, items: FavoriteInput[]) =>
      request<FavoritesResponse>('/favorites', role, { method: 'PUT', body: { items } }),
    resetFavorites: (role: string) =>
      request<FavoritesResponse>('/favorites/reset', role, { method: 'POST' }),
    inbox: (role: string) => request<InboxItem[]>('/inbox', role),
    takeInboxItem: (role: string, id: string) =>
      request<InboxItem>(`/inbox/${enc(id)}/take`, role, { method: 'POST' }),
    feed: (role: string) => request<FeedResponse>('/feed', role),
    latestHistory: (role: string) => request<ChatSessionPackage>('/history/latest', role),
    sessions: (role: string) => request<ChatSessionSummary[]>('/history/sessions', role),
    session: (role: string, id: string) =>
      request<ChatSessionPackage>(`/history/sessions/${enc(id)}`, role),
    newSession: (role: string) =>
      request<ChatSessionSummary>('/history/sessions', role, { method: 'POST' }),
    recentQuestions: (role: string) => request<string[]>('/history/recent-questions', role),

    /** Anonym und ohne Datenbank-Abhängigkeit der Oberfläche: weckt ein schlafendes Backend und zeigt, wann es bereit ist. */
    health: () => request<HealthState>('/health', undefined, { timeoutMs: HEALTH_TIMEOUT_MS }),

    demoSession: () => request<DemoSessionState>('/demo/session'),
    demoLogin: (code: string) =>
      request<DemoLoginResult>('/demo/login', undefined, {
        method: 'POST',
        body: { code },
        timeoutMs: LOGIN_TIMEOUT_MS,
      }),
    demoLogout: () => request<null>('/demo/logout', undefined, { method: 'POST' }),

    /** Aggregierte Kennzahlen für die Report-Seite (ohne Frageinhalte). */
    reportsSummary: () => request<ReportsSummary>('/reports/summary'),

    /** Maschinen-Report der Halle A für einen Zeitraum, optional mit der Vorperiode. */
    reportsMachines: (role: string, range: MachineRange, compare: boolean) =>
      request<MachineReport>('/reports/machines', role, {
        query: { range, compare: String(compare) },
      }),

    /**
     * Stellt eine Frage; ruft je Ereignis (tool_call, answer_draft, answer, done, error) den passenden Handler auf.
     * `fresh` ignoriert eine gespeicherte Antwort auf dieselbe Frage („Neu fragen“); `report` verweist auf die Auffälligkeit eines Reports.
     * Das Ende des Stroms beendet die Funktion, Fehler werden als Ausnahme weitergereicht.
     */
    async ask(
      body: {
        role: string;
        question: string;
        sessionId?: string;
        fresh?: boolean;
        report?: ReportRef;
      },
      handlers: AskHandlers,
      signal?: AbortSignal,
    ): Promise<void> {
      const response = await fetch(buildUrl(baseUrl, '/ask'), {
        method: 'POST',
        headers: buildHeaders(getToken(), {
          'Content-Type': 'application/json',
          Accept: 'text/event-stream',
        }),
        body: JSON.stringify(body),
        credentials,
        signal,
      });
      if (!response.ok || !response.body) throw await errorOf(response);

      const reader = response.body.getReader();
      const decoder = new TextDecoder();
      const parser = new SseParser();
      for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        for (const message of parser.push(decoder.decode(value, { stream: true }))) {
          if (!message.data) continue;
          const data: unknown = JSON.parse(message.data);
          if (message.event === 'tool_call') handlers.toolCall?.(data as ToolCall);
          else if (message.event === 'answer_draft') handlers.answerDraft?.(data as AnswerDraft);
          else if (message.event === 'answer') handlers.answer?.(data as AnswerCard);
          else if (message.event === 'done') handlers.done?.(data as AskDone);
          else if (message.event === 'error') handlers.error?.(data as { message: string });
        }
      }
    },
  };
}

export type ApiClient = ReturnType<typeof createApi>;

/** Gemeinsame Instanz der Oberfläche. */
export const api: ApiClient = createApi();
