// Desktop-Optimierung: Scroll-Ziel des Verlaufs, Fenster über der Statusleiste, Favoritenband, „Keine Daten“,
// Kennungen ohne Umbruch, eingeklappte MCP-Blöcke und Rückweg aus den Reports.
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { OverviewResponse } from '@/api/types';
import { apiMock, resetApiMock } from '@/test/apiMock';
import { FAVORITES, ME, REPORTS_SUMMARY, TOOL_CALLS, sessionPackage } from '@/test/fixtures';
import { useConversationStore } from '@/stores/conversation';
import { useFavoritesStore } from '@/stores/favorites';
import { useIdentityStore } from '@/stores/identity';
import { useInboxStore } from '@/stores/inbox';
import { usePlantStore } from '@/stores/plant';
import { useUiStore } from '@/stores/ui';
import { splitIdentifiers } from '@/utils/format';
import ConversationView from './ConversationView.vue';
import CounterBoxes from './CounterBoxes.vue';
import DeskPanel from './DeskPanel.vue';
import FavoritesBand from './FavoritesBand.vue';
import PlantColumn from './PlantColumn.vue';
import ReportsView from './ReportsView.vue';
import ToastHost from './ToastHost.vue';
import TracePanel from './TracePanel.vue';

vi.mock('@/api/client', () => ({ api: apiMock }));

beforeEach(() => {
  setActivePinia(createPinia());
  resetApiMock();
  useIdentityStore().me = ME;
});

afterEach(() => {
  vi.restoreAllMocks();
  vi.useRealTimers();
});

/** jsdom kennt kein Scrollen; die Aufrufe werden aufgezeichnet. */
function recordScrollIntoView(): ReturnType<typeof vi.fn> {
  const spy = vi.fn();
  Element.prototype.scrollIntoView = spy as unknown as Element['scrollIntoView'];
  return spy;
}

describe('ConversationView (Entwurf der Antwort)', () => {
  beforeEach(() => {
    recordScrollIntoView();
  });

  it('zeigt Überschrift und Was tun? sofort und den Rest als Ladezustand, bis die Karte kommt', async () => {
    const conversation = useConversationStore();
    conversation.pending = {
      question: 'Neue Frage',
      toolCalls: [],
      card: null,
      draft: { headline: 'RT-03 steht seit 05:12.', todo: ['Instandhaltung rufen.'] },
      error: null,
    };
    const wrapper = mount(ConversationView);

    const card = wrapper.find('.card.is-pending');
    expect(card.classes()).toContain('has-draft');
    expect(card.attributes('aria-busy')).toBe('true');
    expect(card.find('.card-headline').text()).toBe('RT-03 steht seit 05:12.');
    expect(card.find('.todo-text').text()).toBe('Instandhaltung rufen.');
    expect(card.find('.draft-rest').attributes('aria-hidden')).toBe('true');
    expect(card.text()).toContain('Claude schreibt Werte und Begründung');

    conversation.pending = null;
    await flushPromises();
    expect(wrapper.find('.has-draft').exists()).toBe(false);
  });

  it('zeigt bei einem Fehler nicht den Entwurf, sondern die Fehlermeldung', () => {
    const conversation = useConversationStore();
    conversation.pending = {
      question: 'Neue Frage',
      toolCalls: [],
      card: null,
      draft: { headline: 'Halbe Antwort', todo: [] },
      error: 'Abbruch',
    };
    const wrapper = mount(ConversationView);

    expect(wrapper.find('.has-draft').exists()).toBe(false);
    expect(wrapper.find('.card-headline').exists()).toBe(false);
    expect(wrapper.find('.error-text').text()).toContain('Abbruch');
  });
});

describe('ConversationView (Scroll-Ziel)', () => {
  it('richtet beim Laden den Anfang der letzten Frage am oberen Rand aus, ohne zu gleiten', async () => {
    const spy = recordScrollIntoView();
    const conversation = useConversationStore();
    const wrapper = mount(ConversationView, { attachTo: document.body });
    spy.mockClear();

    conversation.hydrate(sessionPackage(), []);
    await flushPromises();

    const turns = wrapper.findAll('.turn');
    expect(turns).toHaveLength(1);
    expect(spy).toHaveBeenCalledTimes(1);
    expect(spy.mock.instances[0]).toBe(turns[0]!.element);
    expect(spy).toHaveBeenCalledWith({ block: 'start', behavior: 'auto' });
    wrapper.unmount();
  });

  it('gleitet zur neuen Frage derselben Sitzung und zeigt nach dem Neuladen alle Fragen', async () => {
    const spy = recordScrollIntoView();
    const conversation = useConversationStore();
    const base = sessionPackage();
    const second = { ...base.turns[0]!, sequence: 2, question: 'Zweite Frage' };
    conversation.hydrate({ ...base, turns: [base.turns[0]!, second] }, []);
    const wrapper = mount(ConversationView, { attachTo: document.body });
    await flushPromises();
    expect(wrapper.findAll('.turn')).toHaveLength(2); // alle Fragen der Sitzung, nicht nur die letzte

    spy.mockClear();
    conversation.pending = { question: 'Dritte Frage', toolCalls: [], card: null, error: null };
    await flushPromises();

    expect(wrapper.findAll('.turn')).toHaveLength(3);
    expect(spy).toHaveBeenLastCalledWith({ block: 'start', behavior: 'smooth' });
    expect(spy.mock.instances.at(-1)).toBe(wrapper.findAll('.turn')[2]!.element);
    wrapper.unmount();
  });

  it('springt bei „weniger Bewegung“ statt zu gleiten', async () => {
    const spy = recordScrollIntoView();
    vi.stubGlobal(
      'matchMedia',
      vi.fn().mockImplementation((query: string) => ({ matches: query.includes('reduce') })),
    );
    const conversation = useConversationStore();
    conversation.hydrate(sessionPackage(), []);
    const wrapper = mount(ConversationView, { attachTo: document.body });
    await flushPromises();
    spy.mockClear();

    conversation.pending = { question: 'Neue Frage', toolCalls: [], card: null, error: null };
    await flushPromises();

    expect(spy).toHaveBeenLastCalledWith({ block: 'start', behavior: 'auto' });
    wrapper.unmount();
    vi.unstubAllGlobals();
  });
});

describe('DeskPanel (Postfach, Meldungen, Benutzer)', () => {
  function mountPanel() {
    useInboxStore().items = [];
    const trigger = document.createElement('button');
    trigger.dataset.focusKey = 'sb-inbox';
    document.body.append(trigger);
    const wrapper = mount(DeskPanel, { attachTo: document.body });
    return { wrapper, trigger };
  }

  it('öffnet mit Fokus im Fenster und gibt den Fokus beim Schließen an den Auslöser zurück', async () => {
    const ui = useUiStore();
    const { wrapper, trigger } = mountPanel();

    ui.togglePanel('inbox');
    await flushPromises();
    expect(wrapper.classes()).toContain('is-open');
    expect(wrapper.attributes('inert')).toBeUndefined();
    expect(document.activeElement?.className).toBe('panel-title');

    ui.closePanel(true);
    await flushPromises();
    expect(wrapper.classes()).not.toContain('is-open');
    expect(wrapper.classes()).toContain('is-instant'); // Esc: ohne Bewegung
    expect(document.activeElement).toBe(trigger);
    wrapper.unmount();
    trigger.remove();
  });

  it('schließt bei Klick außerhalb, nicht aber bei Klick im Fenster oder auf einen Knopf der Statusleiste', async () => {
    const ui = useUiStore();
    const { wrapper, trigger } = mountPanel();
    ui.togglePanel('inbox');
    await flushPromises();

    wrapper
      .find('.desk-panel-body')
      .element.dispatchEvent(new Event('pointerdown', { bubbles: true }));
    trigger.dispatchEvent(new Event('pointerdown', { bubbles: true }));
    expect(ui.panel).toBe('inbox');

    document.body.dispatchEvent(new Event('pointerdown', { bubbles: true }));
    expect(ui.panel).toBeNull();
    expect(ui.panelClosedByKeyboard).toBe(false);
    wrapper.unmount();
    trigger.remove();
  });

  it('behält den Fokus, wenn er beim Schließen schon auf einem anderen Element liegt', async () => {
    const ui = useUiStore();
    const { wrapper, trigger } = mountPanel();
    const input = document.createElement('input');
    document.body.append(input);
    ui.togglePanel('inbox');
    await flushPromises();

    input.focus();
    ui.closePanel();
    await flushPromises();

    expect(document.activeElement).toBe(input);
    wrapper.unmount();
    trigger.remove();
    input.remove();
  });

  it('zeigt den Inhalt beim Ausblenden noch so lange, bis die Bewegung zu Ende ist', async () => {
    vi.useFakeTimers();
    const ui = useUiStore();
    const { wrapper, trigger } = mountPanel();
    ui.togglePanel('user');
    await flushPromises();
    expect(wrapper.find('.desk-panel-body').exists()).toBe(true);

    ui.closePanel();
    await flushPromises();
    expect(wrapper.find('.desk-panel-body').exists()).toBe(true);
    await vi.advanceTimersByTimeAsync(150);
    expect(wrapper.find('.desk-panel-body').exists()).toBe(false);
    wrapper.unmount();
    trigger.remove();
  });
});

describe('FavoritesBand (einzeilig mit Überlauf)', () => {
  function sizeList(element: HTMLElement, scrollWidth: number, clientWidth: number, left = 0) {
    Object.defineProperty(element, 'scrollWidth', { configurable: true, value: scrollWidth });
    Object.defineProperty(element, 'clientWidth', { configurable: true, value: clientWidth });
    element.scrollLeft = left;
  }

  function mountBand() {
    useFavoritesStore().favorites = structuredClone(FAVORITES);
    return mount(FavoritesBand, { attachTo: document.body });
  }

  it('markiert den Überlauf und lässt Zähler und Stift außerhalb der scrollenden Liste', async () => {
    const wrapper = mountBand();
    const list = wrapper.find('.fav-list');
    expect(list.classes()).not.toContain('has-more-after');
    expect(list.element.contains(wrapper.find('.fav-count').element)).toBe(false);
    expect(list.element.contains(wrapper.find('.fav-edit-btn').element)).toBe(false);

    sizeList(list.element as HTMLElement, 900, 600);
    await list.trigger('scroll');
    expect(list.classes()).toContain('has-more-after');
    expect(list.classes()).not.toContain('has-more-before');

    sizeList(list.element as HTMLElement, 900, 600, 300);
    await list.trigger('scroll');
    expect(list.classes()).toContain('has-more-before');
    expect(list.classes()).not.toContain('has-more-after');
    wrapper.unmount();
  });

  it('dreht das Mausrad auf dem Band in eine Seitwärtsbewegung, nur wenn etwas überläuft', async () => {
    const wrapper = mountBand();
    const list = wrapper.find('.fav-list').element as HTMLElement;

    sizeList(list, 600, 600);
    const fits = new WheelEvent('wheel', { deltaY: 80, cancelable: true });
    list.dispatchEvent(fits);
    expect(fits.defaultPrevented).toBe(false);

    sizeList(list, 900, 600);
    const overflows = new WheelEvent('wheel', { deltaY: 80, cancelable: true });
    list.dispatchEvent(overflows);
    expect(overflows.defaultPrevented).toBe(true);
    expect(list.scrollLeft).toBe(80);
    wrapper.unmount();
  });

  it('misst den Überlauf neu, wenn der Bearbeitungsmodus die Chips verbreitert', async () => {
    const wrapper = mountBand();
    const list = wrapper.find('.fav-list');
    expect(list.classes()).not.toContain('has-more-after');

    sizeList(list.element as HTMLElement, 900, 600); // mit Entfernen-Knöpfen passt das Band nicht mehr
    await wrapper.find('.fav-edit-btn').trigger('click');
    await flushPromises();

    expect(list.classes()).toContain('has-more-after');
    wrapper.unmount();
  });

  it('hält den Editor im Baum, aber inert, solange er zugeklappt ist', async () => {
    const wrapper = mountBand();
    expect(wrapper.find('.fav-editor-collapse').classes()).not.toContain('is-open');

    await wrapper.find('.fav-edit-btn').trigger('click');
    expect(wrapper.find('.fav-editor-collapse').classes()).toContain('is-open');
    wrapper.unmount();
  });
});

describe('Zustand „Keine Daten“', () => {
  const OVERVIEW: OverviewResponse = {
    scope: 'plant',
    id: 'plant',
    title: 'Werk',
    liveText: 'live 10:00',
    climate: null,
    counts: { problem: 0, warning: 1, running: 0, noData: 2 },
    groups: [
      {
        id: 'hallen',
        title: 'Hallen',
        kpiLabel: 'OEE KW 42',
        stationCount: 3,
        worstStatus: 'warning',
        counts: { problem: 0, warning: 1, running: 0, noData: 2 },
        stations: [
          {
            id: 'Halle A',
            status: 'warning',
            statusWord: 'Warnung',
            article: '',
            kpi: { value: '77 %', unit: 'OEE KW 42' },
            message: 'RT-03 Rückstand',
          },
          {
            id: 'Halle B',
            status: 'nodata',
            statusWord: 'Keine Daten',
            article: '',
            kpi: { value: '–', unit: 'OEE KW 42' },
            message: 'Für diese Woche liegen keine Kennzahlen vor',
          },
          {
            id: 'Halle C',
            status: 'nodata',
            statusWord: 'Keine Daten',
            article: '',
            kpi: { value: '–', unit: 'OEE KW 42' },
            message: 'Für diese Woche liegen keine Kennzahlen vor',
          },
        ],
      },
    ],
  };

  it('zeigt Hallen ohne Daten neutral, ohne Prozentwert und zählt sie nicht unter „Läuft“', () => {
    usePlantStore().overview = OVERVIEW;
    const wrapper = mount(PlantColumn);

    const stations = wrapper.findAll('.station');
    expect(stations[1]!.find('.pill').classes()).toContain('pill-nodata');
    expect(stations[1]!.find('.pill').text()).toBe('Keine Daten');
    expect(stations[1]!.find('.pill').classes()).not.toContain('pill-running');
    expect(stations[1]!.find('.station-kpi').exists()).toBe(false);
    expect(stations[0]!.find('.station-kpi').text()).toBe('77 %');

    const counters = wrapper.findAll('.counter').map((c) => c.text());
    expect(counters).toEqual(['0Problem', '1Warnung', '0Läuft', '2Keine Daten']);
    expect(wrapper.find('.group-worst').text()).toContain('Warnung');
  });

  it('beschriftet den Prozentwert und sein Verhältnis zur Ampel', () => {
    usePlantStore().overview = OVERVIEW;
    const wrapper = mount(PlantColumn);

    expect(wrapper.find('.plant-legend').text()).toBe('% = OEE KW 42 · Ampel: alle Regeln');
    expect(wrapper.find('.plant-legend').attributes('title')).toContain(
      'nicht allein dem Prozentwert',
    );
  });

  it('blendet „Keine Daten“ im Zähler aus, wenn es keine solchen Stationen gibt', () => {
    const wrapper = mount(CounterBoxes, {
      props: { counts: { problem: 1, warning: 5, running: 2 }, variant: 'counter' },
    });
    expect(wrapper.findAll('.counter')).toHaveLength(3);
  });

  it('klappt eine Gruppe über eine Klasse ein und macht die Liste inert', async () => {
    usePlantStore().overview = OVERVIEW;
    const wrapper = mount(PlantColumn);
    const group = () => wrapper.find('.station-collapse');
    expect(group().classes()).toContain('is-open');

    await wrapper.find('.group-toggle').trigger('click');
    expect(group().classes()).not.toContain('is-open');
    expect(wrapper.find('.group-toggle').attributes('aria-expanded')).toBe('false');
  });
});

describe('Kennungen ohne Umbruch', () => {
  it('zerlegt Text in unteilbare Kennungen und Zeiträume', () => {
    expect(splitIdentifiers('Seit 06:00-07:05 · RT-03 und F-125-07 offen')).toEqual([
      { text: 'Seit ', keep: false },
      { text: '06:00-07:05', keep: true },
      { text: ' · ', keep: false },
      { text: 'RT-03', keep: true },
      { text: ' und ', keep: false },
      { text: 'F-125-07', keep: true },
      { text: ' offen', keep: false },
    ]);
    expect(splitIdentifiers('Ohne Kennung')).toEqual([{ text: 'Ohne Kennung', keep: false }]);
    expect(splitIdentifiers('')).toEqual([]);
  });
});

describe('ToastHost', () => {
  it('zeigt die Meldung in einer dauerhaften Ansage-Region und blendet sie wieder aus', async () => {
    const ui = useUiStore();
    const wrapper = mount(ToastHost);
    expect(wrapper.find('#toast').attributes('role')).toBe('status');
    expect(wrapper.find('.toast').exists()).toBe(false);

    ui.toast('Gespeichert.');
    await flushPromises();
    expect(wrapper.find('.toast').text()).toBe('Gespeichert.');
  });
});

describe('TracePanel (ältere Fragen eingeklappt)', () => {
  function withTwoTurns() {
    const conversation = useConversationStore();
    const base = sessionPackage();
    const first = {
      ...base.turns[0]!,
      sequence: 1,
      question: 'Erste Frage',
      toolCalls: TOOL_CALLS,
    };
    const second = {
      ...base.turns[0]!,
      sequence: 2,
      question: 'Zweite Frage',
      toolCalls: TOOL_CALLS,
    };
    conversation.hydrate({ ...base, turns: [first, second] }, []);
    return mount(TracePanel, { attachTo: document.body });
  }

  it('öffnet nur die aktuelle Frage und klappt ältere auf eine Zeile ein', () => {
    const wrapper = withTwoTurns();
    const blocks = wrapper.findAll('.trace-block');

    expect(blocks).toHaveLength(2);
    expect(blocks[0]!.classes()).toContain('is-newest'); // neueste zuerst
    expect(blocks[0]!.find('.trace-toggle').attributes('aria-expanded')).toBe('true');
    expect(blocks[1]!.find('.trace-toggle').attributes('aria-expanded')).toBe('false');
    expect(blocks[1]!.find('.trace-sum').text()).toBe('2 MCP-Aufrufe · 44 ms');
    expect(blocks[1]!.find('.trace-collapse').classes()).not.toContain('is-open');
    wrapper.unmount();
  });

  it('klappt einen älteren Block per Knopf auf und den aktuellen zu', async () => {
    const wrapper = withTwoTurns();
    const blocks = () => wrapper.findAll('.trace-block');

    await blocks()[1]!.find('.trace-toggle').trigger('click');
    expect(blocks()[1]!.find('.trace-toggle').attributes('aria-expanded')).toBe('true');
    await blocks()[0]!.find('.trace-toggle').trigger('click');
    expect(blocks()[0]!.find('.trace-toggle').attributes('aria-expanded')).toBe('false');
    wrapper.unmount();
  });

  it('klappt bei einer neuen Frage die bisher aktuelle ein', async () => {
    const wrapper = withTwoTurns();
    const conversation = useConversationStore();
    conversation.pending = { question: 'Dritte Frage', toolCalls: [], card: null, error: null };
    await flushPromises();

    const blocks = wrapper.findAll('.trace-block');
    expect(blocks).toHaveLength(3);
    expect(blocks[0]!.find('.trace-toggle').attributes('aria-expanded')).toBe('true');
    expect(blocks[1]!.find('.trace-toggle').attributes('aria-expanded')).toBe('false');
    wrapper.unmount();
  });

  it('hält den Erklärtext fest am Fuß der Spalte, außerhalb der scrollenden Liste', () => {
    const wrapper = withTwoTurns();
    expect(
      wrapper.find('.trace-scroll').element.contains(wrapper.find('.trace-foot').element),
    ).toBe(false);
    wrapper.unmount();
  });
});

describe('ReportsView (Rückweg und eine Notiz statt Doppelungen)', () => {
  it('bietet einen Rückweg zum Leitstand', async () => {
    apiMock.reportsSummary.mockResolvedValue(REPORTS_SUMMARY);
    const ui = useUiStore();
    ui.deskReports = true;
    ui.reportsTab = 'usage';
    const wrapper = mount(ReportsView);
    await flushPromises();

    await wrapper.find('.reports-back').trigger('click');
    expect(ui.deskReports).toBe(false);
  });

  it('zeigt Ø, Median und p95 nur einmal und die Tabelle zum Werkzeug-Diagramm zugeklappt', async () => {
    apiMock.reportsSummary.mockResolvedValue(REPORTS_SUMMARY);
    useUiStore().deskReports = true;
    useUiStore().reportsTab = 'usage';
    const wrapper = mount(ReportsView);
    await flushPromises();

    expect(wrapper.findAll('.rp-fact dt').map((dt) => dt.text())).not.toContain('Median (p50)');
    expect(wrapper.text().match(/Ø Antwortzeit/g)).toHaveLength(1);
    const toolsDetails = wrapper.find('.rp-tools details.rp-table-toggle');
    expect(toolsDetails.exists()).toBe(true);
    expect(toolsDetails.attributes('open')).toBeUndefined();
  });

  it('nennt leere Token-Werte einmal oberhalb der Kacheln', async () => {
    apiMock.reportsSummary.mockResolvedValue({
      ...REPORTS_SUMMARY,
      tokens: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 },
    });
    useUiStore().deskReports = true;
    useUiStore().reportsTab = 'usage';
    const wrapper = mount(ReportsView);
    await flushPromises();

    expect(wrapper.findAll('.reports-note')).toHaveLength(1);
    expect(wrapper.text().match(/Noch keine Tokens verbraucht/g)).toHaveLength(1);
  });
});
