/// <reference types="node" />
// Maschinen-Report: Raster, Stationsdetail, Zeitraumwechsel, „Mit Claude analysieren“, Vorschau-Hinweis, Handy-Ansicht, Reiter.
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiMock, resetApiMock } from '@/test/apiMock';
import { MACHINE_REPORT } from '@/test/machineFixtures';
import { REPORTS_SUMMARY } from '@/test/fixtures';
import { loadMachinesSample } from '@/preview/snapshot';
import { useBackendStore } from '@/stores/backend';
import { useConversationStore } from '@/stores/conversation';
import { useIdentityStore } from '@/stores/identity';
import { useMachinesStore } from '@/stores/machines';
import { useUiStore } from '@/stores/ui';
import { deltaOf, defaultRangeOf, lossSegments, minutesText } from '@/utils/machineFormat';
import MachinesView from './MachinesView.vue';
import ReportsView from './ReportsView.vue';

vi.mock('@/api/client', () => ({ api: apiMock }));
vi.mock('@/preview/snapshot', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/preview/snapshot')>()),
  loadMachinesSample: vi.fn(),
  loadReportsSample: vi.fn(),
}));

beforeEach(() => {
  setActivePinia(createPinia());
  resetApiMock();
  vi.mocked(loadMachinesSample).mockReset();
  useIdentityStore().role = 'hall_lead';
  useUiStore().deskReports = true;
  apiMock.reportsMachines.mockResolvedValue(MACHINE_REPORT);
});

afterEach(() => {
  vi.restoreAllMocks();
});

async function show() {
  const wrapper = mount(MachinesView, { attachTo: document.body });
  await flushPromises();
  return wrapper;
}

describe('Hilfen des Maschinen-Reports', () => {
  it('rechnet Änderungen in Prozentpunkten und kennzeichnet besser oder schlechter', () => {
    expect(deltaOf(77, 75, true)).toEqual({ direction: 'up', text: '+2 Pkt', better: true });
    expect(deltaOf(1.2, 0.8, false, 1)).toEqual({
      direction: 'up',
      text: '+0,4 Pkt',
      better: false,
    });
    expect(deltaOf(70, 75, true)?.text).toBe('−5 Pkt');
    expect(deltaOf(75, 75, true)).toEqual({ direction: 'flat', text: '±0 Pkt', better: null });
    expect(deltaOf(null, 75, true)).toBeNull();
    expect(deltaOf(75, undefined, true)).toBeNull();
  });

  it('zerlegt die OEE in OEE und drei Verluste, die zusammen 100 % ergeben', () => {
    const segments = lossSegments({
      oeePercent: 75,
      availabilityPercent: 95,
      performancePercent: 80,
      qualityPercent: 99,
      rating: 'warn',
    });
    expect(segments.map((s) => s.key)).toEqual(['oee', 'quality', 'performance', 'availability']);
    expect(segments.reduce((sum, s) => sum + s.percent, 0)).toBeCloseTo(100, 6);
    expect(segments[0]!.percent).toBeCloseTo(75.24, 2);
  });

  it('nennt Dauer und Startzeitraum je Rolle', () => {
    expect(minutesText(47)).toBe('47 min');
    expect(minutesText(291)).toBe('4 h 51 min');
    expect(defaultRangeOf('shift_lead')).toBe('shift');
    expect(defaultRangeOf('hall_lead')).toBe('week');
    expect(defaultRangeOf('production_lead')).toBe('fourWeeks');
  });
});

describe('Raster der Stationen', () => {
  it('zeigt alle acht Stationen mit OEE, Bewertung in Worten, Ist/Soll, Ausschuss, Stillstand und Pfeil', async () => {
    const wrapper = await show();

    const tiles = wrapper.findAll('.mc-tile');
    expect(tiles).toHaveLength(8);
    const rt03 = tiles[2]!;
    expect(rt03.text()).toContain('RT-03');
    expect(rt03.text()).toContain('62 %');
    expect(rt03.text()).toContain('kritisch');
    expect(rt03.text()).toContain('55.830 / 60.699');
    expect(rt03.text()).toContain('1,4 %');
    expect(rt03.text()).toContain('4 h 47 min');
    expect(rt03.find('.mc-delta').text()).toContain('−10 Pkt');
    expect(rt03.find('.mc-delta').classes()).toContain('is-worse');
    expect(rt03.find('.mc-delta .visually-hidden').text()).toBe('gegenüber KW 41');
    expect(rt03.attributes('aria-label')).toContain('OEE 62 Prozent, kritisch');
    // Öfen zeigen die Belegung statt der OEE.
    expect(tiles[4]!.text()).toContain('Belegung');
    expect(tiles[4]!.text()).toContain('76 %');
    wrapper.unmount();
  });

  it('lädt für die Rolle den Startzeitraum mit Vergleich und zeigt Kosten nur auf Wunsch', async () => {
    const wrapper = await show();

    expect(apiMock.reportsMachines).toHaveBeenCalledWith('hall_lead', 'week', true);
    expect(wrapper.find('.mc-kpis').text()).toContain('77 %');
    expect(wrapper.find('.mc-kpis').text()).not.toContain('Kosten der Verluste');
    expect(wrapper.find('.mc-costs').exists()).toBe(false);

    await wrapper
      .find('.mc-options input[type="checkbox"]:not([disabled]) ~ span')
      .trigger('click');
    const costBox = wrapper.findAll('.mc-check input')[1]!;
    await costBox.setValue(true);
    expect(wrapper.find('.mc-costs').text()).toContain('19.197 €');
    expect(wrapper.find('.mc-kpis').text()).toContain('Kosten der Verluste');
    wrapper.unmount();
  });

  it('startet der Produktionsleiter mit vier Wochen und Kosten', async () => {
    useIdentityStore().role = 'production_lead';
    const wrapper = await show();

    expect(apiMock.reportsMachines).toHaveBeenCalledWith('production_lead', 'fourWeeks', true);
    expect(useMachinesStore().showCosts).toBe(true);
    expect(wrapper.find('.mc-costs').exists()).toBe(true);
    wrapper.unmount();
  });

  it('startet der Schichtleiter mit der laufenden Schicht', async () => {
    useIdentityStore().role = 'shift_lead';
    const wrapper = await show();

    expect(apiMock.reportsMachines).toHaveBeenCalledWith('shift_lead', 'shift', true);
    wrapper.unmount();
  });
});

describe('Stationsdetail', () => {
  it('öffnet bei Klick den Stationsbereich mit allen Diagrammen und Tabellen-Alternativen', async () => {
    const wrapper = await show();
    await wrapper.findAll('.mc-tile')[2]!.trigger('click');

    const detail = wrapper.find('.mc-detail');
    expect(detail.exists()).toBe(true);
    expect(wrapper.findAll('.mc-tile')[2]!.attributes('aria-current')).toBe('true');
    expect(detail.find('h3').text()).toContain('RT-03');
    // OEE zerlegt, gestapelt, mit Werten und Tabelle
    expect(detail.find('.mc-stack').attributes('aria-label')).toContain('OEE 62 Prozent');
    expect(detail.findAll('.mc-seg')).toHaveLength(4);
    expect(detail.text()).toContain('Verfügbarkeit');
    // Pareto in Minuten (Euro erst mit Kostenanzeige)
    expect(detail.text()).toContain('Störung Gewebezuführung');
    expect(detail.text()).toContain('4 h 51 min');
    expect(detail.text()).not.toContain('9.807 €');
    // Ausschuss nach Grund
    expect(detail.text()).toContain('Gewebe');
    // Trend mit Toleranzband und Anteil außerhalb
    const trend = detail.find('.mc-trend');
    expect(trend.text()).toContain('19,6 %');
    expect(trend.text()).toContain('Toleranzband -5,0 bis 5,0');
    expect(trend.findAll('.mc-flag')).toHaveLength(2);
    expect(trend.find('.rp-plot').attributes('aria-label')).toContain('Toleranzband');
    // Zuverlässigkeit und Wartung
    expect(detail.text()).toContain('7,8 h');
    expect(detail.text()).toContain('32 min');
    expect(detail.text()).toContain('94 % der Standzeit');
    expect(detail.text()).toContain('Hydrauliköl wechseln');
    // Jede Grafik hat eine Tabelle als Alternative.
    expect(detail.findAll('details.rp-table-toggle').length).toBeGreaterThanOrEqual(3);
    wrapper.unmount();
  });

  it('zeigt Pareto-Kosten in Euro, wenn Kosten gewählt sind', async () => {
    const wrapper = await show();
    useMachinesStore().showCosts = true;
    await wrapper.findAll('.mc-tile')[2]!.trigger('click');

    expect(wrapper.find('.mc-detail').text()).toContain('9.807 €');
    wrapper.unmount();
  });

  it('zeigt bei Öfen die Belegung und bei Pressen keinen Ausschuss ohne Daten', async () => {
    const wrapper = await show();
    await wrapper.findAll('.mc-tile')[4]!.trigger('click');

    const detail = wrapper.find('.mc-detail');
    expect(detail.text()).toContain('Belegung');
    expect(detail.text()).toContain('76 %');
    expect(detail.text()).not.toContain('Ausschuss nach Grund');
    wrapper.unmount();
  });
});

describe('Zeitraumwechsel', () => {
  it('lädt den gewählten Zeitraum neu und merkt ihn sich', async () => {
    const wrapper = await show();
    apiMock.reportsMachines.mockResolvedValue({ ...MACHINE_REPORT, range: 'yesterday' });

    const chip = wrapper.findAll('.mc-chip').find((c) => c.text() === 'Gestern')!;
    await chip.trigger('click');
    await flushPromises();

    expect(apiMock.reportsMachines).toHaveBeenLastCalledWith('hall_lead', 'yesterday', true);
    expect(useMachinesStore().range).toBe('yesterday');
    expect(
      wrapper
        .findAll('.mc-chip')
        .find((c) => c.text() === 'Gestern')!
        .attributes('aria-pressed'),
    ).toBe('true');
    wrapper.unmount();
  });

  it('schaltet den Vergleich mit der Vorperiode ab', async () => {
    const wrapper = await show();
    apiMock.reportsMachines.mockResolvedValue({
      ...MACHINE_REPORT,
      compare: false,
      previousLabel: null,
    });

    await wrapper.findAll('.mc-check input')[0]!.setValue(false);
    await flushPromises();

    expect(apiMock.reportsMachines).toHaveBeenLastCalledWith('hall_lead', 'week', false);
    expect(wrapper.find('.mc-tile .mc-delta').exists()).toBe(false);
    wrapper.unmount();
  });
});

describe('Mit Claude analysieren', () => {
  it('stellt die passende Frage in der Fragen-Ansicht', async () => {
    useBackendStore().phase = 'ready';
    const wrapper = await show();
    const ask = vi.spyOn(useConversationStore(), 'askFrom').mockResolvedValue();

    const buttons = wrapper.findAll('.mc-analyze');
    expect(buttons).toHaveLength(2);
    expect(buttons[0]!.attributes('aria-label')).toContain('RT-03: OEE 62 %');
    await buttons[0]!.trigger('click');

    expect(ask).toHaveBeenCalledWith('Warum liegt die OEE von RT-03 diese Woche bei 62 %?', {
      report: { kind: 'machines', range: 'week', findingId: 'oee-RT-03' },
    });
    wrapper.unmount();
  });

  it('legt die Frage in der Vorschau in die Warteschlange und wechselt zu den Fragen', async () => {
    vi.mocked(loadMachinesSample).mockResolvedValue(MACHINE_REPORT);
    useBackendStore().phase = 'asleep';
    const wrapper = await show();

    await wrapper.findAll('.mc-analyze')[1]!.trigger('click');

    const conversation = useConversationStore();
    expect(conversation.queuedQuestion).toBe(
      'Warum ist die OEE in der Nachtschicht diese Woche niedriger als in der Frühschicht?',
    );
    expect(conversation.pending?.queued).toBe(true);
    expect(useUiStore().deskReports).toBe(false);
    wrapper.unmount();
  });

  it('zeigt die Auffälligkeiten einer Station im Stationsbereich', async () => {
    const wrapper = await show();
    await wrapper.findAll('.mc-tile')[2]!.trigger('click');

    expect(wrapper.find('.mc-detail .mc-findings').text()).toContain('RT-03: OEE 62 %');
    wrapper.unmount();
  });
});

describe('Vorschau', () => {
  it('weist auf verfügbare Zeiträume hin, sobald die Halle bereit ist, und sperrt die anderen', async () => {
    vi.mocked(loadMachinesSample).mockResolvedValue(MACHINE_REPORT);
    useBackendStore().phase = 'asleep';
    const wrapper = await show();

    expect(apiMock.reportsMachines).not.toHaveBeenCalled();
    expect(loadMachinesSample).toHaveBeenCalledWith('hall_lead');
    expect(wrapper.find('.reports-stale').text()).toContain(
      'verfügbar, sobald die Halle bereit ist',
    );
    expect(wrapper.find('.reports-stale').text()).toContain('09:42');
    const chips = wrapper.findAll('.mc-chip');
    expect(chips.map((c) => c.attributes('disabled') !== undefined)).toEqual([
      true,
      true,
      false,
      true,
    ]);
    expect(chips[2]!.attributes('aria-pressed')).toBe('true');
    expect(wrapper.findAll('.mc-check input')[0]!.attributes('disabled')).toBeDefined();

    await chips[0]!.trigger('click');
    expect(useMachinesStore().range).toBe('week');
    wrapper.unmount();
  });
});

describe('Handy', () => {
  it('wechselt von der Liste zur Detailseite und zurück', async () => {
    const wrapper = await show();
    const root = wrapper.find('.mc');
    expect(root.classes()).not.toContain('has-detail');

    await wrapper.findAll('.mc-tile')[0]!.trigger('click');
    expect(root.classes()).toContain('has-detail');
    const back = wrapper.find('.mc-back');
    expect(back.text()).toContain('Alle Stationen');

    await back.trigger('click');
    expect(root.classes()).not.toContain('has-detail');
    expect(useMachinesStore().selectedId).toBeNull();
    wrapper.unmount();
  });

  it('gibt allen Bedienelementen am Handy mindestens 44 px', () => {
    const css = readFileSync(resolve(process.cwd(), 'src/styles/machines.css'), 'utf8');
    const mobile = css.slice(css.indexOf('@media (max-width: 900px)'));
    for (const selector of ['.mc-chip,', '.mc-analyze {', '.mc-tile {']) {
      const block = mobile.slice(mobile.indexOf(selector));
      expect(block.slice(0, block.indexOf('}'))).toMatch(/min-height: (var\(--touch\)|\d+px)/);
    }
    expect(css).toMatch(/\.mc-back \{[^}]*min-height: var\(--touch\)/);
    expect(css).toMatch(/\.reports-tab \{[^}]*min-height: var\(--touch\)/);
  });
});

describe('Report-Seite mit Reitern', () => {
  it('zeigt zuerst die Maschinen und wechselt per Reiter und Pfeiltaste zur KI-Nutzung', async () => {
    apiMock.reportsSummary.mockResolvedValue(REPORTS_SUMMARY);
    const wrapper = mount(ReportsView, { attachTo: document.body });
    await flushPromises();

    const tabs = wrapper.findAll('[role="tab"]');
    expect(tabs.map((t) => t.text())).toEqual(['Maschinen', 'KI-Nutzung']);
    expect(tabs[0]!.attributes('aria-selected')).toBe('true');
    expect(wrapper.find('.mc').exists()).toBe(true);
    expect(apiMock.reportsSummary).not.toHaveBeenCalled();

    await tabs[0]!.trigger('keydown', { key: 'ArrowRight' });
    await flushPromises();
    expect(useUiStore().reportsTab).toBe('usage');
    expect(wrapper.find('.mc').exists()).toBe(false);
    expect(apiMock.reportsSummary).toHaveBeenCalled();
    expect(wrapper.find('[role="tabpanel"]').attributes('aria-labelledby')).toBe(
      'reports-tab-usage',
    );
    wrapper.unmount();
  });
});
