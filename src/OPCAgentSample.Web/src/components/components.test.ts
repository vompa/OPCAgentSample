import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiMock, resetApiMock } from '@/test/apiMock';
import { CARD, FAVORITES, ME, REPORTS_SUMMARY, TOOL_CALLS, sessionPackage } from '@/test/fixtures';
import { useConversationStore, type Turn } from '@/stores/conversation';
import { useFavoritesStore } from '@/stores/favorites';
import { useIdentityStore } from '@/stores/identity';
import { useUiStore } from '@/stores/ui';
import AnswerCardView from './AnswerCardView.vue';
import FavoritesBand from './FavoritesBand.vue';
import ReportsView from './ReportsView.vue';
import TracePanel from './TracePanel.vue';

vi.mock('@/api/client', () => ({ api: apiMock }));

const TURN: Turn = {
  sequence: 1,
  question: 'Schichtübergabe?',
  askedAtUtc: '2026-10-15T07:42:00Z',
  answerCard: CARD,
  toolCalls: TOOL_CALLS,
  dataAsOfUtc: '2026-10-15T07:42:00Z',
  restored: true,
};

beforeEach(() => {
  setActivePinia(createPinia());
  resetApiMock();
  useIdentityStore().me = ME;
});

describe('AnswerCardView', () => {
  it('zeigt die Antwortkarte in fester Reihenfolge mit Statuswort und Datenstand', () => {
    const wrapper = mount(AnswerCardView, { props: { turn: TURN } });
    const text = wrapper.text();

    const order = [
      'RT-01 läuft unter Soll.',
      'Was tun?',
      'Die Werte',
      'Ausschuss',
      'Warum',
      'Datenstand 09:42',
    ];
    const positions = order.map((part) => text.indexOf(part));
    expect(positions.every((p) => p >= 0)).toBe(true);
    expect([...positions].sort((a, b) => a - b)).toEqual(positions);
    expect(wrapper.find('.badge-warn').text()).toBe('Warnung'); // Bewertung auch als Wort, nicht nur Farbe
    expect(text).toContain('Quellen: OPC HistoryRead');
    expect(wrapper.find('.bar-fill').attributes('style')).toContain('width: 25%');
  });

  it('klappt die Tool-Aufrufe auf und zu', async () => {
    const wrapper = mount(AnswerCardView, { props: { turn: TURN } });
    const toggle = wrapper.find('.tools-toggle');
    expect(toggle.attributes('aria-expanded')).toBe('false');
    expect(wrapper.find('.tools-list').attributes('style')).toContain('display: none');

    await toggle.trigger('click');
    expect(toggle.attributes('aria-expanded')).toBe('true');
    expect(wrapper.find('.tools-list').attributes('style') ?? '').not.toContain('display: none');
    expect(wrapper.find('.tools-list').text()).toContain('get_station_status');
  });

  it('zeigt bei Antworten aus dem Verlauf Stand und „Neu fragen“', async () => {
    apiMock.ask.mockResolvedValue(undefined);
    apiMock.recentQuestions.mockResolvedValue([]);
    const wrapper = mount(AnswerCardView, { props: { turn: TURN } });

    expect(wrapper.find('.stale-chip').text()).toBe('Stand 09:42');
    await wrapper.find('.requery-btn').trigger('click');
    expect(apiMock.ask).toHaveBeenCalledWith(
      expect.objectContaining({ question: 'Schichtübergabe?', fresh: true }),
      expect.any(Object),
    );
  });

  it('kennzeichnet eine Wiederholung und fragt mit „Neu fragen“ ohne gespeicherte Antwort', async () => {
    apiMock.ask.mockResolvedValue(undefined);
    apiMock.recentQuestions.mockResolvedValue([]);
    const wrapper = mount(AnswerCardView, {
      props: { turn: { ...TURN, restored: false, repeat: true } },
    });

    expect(wrapper.find('.chip-repeat').text()).toBe('Wiederholung · gleiche Antwort wie eben');
    expect(wrapper.find('.stale-chip').exists()).toBe(false);
    await wrapper.find('.requery-btn').trigger('click');
    expect(apiMock.ask).toHaveBeenCalledWith(
      expect.objectContaining({ question: 'Schichtübergabe?', fresh: true }),
      expect.any(Object),
    );
  });

  it('sendet bei „Neu fragen“ den Report-Verweis der Frage erneut mit', async () => {
    apiMock.ask.mockResolvedValue(undefined);
    apiMock.recentQuestions.mockResolvedValue([]);
    const report = { kind: 'machines', range: 'week', findingId: 'loss-top' } as const;
    const wrapper = mount(AnswerCardView, { props: { turn: { ...TURN, report } } });

    await wrapper.find('.requery-btn').trigger('click');

    expect(apiMock.ask).toHaveBeenCalledWith(
      expect.objectContaining({ question: 'Schichtübergabe?', fresh: true, report }),
      expect.any(Object),
    );
  });

  it('zeigt ohne Wiederholung keinen Hinweis und keinen Knopf', () => {
    const wrapper = mount(AnswerCardView, { props: { turn: { ...TURN, restored: false } } });

    expect(wrapper.find('.chip-repeat').exists()).toBe(false);
    expect(wrapper.find('.requery-btn').exists()).toBe(false);
  });

  it('kennzeichnet vorbereitete Antworten', () => {
    const wrapper = mount(AnswerCardView, {
      props: {
        turn: { ...TURN, restored: false, answerCard: { ...CARD, notice: 'Beispielantwort' } },
      },
    });
    expect(wrapper.find('.chip-notice').text()).toBe('Vorbereitet');
    expect(wrapper.find('.card-notice').text()).toBe('Beispielantwort');
    expect(wrapper.find('.stale').exists()).toBe(false);
  });
});

describe('TracePanel (MCP-Band)', () => {
  it('markiert verweigerte Aufrufe dezent als „Verweigert (Rolle)“ und nicht als Fehler', () => {
    useConversationStore().hydrate(sessionPackage(), []);
    const wrapper = mount(TracePanel);

    const calls = wrapper.findAll('.trace-call');
    expect(calls).toHaveLength(2);
    expect(calls[0]!.classes()).not.toContain('is-denied');
    expect(calls[1]!.classes()).toContain('is-denied');
    expect(calls[1]!.text()).toContain('Verweigert (Rolle)');
    expect(wrapper.text()).not.toContain('Fehler');
    expect(wrapper.find('.trace-total').text()).toBe('2 Aufrufe in dieser Sitzung');
  });

  it('zeigt laufende Aufrufe der aktuellen Frage und lässt sich ausblenden', async () => {
    const conversation = useConversationStore();
    conversation.pending = { question: 'Neue Frage', toolCalls: [], card: null, error: null };
    const wrapper = mount(TracePanel);
    expect(wrapper.text()).toContain('Claude wählt die Tools …');

    useUiStore().traceVisible = false;
    await flushPromises();
    expect(wrapper.attributes('hidden')).toBeDefined();
  });
});

describe('FavoritesBand', () => {
  function mountBand() {
    useFavoritesStore().favorites = structuredClone(FAVORITES);
    return mount(FavoritesBand);
  }

  it('zeigt Favoriten mit Tooltip der vollen Frage und stellt die Frage beim Klick', async () => {
    const conversation = useConversationStore();
    const askFrom = vi.fn().mockResolvedValue(undefined);
    conversation.askFrom = askFrom;
    const wrapper = mountBand();

    const buttons = wrapper.findAll('.fav');
    expect(buttons.map((b) => b.text())).toEqual(['Übergabe', 'Eigener']);
    expect(buttons[0]!.attributes('title')).toBe(FAVORITES.items[0]!.question);
    expect(wrapper.find('.fav-count').text()).toBe('2 / 3');

    await buttons[1]!.trigger('click');
    expect(askFrom).toHaveBeenCalledWith('Wie läuft RT-02?');
  });

  it('entfernt im Bearbeitungsmodus einen Favoriten und speichert die Restliste', async () => {
    apiMock.saveFavorites.mockResolvedValue({ ...FAVORITES, items: [FAVORITES.items[0]] });
    const wrapper = mountBand();
    expect(wrapper.find('.fav-remove').exists()).toBe(false);

    await wrapper.find('.fav-edit-btn').trigger('click');
    expect(wrapper.find('.fav-edit-btn').attributes('aria-expanded')).toBe('true');
    const remove = wrapper.findAll('.fav-remove')[1]!;
    expect(remove.attributes('aria-label')).toBe('Favorit 2 entfernen: Eigener');
    await remove.trigger('click');

    await vi.waitFor(() => expect(apiMock.saveFavorites).toHaveBeenCalledTimes(1));
    expect(apiMock.saveFavorites).toHaveBeenCalledWith('shift_lead', [
      { shortTitle: 'Übergabe', question: FAVORITES.items[0]!.question },
    ]);
  });

  it('fügt über das Bearbeitungsfeld hinzu und begrenzt auf die freien Plätze', async () => {
    apiMock.saveFavorites.mockResolvedValue(FAVORITES);
    const wrapper = mountBand();
    await wrapper.find('.fav-edit-btn').trigger('click');

    expect(wrapper.find('.fav-limit').text()).toContain('Noch 1 von 3 Plätzen frei');
    await wrapper.find('#new-short').setValue('RT-03');
    await wrapper.find('#new-question').setValue('Wie läuft RT-03?');
    await wrapper.find('#fav-editor').trigger('submit');

    await vi.waitFor(() => expect(apiMock.saveFavorites).toHaveBeenCalledTimes(1));
    const items = apiMock.saveFavorites.mock.calls[0]![1] as { shortTitle: string }[];
    expect(items.map((i) => i.shortTitle)).toEqual(['Übergabe', 'Eigener', 'RT-03']);
  });
});

describe('ReportsView', () => {
  function show() {
    useUiStore().deskReports = true;
    useUiStore().reportsTab = 'usage';
    return mount(ReportsView);
  }

  function summaryWith(patch: Partial<typeof REPORTS_SUMMARY>) {
    return { ...REPORTS_SUMMARY, ...patch };
  }

  it('zeigt Kacheln, Diagramme mit Tabellen-Alternative und die Werkzeug-Tabelle', async () => {
    apiMock.reportsSummary.mockResolvedValue(REPORTS_SUMMARY);
    const wrapper = show();
    await flushPromises();

    const tiles = wrapper.find('.rp-tiles').text();
    expect(tiles).toContain('Fragen gesamt');
    expect(tiles).toContain('12');
    expect(tiles).toContain('Geschätzte Kosten');
    expect(tiles).toContain('Cache-Trefferquote');
    expect(tiles).toContain('75 %');
    expect(wrapper.findAll('figure.rp-chart')).toHaveLength(3); // Antwortzeiten, Fragen, Tokens
    expect(wrapper.findAll('figure.chart')).toHaveLength(1); // Aufrufe je Werkzeug
    expect(wrapper.findAll('details.rp-table-toggle').length).toBeGreaterThanOrEqual(3);
    const rows = wrapper.findAll('.rp-tools tbody tr');
    expect(rows).toHaveLength(REPORTS_SUMMARY.tools.length);
    expect(rows[0]!.text()).toContain('Schichtleiter');
    expect(wrapper.text()).toContain('Verweigert');
    expect(wrapper.find('iframe').exists()).toBe(false);
  });

  it('zeigt den Leerzustand ohne Fragen', async () => {
    apiMock.reportsSummary.mockResolvedValue(
      summaryWith({
        questions: { total: 0, today: 0, last24Hours: 0, answeredByClaude: 0 },
        latency: { measured: 0, averageMs: null, p50Ms: null, p95Ms: null },
        tools: [],
      }),
    );
    const wrapper = show();
    await flushPromises();

    expect(wrapper.text()).toContain('Noch keine Fragen gestellt.');
    expect(wrapper.text()).toContain('Noch keine Werkzeug-Aufrufe.');
    expect(wrapper.findAll('figure.rp-chart')).toHaveLength(0);
    expect(wrapper.find('[role="alert"]').exists()).toBe(false);
  });

  it('zeigt statt des Token-Verlaufs einen Hinweis, solange keine Tokens verbraucht wurden', async () => {
    apiMock.reportsSummary.mockResolvedValue(
      summaryWith({ tokens: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } }),
    );
    const wrapper = show();
    await flushPromises();

    expect(wrapper.text()).toContain('Noch keine Tokens verbraucht');
    expect(wrapper.findAll('figure.rp-chart')).toHaveLength(2); // Antwortzeiten, Fragen
  });

  it('zeigt den Ladezustand und danach einen Ladefehler mit Wiederholen', async () => {
    apiMock.reportsSummary.mockRejectedValueOnce(new Error('Server nicht erreichbar'));
    const wrapper = show();
    expect(wrapper.find('[role="status"]').text()).toContain('Reports werden geladen');
    await flushPromises();
    expect(wrapper.find('[role="alert"]').text()).toContain('Server nicht erreichbar');

    apiMock.reportsSummary.mockResolvedValueOnce(REPORTS_SUMMARY);
    await wrapper.find('[role="alert"] button').trigger('click');
    await flushPromises();
    expect(wrapper.find('.rp-tiles').exists()).toBe(true);
  });

  it('behält bei einem Fehler der Aktualisierung die alten Werte und weist darauf hin', async () => {
    apiMock.reportsSummary.mockResolvedValueOnce(REPORTS_SUMMARY);
    const wrapper = show();
    await flushPromises();

    apiMock.reportsSummary.mockRejectedValueOnce(new Error('Zeitüberschreitung'));
    await wrapper.find('.reports-actions button').trigger('click');
    await flushPromises();

    expect(wrapper.find('.rp-tiles').exists()).toBe(true);
    expect(wrapper.find('.reports-stale').text()).toContain('Zeitüberschreitung');
  });

  it('aktualisiert alle 30 Sekunden, solange die Seite sichtbar ist', async () => {
    vi.useFakeTimers();
    try {
      apiMock.reportsSummary.mockResolvedValue(REPORTS_SUMMARY);
      const ui = useUiStore();
      ui.deskReports = true;
      ui.reportsTab = 'usage';
      const wrapper = mount(ReportsView);
      await flushPromises();
      expect(apiMock.reportsSummary).toHaveBeenCalledTimes(1);

      await vi.advanceTimersByTimeAsync(30_000);
      expect(apiMock.reportsSummary).toHaveBeenCalledTimes(2);

      ui.deskReports = false;
      await flushPromises();
      await vi.advanceTimersByTimeAsync(60_000);
      expect(apiMock.reportsSummary).toHaveBeenCalledTimes(2);
      wrapper.unmount();
    } finally {
      vi.useRealTimers();
    }
  });

  it('wählt im Diagramm per Pfeiltaste einen Wert und zeigt ihn über dem Diagramm', async () => {
    apiMock.reportsSummary.mockResolvedValue(REPORTS_SUMMARY);
    const wrapper = show();
    await flushPromises();

    const histogram = wrapper.find('.rp-columns');
    await histogram.trigger('focus');
    const before = wrapper.find('.rp-readout').text();
    await histogram.trigger('keydown', { key: 'Home' });
    expect(wrapper.find('.rp-readout').text()).toContain('unter 0,25 s');
    expect(wrapper.find('.rp-readout').text()).not.toBe(before);
  });
});
