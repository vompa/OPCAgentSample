// Verlauf wie Claude Code: Fragen mit Antwortkarte, laufende Frage, Sitzungen.
import { defineStore } from 'pinia';
import { ref } from 'vue';
import { api, type AskHandlers } from '@/api/client';
import { ApiError } from '@/api/errors';
import type {
  AnswerCard,
  AnswerDraft,
  ChatSessionPackage,
  ChatSessionSummary,
  ChatTurnRecord,
  ReportRef,
  ToolCall,
} from '@/api/types';
import { findPreparedAnswer, type PreparedAnswer } from '@/preview/snapshot';
import { dateTimeText } from '@/utils/format';
import { useBackendStore } from './backend';
import { useIdentityStore } from './identity';
import { isMobile, useUiStore } from './ui';

export interface Turn {
  sequence: number;
  question: string;
  askedAtUtc: string;
  answerCard: AnswerCard;
  toolCalls: ToolCall[];
  /** Datenstand (Simulationsuhr), nicht die Uhrzeit des Browsers. */
  dataAsOfUtc: string;
  /** Aus dem Verlauf geladen (zeigt den damaligen Datenstand, „Neu fragen“ möglich). */
  restored: boolean;
  /** Vorschau: aus der Momentaufnahme oder als vorbereitete Antwort, nicht vom Backend. */
  preview?: boolean;
  /** Im Browser beantwortet (vorbereitete Antwort der Vorschau); bleibt beim Wechsel auf Live-Daten stehen. */
  local?: boolean;
  /** Gespeicherte Antwort auf dieselbe Frage von eben (der Server hat Claude nicht erneut gefragt). */
  repeat?: boolean;
  /** Auffälligkeit des Reports, aus der die Frage stammt; „Neu fragen“ sendet sie wieder mit (nur in dieser Sitzung des Browsers). */
  report?: ReportRef;
}

/** Optionen einer Frage: `fresh` fragt neu, `report` verweist auf die Auffälligkeit eines Reports. */
export interface AskOptions {
  fresh?: boolean;
  report?: ReportRef;
}

export interface PendingQuestion {
  question: string;
  toolCalls: ToolCall[];
  card: AnswerCard | null;
  /** Anfang der Karte (Überschrift, Was tun?), solange Claude noch schreibt; die geprüfte Karte ersetzt ihn. */
  draft?: AnswerDraft | null;
  error: string | null;
  /** Wartet in der Warteschlange, bis die Halle bereit ist; die Frage ging noch nicht raus. */
  queued?: boolean;
}

/** Code des Servers für „die Sitzung gehört nicht (mehr) zu dieser Anmeldung“. */
export const SESSION_NOT_FOUND = 'session-not-found';

/** Kennzeichnung einer vorbereiteten Antwort der Vorschau (statt des Hinweises „Claude ist nicht angebunden“). */
export const PREVIEW_NOTICE =
  'Vorschau: vorbereitete Antwort zum Stand der Momentaufnahme. Claude antwortet live, sobald die Halle bereit ist.';

export const SESSION_LOST_TEXT =
  'Die Sitzung gehört nicht mehr zu dieser Anmeldung. Bitte die Frage in einer neuen Sitzung noch einmal stellen.';

function isSessionNotFound(error: unknown): boolean {
  return error instanceof ApiError && error.status === 404 && error.code === SESSION_NOT_FOUND;
}

function describe(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function turnFromRecord(record: ChatTurnRecord, restored: boolean): Turn {
  return {
    sequence: record.sequence,
    question: record.question,
    askedAtUtc: record.askedAtUtc,
    answerCard: record.answerCard,
    toolCalls: record.toolCalls,
    dataAsOfUtc: record.dataAsOfUtc,
    restored,
  };
}

/**
 * Text des Trenners („Fortgesetzt · letzte Sitzung Do 15.10., 09:42“). Die Zeit stammt aus dem gespeicherten
 * Datenstand der letzten Frage (Simulationsuhr), nicht aus der Browser-Uhr oder dem Speicherzeitpunkt des Servers.
 */
export function dividerTextOf(prefix: string, turns: { dataAsOfUtc: string }[]): string {
  const last = turns[turns.length - 1];
  return last ? `${prefix} ${dateTimeText(last.dataAsOfUtc)}` : '';
}

export const useConversationStore = defineStore('conversation', () => {
  const turns = ref<Turn[]>([]);
  const session = ref<ChatSessionSummary | null>(null);
  const sessionId = ref<string | null>(null);
  const dividerText = ref('');
  const recentQuestions = ref<string[]>([]);
  const pending = ref<PendingQuestion | null>(null);
  const busy = ref(false);
  /** Vorbereitete Antworten der Vorschau (Schlüssel = Fragetext der Standard-Favoriten). */
  const previewAnswers = ref<Record<string, PreparedAnswer>>({});
  /** Freie Frage, die auf die Halle wartet und danach automatisch gesendet wird. */
  const queuedQuestion = ref<string | null>(null);
  const queuedReport = ref<ReportRef | null>(null);

  function applyPackage(
    sessionPackage: ChatSessionPackage,
    dividerPrefix: string,
    preview = false,
  ): void {
    session.value = sessionPackage.session;
    sessionId.value = sessionPackage.session.id;
    const restored = sessionPackage.turns.map((turn) => ({
      ...turnFromRecord(turn, true),
      ...(preview ? { preview: true } : {}),
    }));
    // Im Browser beantwortete Fragen der Vorschau bleiben stehen, wenn die Live-Daten kommen.
    const carried = preview ? [] : turns.value.filter((turn) => turn.local);
    const next = restored.at(-1)?.sequence ?? 0;
    turns.value = [
      ...restored,
      ...carried.map((turn, index) => ({ ...turn, sequence: next + index + 1 })),
    ];
    dividerText.value = dividerTextOf(dividerPrefix, restored);
  }

  /** Setzt die Daten der Rolle (Start und Rollenwechsel); `preview`: Momentaufnahme oder Zwischenspeicher statt Live-Daten. */
  function hydrate(latest: ChatSessionPackage, recent: string[], preview = false): void {
    if (!queuedQuestion.value) {
      pending.value = null;
      busy.value = false;
    }
    recentQuestions.value = recent;
    applyPackage(latest, 'Fortgesetzt · letzte Sitzung', preview);
  }

  /** Verwirft den Verlauf (Abmelden, abgelaufene Sitzung): Sitzung und Fragen gehören dem bisherigen Besitzer. */
  function clear(): void {
    turns.value = [];
    session.value = null;
    sessionId.value = null;
    dividerText.value = '';
    recentQuestions.value = [];
    pending.value = null;
    busy.value = false;
    previewAnswers.value = {};
    queuedQuestion.value = null;
    queuedReport.value = null;
  }

  /**
   * Vorschau (Backend schläft): Eine Standard-Favoritenfrage beantwortet die vorbereitete Antwort sofort, gekennzeichnet als
   * Vorschau. Jede andere Frage wartet in der Warteschlange und geht automatisch raus, sobald die Halle bereit ist.
   */
  function askPreview(question: string, report?: ReportRef): void {
    const ui = useUiStore();
    const prepared = findPreparedAnswer(previewAnswers.value, question);
    const asOf = useIdentityStore().me?.clock.utcNow ?? new Date().toISOString();
    ui.showAsk();
    if (prepared) {
      const next = (turns.value.at(-1)?.sequence ?? 0) + 1;
      turns.value.push({
        sequence: next,
        question,
        askedAtUtc: asOf,
        answerCard: { ...prepared.card, notice: PREVIEW_NOTICE },
        toolCalls: prepared.toolCalls,
        dataAsOfUtc: asOf,
        restored: false,
        preview: true,
        local: true,
      });
      return;
    }
    queuedQuestion.value = question;
    queuedReport.value = report ?? null;
    busy.value = true;
    pending.value = { question, toolCalls: [], card: null, draft: null, error: null, queued: true };
  }

  /** Sendet die wartende Frage, sobald die Halle bereit ist (Live-Daten sind geladen). */
  async function flushQueue(): Promise<void> {
    const question = queuedQuestion.value;
    if (!question) return;
    const report = queuedReport.value;
    queuedQuestion.value = null;
    queuedReport.value = null;
    pending.value = null;
    busy.value = false;
    await ask(question, report ? { report } : {});
  }

  /**
   * Die Sitzung des Clients gehört dem Server zufolge nicht (mehr) zum aktuellen Besitzer, etwa weil nach einer neuen
   * Anmeldung der Verlauf noch nicht neu geladen war. Lädt die Sitzung des Besitzers und gibt zurück, ob die Frage
   * erneut gestellt werden darf (nur mit einer eigenen Sitzung; nie in eine fremde).
   */
  async function recoverSession(): Promise<boolean> {
    try {
      const latest = await api.latestHistory(useIdentityStore().role);
      applyPackage(latest, 'Fortgesetzt · letzte Sitzung');
      return true;
    } catch {
      return false;
    }
  }

  /**
   * Stellt eine Frage: tool_call -> answer_draft -> answer -> done; danach ist der Turn auf dem Server gespeichert.
   * `fresh` („Neu fragen“) umgeht die gespeicherte Antwort auf dieselbe Frage. `report` benennt die Auffälligkeit eines Reports,
   * aus der die Frage stammt; den Kontext (Zeitraum, Werte) berechnet der Server.
   */
  async function ask(rawQuestion: string, options: AskOptions = {}): Promise<void> {
    const question = rawQuestion.trim();
    if (!question || busy.value) return;
    if (!useBackendStore().ready) {
      askPreview(question, options.report);
      return;
    }

    const identity = useIdentityStore();
    busy.value = true;
    pending.value = { question, toolCalls: [], card: null, draft: null, error: null };
    useUiStore().showAsk();

    let finished = false;
    let answerCard: AnswerCard | null = null; // bleibt erhalten, auch wenn der Verlauf zwischendurch neu geladen wird
    const handlers: AskHandlers = {
      toolCall: (call) => pending.value?.toolCalls.push(call),
      answerDraft: (draft) => {
        if (pending.value) pending.value.draft = draft;
      },
      answer: (card) => {
        answerCard = card;
        if (pending.value) {
          pending.value.card = card;
          pending.value.draft = null; // die geprüfte Karte (oder die Fehlerkarte) ersetzt den Entwurf
        }
      },
      done: (done) => {
        finished = true;
        // Die Nummer des Servers kann mit einer im Browser beantworteten Vorschau-Frage zusammenfallen: Schlüssel bleiben eindeutig.
        const last = turns.value.at(-1)?.sequence ?? 0;
        turns.value.push({
          sequence: Math.max(done.sequence, last + 1),
          question,
          askedAtUtc: done.dataAsOf,
          answerCard: (answerCard ?? pending.value?.card) as AnswerCard,
          toolCalls: pending.value?.toolCalls ?? [],
          dataAsOfUtc: done.dataAsOf,
          restored: false,
          ...(done.repeat ? { repeat: true } : {}),
          ...(options.report ? { report: options.report } : {}),
        });
        sessionId.value = done.sessionId;
        pending.value = null;
      },
      error: (data) => {
        if (pending.value) pending.value.error = data.message;
      },
    };
    const send = () =>
      api.ask(
        {
          role: identity.role,
          question,
          sessionId: sessionId.value ?? undefined,
          ...(options.fresh ? { fresh: true } : {}),
          ...(options.report ? { report: options.report } : {}),
        },
        handlers,
      );

    try {
      try {
        await send();
      } catch (error) {
        // Genau ein Neuversuch, und nur mit der geladenen Sitzung des aktuellen Besitzers.
        if (!isSessionNotFound(error)) throw error;
        if (!(await recoverSession())) throw new Error(SESSION_LOST_TEXT);
        if (pending.value) pending.value.toolCalls = [];
        try {
          await send();
        } catch (retryError) {
          throw isSessionNotFound(retryError) ? new Error(SESSION_LOST_TEXT) : retryError;
        }
      }
      if (!finished && pending.value && !pending.value.error) {
        pending.value.error = 'Die Antwort wurde unterbrochen.';
      }
    } catch (error) {
      if (pending.value) pending.value.error = describe(error);
    } finally {
      busy.value = false;
    }
    await refreshRecentQuestions();
  }

  async function refreshRecentQuestions(): Promise<void> {
    try {
      recentQuestions.value = await api.recentQuestions(useIdentityStore().role);
    } catch {
      // Die Liste ist eine Bequemlichkeit.
    }
  }

  async function startNewSession(): Promise<void> {
    if (busy.value) return;
    const ui = useUiStore();
    if (!useBackendStore().ready) {
      ui.toast('Neue Sitzungen sind möglich, sobald die Halle bereit ist.');
      return;
    }
    try {
      const created = await api.newSession(useIdentityStore().role);
      session.value = created;
      sessionId.value = created.id;
      turns.value = [];
      dividerText.value = '';
      pending.value = null; // der Leerzustand im Verlauf meldet den Neustart, kein zusätzlicher Hinweis
    } catch (error) {
      ui.toast(`Neue Sitzung fehlgeschlagen: ${describe(error)}`);
    }
  }

  function listSessions(): Promise<ChatSessionSummary[]> {
    if (!useBackendStore().ready) return Promise.resolve(session.value ? [session.value] : []);
    return api.sessions(useIdentityStore().role);
  }

  async function openSession(id: string): Promise<void> {
    const ui = useUiStore();
    if (!useBackendStore().ready) return;
    try {
      applyPackage(await api.session(useIdentityStore().role, id), 'Frühere Sitzung ·');
      pending.value = null;
      ui.showAsk();
    } catch (error) {
      ui.toast(`Sitzung konnte nicht geladen werden: ${describe(error)}`);
    }
  }

  /** Fragt aus Postfach, Favoritenband oder Report: am Handy wechselt die Ansicht zu „Fragen“. */
  function askFrom(question: string, options: AskOptions = {}): Promise<void> {
    if (isMobile()) useUiStore().setMobileView('ask');
    return ask(question, options);
  }

  return {
    turns,
    session,
    sessionId,
    dividerText,
    recentQuestions,
    pending,
    busy,
    previewAnswers,
    queuedQuestion,
    hydrate,
    clear,
    ask,
    flushQueue,
    askFrom,
    refreshRecentQuestions,
    startNewSession,
    listSessions,
    openSession,
  };
});
