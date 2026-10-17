import { createPinia, setActivePinia } from 'pinia';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { AskHandlers } from '@/api/client';
import { ApiError } from '@/api/errors';
import { apiMock, resetApiMock } from '@/test/apiMock';
import { CARD, ME, TOOL_CALLS, sessionPackage } from '@/test/fixtures';
import { SESSION_LOST_TEXT, dividerTextOf, useConversationStore } from './conversation';
import { useIdentityStore } from './identity';
import { useUiStore } from './ui';

vi.mock('@/api/client', () => ({ api: apiMock }));

beforeEach(() => {
  setActivePinia(createPinia());
  resetApiMock();
  useIdentityStore().me = ME;
  apiMock.recentQuestions.mockResolvedValue(['Schichtübergabe?']);
});

describe('Trenner „Fortgesetzt · letzte Sitzung“', () => {
  it('nutzt den gespeicherten Datenstand (Simulationsuhr), nicht Server- oder Browser-Zeit', () => {
    const conversation = useConversationStore();
    conversation.hydrate(sessionPackage('2026-10-14T13:30:00Z'), []);

    // 13:30 UTC = 15:30 Wien (Sommerzeit); die Serverzeiten der Sitzung (2030) und die Browser-Uhr kommen nicht vor.
    expect(conversation.dividerText).toBe('Fortgesetzt · letzte Sitzung Mi 14.10., 15:30');
  });

  it('bleibt ohne Fragen leer', () => {
    expect(dividerTextOf('Fortgesetzt · letzte Sitzung', [])).toBe('');
  });
});

describe('ask', () => {
  it('sammelt Tool-Aufrufe und Antwort und speichert den Turn mit dem Datenstand des Servers', async () => {
    apiMock.ask.mockImplementation(async (_body: unknown, handlers: AskHandlers) => {
      handlers.toolCall?.(TOOL_CALLS[0]!);
      handlers.answer?.(CARD);
      handlers.done?.({
        sessionId: 's9',
        sequence: 3,
        tokens: { input: 1, output: 1, cacheRead: 0, cacheWrite: 0 },
        dataAsOf: '2026-10-15T07:44:00Z',
        dataAsOfText: '09:44',
      });
    });
    const conversation = useConversationStore();

    await conversation.ask('  Wie läuft RT-01?  ');

    expect(apiMock.ask).toHaveBeenCalledWith(
      { role: 'shift_lead', question: 'Wie läuft RT-01?', sessionId: undefined },
      expect.any(Object),
    );
    const [turn] = conversation.turns;
    expect(turn).toMatchObject({
      question: 'Wie läuft RT-01?',
      dataAsOfUtc: '2026-10-15T07:44:00Z',
      askedAtUtc: '2026-10-15T07:44:00Z', // Simulationsuhr statt new Date()
      restored: false,
    });
    expect(turn?.toolCalls).toHaveLength(1);
    expect(conversation.pending).toBeNull();
    expect(conversation.busy).toBe(false);
    expect(conversation.sessionId).toBe('s9');
    expect(conversation.recentQuestions).toEqual(['Schichtübergabe?']);
  });

  it('zeigt den Entwurf der Karte, sobald er eintrifft, und ersetzt ihn durch die geprüfte Karte', async () => {
    let seenDuringStream: unknown;
    apiMock.ask.mockImplementation(async (_body: unknown, handlers: AskHandlers) => {
      handlers.toolCall?.(TOOL_CALLS[0]!);
      handlers.answerDraft?.({ headline: 'RT-01 läuft unter Soll.', todo: [] });
      handlers.answerDraft?.({ headline: 'RT-01 läuft unter Soll.', todo: ['Prüfen'] });
      seenDuringStream = { ...useConversationStore().pending };
      handlers.answer?.(CARD);
      expect(useConversationStore().pending?.draft).toBeNull();
      handlers.done?.({
        sessionId: 's9',
        sequence: 1,
        tokens: { input: 1, output: 1, cacheRead: 0, cacheWrite: 0 },
        dataAsOf: '2026-10-15T07:44:00Z',
        dataAsOfText: '09:44',
      });
    });
    const conversation = useConversationStore();

    await conversation.ask('Frage');

    expect(seenDuringStream).toMatchObject({
      draft: { headline: 'RT-01 läuft unter Soll.', todo: ['Prüfen'] },
    });
    expect(conversation.turns[0]?.answerCard).toEqual(CARD);
    expect(conversation.turns[0]?.repeat).toBeUndefined();
  });

  it('merkt sich eine Wiederholung und schickt „fresh“ nur bei „Neu fragen“', async () => {
    apiMock.ask.mockImplementation(async (_body: unknown, handlers: AskHandlers) => {
      handlers.answer?.(CARD);
      handlers.done?.({
        sessionId: 's9',
        sequence: 2,
        tokens: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
        dataAsOf: '2026-10-15T07:44:00Z',
        dataAsOfText: '09:44',
        repeat: true,
      });
    });
    const conversation = useConversationStore();

    await conversation.ask('Frage');
    await conversation.ask('Frage', { fresh: true });

    expect(conversation.turns[0]?.repeat).toBe(true);
    expect(apiMock.ask.mock.calls[0]![0]).not.toHaveProperty('fresh');
    expect(apiMock.ask.mock.calls[1]![0]).toMatchObject({ fresh: true });
  });

  it('schickt den Report-Verweis mit und merkt ihn für „Neu fragen“', async () => {
    apiMock.ask.mockImplementation(async (_body: unknown, handlers: AskHandlers) => {
      handlers.answer?.(CARD);
      handlers.done?.({
        sessionId: 's9',
        sequence: 1,
        tokens: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
        dataAsOf: '2026-10-15T07:44:00Z',
        dataAsOfText: '09:44',
      });
    });
    const conversation = useConversationStore();
    const report = { kind: 'machines', range: 'fourWeeks', findingId: 'oee-RT-03' } as const;

    await conversation.ask('Warum?', { report });

    expect(apiMock.ask.mock.calls[0]![0]).toMatchObject({ question: 'Warum?', report });
    expect(conversation.turns[0]?.report).toEqual(report);
  });

  it('sendet ohne Report-Verweis kein Feld „report“', async () => {
    apiMock.ask.mockResolvedValue(undefined);
    const conversation = useConversationStore();

    await conversation.ask('Frage');

    expect(apiMock.ask.mock.calls[0]![0]).not.toHaveProperty('report');
  });

  it('lässt den Entwurf bei einem Fehler nicht als Antwort stehen', async () => {
    apiMock.ask.mockImplementation(async (_body: unknown, handlers: AskHandlers) => {
      handlers.answerDraft?.({ headline: 'Halbe Antwort', todo: [] });
      handlers.error?.({ message: 'Abbruch' });
    });
    const conversation = useConversationStore();

    await conversation.ask('Frage');

    expect(conversation.pending?.error).toBe('Abbruch');
    expect(conversation.turns).toHaveLength(0);
  });

  it('zeigt Fehler der Verbindung an der laufenden Frage', async () => {
    apiMock.ask.mockRejectedValue(new Error('Kontingent erschöpft'));
    const conversation = useConversationStore();

    await conversation.ask('Frage');

    expect(conversation.pending?.error).toBe('Kontingent erschöpft');
    expect(conversation.turns).toHaveLength(0);
    expect(conversation.busy).toBe(false);
  });

  it('meldet einen Strom ohne done als unterbrochen', async () => {
    apiMock.ask.mockResolvedValue(undefined);
    const conversation = useConversationStore();
    await conversation.ask('Frage');
    expect(conversation.pending?.error).toBe('Die Antwort wurde unterbrochen.');
  });

  it('ignoriert leere Fragen und parallele Fragen', async () => {
    const conversation = useConversationStore();
    await conversation.ask('   ');
    expect(apiMock.ask).not.toHaveBeenCalled();

    conversation.busy = true;
    await conversation.ask('Frage');
    expect(apiMock.ask).not.toHaveBeenCalled();
  });
});

describe('ask mit fremder Sitzung (session-not-found)', () => {
  const notFound = () =>
    new ApiError(
      404,
      'Die Sitzung gehört nicht zu dieser Anmeldung.',
      undefined,
      'session-not-found',
    );
  const answer = (handlers: AskHandlers) => {
    handlers.answer?.(CARD);
    handlers.done?.({
      sessionId: 's1',
      sequence: 2,
      tokens: { input: 1, output: 1, cacheRead: 0, cacheWrite: 0 },
      dataAsOf: '2026-10-15T07:44:00Z',
      dataAsOfText: '09:44',
    });
  };

  it('lädt den Verlauf neu und stellt die Frage einmal in der Sitzung des Besitzers erneut', async () => {
    apiMock.latestHistory.mockResolvedValue(sessionPackage());
    apiMock.ask
      .mockRejectedValueOnce(notFound())
      .mockImplementationOnce(async (_body: unknown, handlers: AskHandlers) => answer(handlers));
    const conversation = useConversationStore();
    conversation.hydrate(
      { ...sessionPackage(), session: { ...sessionPackage().session, id: 'alt' } },
      [],
    );

    await conversation.ask('Hallenlage');

    expect(apiMock.ask).toHaveBeenCalledTimes(2);
    expect(apiMock.ask.mock.calls[0]![0]).toMatchObject({ sessionId: 'alt' });
    expect(apiMock.ask.mock.calls[1]![0]).toMatchObject({ sessionId: 's1' });
    expect(conversation.pending).toBeNull();
    expect(conversation.turns.at(-1)?.question).toBe('Hallenlage');
    expect(conversation.sessionId).toBe('s1');
  });

  it('versucht es nicht endlos: nach dem zweiten Fehler steht ein verständlicher deutscher Text', async () => {
    apiMock.latestHistory.mockResolvedValue(sessionPackage());
    apiMock.ask.mockRejectedValue(notFound());
    const conversation = useConversationStore();

    await conversation.ask('Hallenlage');

    expect(apiMock.ask).toHaveBeenCalledTimes(2);
    expect(apiMock.latestHistory).toHaveBeenCalledTimes(1);
    expect(conversation.pending?.error).toBe(SESSION_LOST_TEXT);
    expect(conversation.busy).toBe(false);
  });

  it('meldet den Fehler, wenn der Verlauf nicht neu geladen werden kann (kein zweiter Aufruf)', async () => {
    apiMock.latestHistory.mockRejectedValue(new Error('offline'));
    apiMock.ask.mockRejectedValue(notFound());
    const conversation = useConversationStore();

    await conversation.ask('Hallenlage');

    expect(apiMock.ask).toHaveBeenCalledTimes(1);
    expect(conversation.pending?.error).toBe(SESSION_LOST_TEXT);
  });

  it('wiederholt andere 404 ohne Code nicht', async () => {
    apiMock.ask.mockRejectedValue(new ApiError(404, 'Not Found'));
    const conversation = useConversationStore();

    await conversation.ask('Hallenlage');

    expect(apiMock.ask).toHaveBeenCalledTimes(1);
    expect(apiMock.latestHistory).not.toHaveBeenCalled();
  });

  it('clear verwirft Sitzung und Fragen des bisherigen Besitzers', () => {
    const conversation = useConversationStore();
    conversation.hydrate(sessionPackage(), ['Frage']);

    conversation.clear();

    expect(conversation.sessionId).toBeNull();
    expect(conversation.turns).toHaveLength(0);
    expect(conversation.recentQuestions).toHaveLength(0);
  });
});

describe('startNewSession', () => {
  it('leert den Verlauf ohne zusätzlichen Hinweis, weil der Leerzustand den Neustart zeigt', async () => {
    apiMock.newSession.mockResolvedValue({ ...sessionPackage().session, id: 's2', turnCount: 0 });
    const conversation = useConversationStore();
    conversation.hydrate(sessionPackage(), []);

    await conversation.startNewSession();

    expect(conversation.turns).toHaveLength(0);
    expect(conversation.sessionId).toBe('s2');
    expect(useUiStore().toastMessage).toBe('');
  });
});

describe('hydrate (Neuladen)', () => {
  it('zeigt alle Fragen der letzten Sitzung, nicht nur die letzte', () => {
    const base = sessionPackage();
    const turns = [1, 2, 3, 4].map((sequence) => ({
      ...base.turns[0]!,
      sequence,
      question: `Frage ${sequence}`,
    }));
    const conversation = useConversationStore();

    conversation.hydrate({ ...base, turns }, []);

    expect(conversation.turns.map((turn) => turn.question)).toEqual([
      'Frage 1',
      'Frage 2',
      'Frage 3',
      'Frage 4',
    ]);
  });
});
