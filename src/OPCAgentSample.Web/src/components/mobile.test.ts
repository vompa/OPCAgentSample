// Handy (Claude-Stil): Senden/Mikrofon-Tausch, „Zuletzt gefragt“, Seitenleiste, Rollenmenü, Favoritenleiste,
// Leerzustand mit Vorschlägen, Mitlaufen des Verlaufs, Scrollposition je Reiter.
import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { defineComponent, nextTick } from 'vue';
import { apiMock, resetApiMock } from '@/test/apiMock';
import { FAVORITES, ME, sessionPackage } from '@/test/fixtures';
import { useScrollMemory } from '@/composables/useScrollMemory';
import { configurePlatform, platform } from '@/platform';
import { useAppStore } from '@/stores/app';
import { useAuthStore } from '@/stores/auth';
import { useConversationStore } from '@/stores/conversation';
import { useFavoritesStore } from '@/stores/favorites';
import { useIdentityStore } from '@/stores/identity';
import { useUiStore } from '@/stores/ui';
import ComposerBar from './ComposerBar.vue';
import ConversationView from './ConversationView.vue';
import FavoritesBand from './FavoritesBand.vue';
import MobileBar from './MobileBar.vue';
import MobileSidebar from './MobileSidebar.vue';
import RoleMenu from './RoleMenu.vue';
import SessionsDialog from './SessionsDialog.vue';

vi.mock('@/api/client', () => ({ api: apiMock }));

const ROLES = [
  { id: 'shift_lead', label: 'Schichtleiter', shortLabel: 'Schicht' },
  { id: 'hall_lead', label: 'Hallenleiter', shortLabel: 'Halle' },
  { id: 'production_lead', label: 'Produktionsleiter', shortLabel: 'Produktion' },
];

const originalSpeech = platform.speech;

beforeEach(() => {
  setActivePinia(createPinia());
  resetApiMock();
  useIdentityStore().me = { ...ME, roles: ROLES };
});

afterEach(() => {
  configurePlatform({ speech: originalSpeech });
  document.body.innerHTML = '';
  vi.restoreAllMocks();
});

/** jsdom kennt `showModal` und `close` nicht; die Stubs setzen und löschen das Attribut `open` wie ein Browser. */
function stubDialog(): { showModal: ReturnType<typeof vi.fn>; close: ReturnType<typeof vi.fn> } {
  const showModal = vi.fn(function (this: HTMLDialogElement) {
    this.setAttribute('open', '');
  });
  const close = vi.fn(function (this: HTMLDialogElement) {
    this.removeAttribute('open');
  });
  Object.assign(HTMLDialogElement.prototype, { showModal, close });
  return { showModal, close };
}

function withSpeech(supported: boolean): void {
  configurePlatform({ speech: { supported, start: vi.fn() } });
}

describe('ComposerBar (Senden und Mikrofon tauschen den Platz)', () => {
  it('zeigt bei leerem Feld das Mikrofon und mit Text den Senden-Knopf', async () => {
    withSpeech(true);
    const wrapper = mount(ComposerBar);
    expect(wrapper.get('#mic-btn').classes()).not.toContain('is-swapped');
    expect(wrapper.get('#send-btn').classes()).toContain('is-idle');

    await wrapper.get('#question-input').setValue('Wie ist die Lage?');
    expect(wrapper.get('#mic-btn').classes()).toContain('is-swapped');
    expect(wrapper.get('#send-btn').classes()).not.toContain('is-idle');

    await wrapper.get('#question-input').setValue('   ');
    expect(wrapper.get('#send-btn').classes()).toContain('is-idle'); // Leerzeichen zählen nicht als Eingabe
  });

  it('lässt „Senden“ ohne Spracheingabe immer sichtbar', () => {
    withSpeech(false);
    const wrapper = mount(ComposerBar);
    expect(wrapper.find('#mic-btn').exists()).toBe(false);
    expect(wrapper.get('#send-btn').classes()).not.toContain('is-idle');
  });

  it('sendet die Frage über den Senden-Knopf und leert das Feld', async () => {
    withSpeech(true);
    const conversation = useConversationStore();
    const ask = vi.spyOn(conversation, 'ask').mockResolvedValue();
    const wrapper = mount(ComposerBar);
    await wrapper.get('#question-input').setValue('Wie läuft RT-01?');
    await wrapper.get('#composer').trigger('submit');
    expect(ask).toHaveBeenCalledWith('Wie läuft RT-01?');
    expect((wrapper.get('#question-input').element as HTMLInputElement).value).toBe('');
  });

  it('meldet den Fokus des Eingabefelds (Tastatur offen) an den Zustand', async () => {
    const ui = useUiStore();
    const wrapper = mount(ComposerBar);
    await wrapper.get('#question-input').trigger('focus');
    expect(ui.composerFocused).toBe(true);
    await wrapper.get('#question-input').trigger('blur');
    expect(ui.composerFocused).toBe(false);
  });
});

describe('ComposerBar („Zuletzt gefragt“ einklappbar, ohne die aktuelle Frage)', () => {
  it('zeigt die Liste erst nach dem Aufklappen und lässt die Frage des Verlaufs weg', async () => {
    const conversation = useConversationStore();
    conversation.hydrate(sessionPackage(), ['Schichtübergabe?', 'Wie läuft RT-02?', 'Ofen voll?']);
    const wrapper = mount(ComposerBar);

    const toggle = wrapper.get('.recent-toggle');
    expect(toggle.attributes('aria-expanded')).toBe('false');
    expect(toggle.get('.recent-count').text()).toBe('2'); // „Schichtübergabe?“ steht schon im Verlauf
    expect(wrapper.findAll('.recent-btn')).toHaveLength(0);

    await toggle.trigger('click');
    expect(toggle.attributes('aria-expanded')).toBe('true');
    expect(wrapper.findAll('.recent-btn').map((b) => b.text())).toEqual([
      'Wie läuft RT-02?',
      'Ofen voll?',
    ]);
  });

  it('lässt auch die gerade laufende Frage weg und blendet den Schalter ohne Rest aus', async () => {
    const conversation = useConversationStore();
    conversation.recentQuestions = ['Nur diese'];
    conversation.pending = { question: 'Nur diese', toolCalls: [], card: null, error: null };
    const wrapper = mount(ComposerBar);
    expect(wrapper.find('.recent-toggle').exists()).toBe(false);
  });

  it('stellt eine gewählte Frage und klappt die Liste wieder zu', async () => {
    const conversation = useConversationStore();
    const ask = vi.spyOn(conversation, 'ask').mockResolvedValue();
    conversation.recentQuestions = ['Erste', 'Zweite'];
    const wrapper = mount(ComposerBar);
    await wrapper.get('.recent-toggle').trigger('click');
    await wrapper.findAll('.recent-btn')[1]!.trigger('click');
    expect(ask).toHaveBeenCalledWith('Zweite');
    expect(wrapper.findAll('.recent-btn')).toHaveLength(0);
  });
});

/** Handy-Breite für Komponenten, die `useIsMobile` fragen. */
function mockMobile(matches = true): void {
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: matches && query.includes('max-width'),
    media: query,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  }));
}

describe('Kopfzeile (Seitenleiste, Rollenmenü, Stift)', () => {
  it('öffnet die Seitenleiste über den Menüknopf und meldet den Zustand', async () => {
    const ui = useUiStore();
    const wrapper = mount(MobileBar);
    const button = wrapper.get('#sidebar-btn');
    expect(button.attributes('aria-expanded')).toBe('false');
    await button.trigger('click');
    expect(ui.sidebarOpen).toBe(true);
    expect(button.attributes('aria-expanded')).toBe('true');
  });

  it('legt über den Stift eine neue Sitzung an, aber keine zweite leere', async () => {
    const ui = useUiStore();
    const conversation = useConversationStore();
    const start = vi.spyOn(conversation, 'startNewSession').mockResolvedValue();
    const wrapper = mount(MobileBar);

    await wrapper.get('#new-session-btn').trigger('click');
    expect(start).not.toHaveBeenCalled(); // die Sitzung ist schon leer
    expect(ui.mobileView).toBe('ask');

    conversation.hydrate(sessionPackage(), []);
    ui.setMobileView('start');
    await wrapper.get('#new-session-btn').trigger('click');
    expect(start).toHaveBeenCalledTimes(1);
    expect(ui.mobileView).toBe('ask');
  });
});

describe('Rollenmenü', () => {
  it('zeigt die aktive Rolle im Feld und öffnet ein Menü mit Häkchen an der aktiven', async () => {
    const wrapper = mount(RoleMenu, { attachTo: document.body });
    const trigger = wrapper.get('#role-menu-btn');
    expect(trigger.text()).toContain('Schicht');
    expect(trigger.attributes('aria-haspopup')).toBe('menu');
    expect(trigger.attributes('aria-expanded')).toBe('false');
    expect(wrapper.find('[role="menu"]').exists()).toBe(false);

    await trigger.trigger('click');
    expect(trigger.attributes('aria-expanded')).toBe('true');
    const items = wrapper.findAll('[role="menuitemradio"]');
    expect(items.map((i) => i.get('.role-menu-item-name').text())).toEqual([
      'Schicht',
      'Halle',
      'Produktion',
    ]);
    expect(items.map((i) => i.attributes('aria-checked'))).toEqual(['true', 'false', 'false']);
    expect(items[0]!.find('.role-menu-check').exists()).toBe(true);
    expect(items[1]!.find('.role-menu-check').exists()).toBe(false);
    expect(document.activeElement).toBe(items[0]!.element); // Fokus auf der aktiven Rolle
    wrapper.unmount();
  });

  it('wechselt die Rolle bei der Auswahl, schließt das Menü und gibt den Fokus zurück', async () => {
    const app = useAppStore();
    const loadRole = vi.spyOn(app, 'loadRole').mockResolvedValue();
    const wrapper = mount(RoleMenu, { attachTo: document.body });
    await wrapper.get('#role-menu-btn').trigger('click');
    await wrapper.findAll('[role="menuitemradio"]')[1]!.trigger('click');

    expect(loadRole).toHaveBeenCalledWith('hall_lead');
    expect(wrapper.find('[role="menu"]').exists()).toBe(false);
    expect(document.activeElement).toBe(wrapper.get('#role-menu-btn').element);
    wrapper.unmount();
  });

  it('lädt dieselbe Rolle nicht neu', async () => {
    const app = useAppStore();
    const loadRole = vi.spyOn(app, 'loadRole').mockResolvedValue();
    const wrapper = mount(RoleMenu, { attachTo: document.body });
    await wrapper.get('#role-menu-btn').trigger('click');
    await wrapper.findAll('[role="menuitemradio"]')[0]!.trigger('click');
    expect(loadRole).not.toHaveBeenCalled();
    wrapper.unmount();
  });

  it('bedient sich per Tastatur: Pfeile bewegen, Esc schließt und gibt den Fokus zurück', async () => {
    const wrapper = mount(RoleMenu, { attachTo: document.body });
    const trigger = wrapper.get('#role-menu-btn');
    await trigger.trigger('keydown', { key: 'ArrowDown' });
    const items = wrapper.findAll('[role="menuitemradio"]');
    expect(document.activeElement).toBe(items[0]!.element);

    await wrapper.get('[role="menu"]').trigger('keydown', { key: 'ArrowDown' });
    expect(document.activeElement).toBe(items[1]!.element);
    await wrapper.get('[role="menu"]').trigger('keydown', { key: 'End' });
    expect(document.activeElement).toBe(items[2]!.element);
    await wrapper.get('[role="menu"]').trigger('keydown', { key: 'ArrowDown' });
    expect(document.activeElement).toBe(items[0]!.element); // umlaufend

    await wrapper.get('[role="menu"]').trigger('keydown', { key: 'Escape' });
    expect(wrapper.find('[role="menu"]').exists()).toBe(false);
    expect(document.activeElement).toBe(trigger.element);
    wrapper.unmount();
  });

  it('schließt beim Tippen neben das Menü', async () => {
    const wrapper = mount(RoleMenu, { attachTo: document.body });
    await wrapper.get('#role-menu-btn').trigger('click');
    document.body.dispatchEvent(new Event('pointerdown', { bubbles: true }));
    await nextTick();
    expect(wrapper.find('[role="menu"]').exists()).toBe(false);
    wrapper.unmount();
  });
});

describe('Seitenleiste', () => {
  const SESSIONS = [
    {
      id: 's-old',
      role: 'shift_lead',
      startedAtUtc: '2026-10-10T08:00:00Z',
      lastActivityUtc: '2026-10-10T08:05:00Z',
      title: 'Ältere Frage',
      turnCount: 3,
    },
    {
      id: 's1',
      role: 'shift_lead',
      startedAtUtc: '2026-10-15T07:40:00Z',
      lastActivityUtc: '2026-10-15T07:42:00Z',
      title: 'Schichtübergabe',
      turnCount: 1,
    },
    {
      id: 's-yesterday',
      role: 'shift_lead',
      startedAtUtc: '2026-10-14T11:00:00Z',
      lastActivityUtc: '2026-10-14T11:30:00Z',
      title: 'Wie läuft RT-02?',
      turnCount: 2,
    },
  ];

  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date('2026-10-15T08:00:00Z'));
    apiMock.sessions.mockResolvedValue(SESSIONS);
    useConversationStore().hydrate(sessionPackage(), []);
  });
  afterEach(() => vi.useRealTimers());

  async function openSidebar() {
    const dialog = stubDialog();
    const opener = document.createElement('button');
    document.body.append(opener);
    opener.focus();
    const ui = useUiStore();
    const wrapper = mount(MobileSidebar, { attachTo: document.body });
    ui.sidebarOpen = true;
    await flushPromises();
    return { dialog, opener, ui, wrapper };
  }

  it('öffnet als Dialog, setzt den Fokus hinein und gruppiert Sitzungen nach Tag', async () => {
    const { dialog, wrapper } = await openSidebar();
    expect(dialog.showModal).toHaveBeenCalledTimes(1);
    expect(document.activeElement).toBe(wrapper.get('.close-btn').element);
    expect(wrapper.get('.m-side-title').text()).toBe('Sitzungen');

    expect(wrapper.findAll('.m-side-day').map((d) => d.text())).toEqual([
      'Heute',
      'Gestern',
      'Sa 10.10.',
    ]);
    const rows = wrapper.findAll('.m-session');
    expect(rows.map((r) => r.get('.m-session-title').text())).toEqual([
      'Schichtübergabe',
      'Wie läuft RT-02?',
      'Ältere Frage',
    ]);
    expect(rows[0]!.get('.m-session-meta').text()).toContain('09:42 · 1 Frage');
    expect(rows[1]!.get('.m-session-meta').text()).toContain('2 Fragen');
    // Die aktuelle Sitzung (s1) ist markiert.
    expect(rows[0]!.attributes('aria-current')).toBe('true');
    expect(rows[0]!.find('.m-session-badge').text()).toBe('aktuell');
    expect(rows[1]!.attributes('aria-current')).toBeUndefined();
    wrapper.unmount();
  });

  it('schließt mit Esc und gibt den Fokus an den Auslöser zurück', async () => {
    const { dialog, opener, ui, wrapper } = await openSidebar();
    await wrapper.get('dialog').trigger('keydown', { key: 'Escape' });
    expect(ui.sidebarOpen).toBe(false);
    await flushPromises();
    expect(dialog.close).toHaveBeenCalledTimes(1);
    expect(document.activeElement).toBe(opener);
    wrapper.unmount();
  });

  it('schließt beim Tippen auf den abgedunkelten Hintergrund und über den Schließen-Knopf', async () => {
    const { ui, wrapper } = await openSidebar();
    await wrapper.get('dialog').trigger('click');
    expect(ui.sidebarOpen).toBe(false);

    ui.sidebarOpen = true;
    await flushPromises();
    await wrapper.get('.close-btn').trigger('click');
    expect(ui.sidebarOpen).toBe(false);
    wrapper.unmount();
  });

  /** Zeigerereignis auf dem Dialog (jsdom kennt `PointerEvent` nicht, `pointerType` und `isPrimary` werden gesetzt). */
  function pointer(
    target: Element,
    type: 'pointerdown' | 'pointermove' | 'pointerup',
    x: number,
    y: number,
    pointerType = 'touch',
  ): void {
    const event = new MouseEvent(type, { clientX: x, clientY: y, bubbles: true });
    Object.defineProperties(event, {
      pointerType: { value: pointerType },
      isPrimary: { value: true },
      pointerId: { value: 1 },
    });
    target.dispatchEvent(event);
  }

  it('schließt, wenn man nach links wischt, und bleibt bei einer kurzen Bewegung offen', async () => {
    const { ui, wrapper } = await openSidebar();
    const dialog = wrapper.get('dialog').element;

    pointer(dialog, 'pointerdown', 250, 300);
    pointer(dialog, 'pointermove', 238, 301);
    pointer(dialog, 'pointerup', 238, 301);
    await nextTick();
    expect(ui.sidebarOpen).toBe(true); // 12 px sind kein Wischen

    pointer(dialog, 'pointerdown', 250, 300);
    pointer(dialog, 'pointermove', 120, 305);
    pointer(dialog, 'pointerup', 120, 305);
    await nextTick();
    expect(ui.sidebarOpen).toBe(false);
    wrapper.unmount();
  });

  it('lässt senkrechtes Wischen der Liste und das Maus-Ziehen in Ruhe', async () => {
    const { ui, wrapper } = await openSidebar();
    const dialog = wrapper.get('dialog').element;

    pointer(dialog, 'pointerdown', 250, 300);
    pointer(dialog, 'pointermove', 180, 500);
    pointer(dialog, 'pointerup', 180, 500);
    await nextTick();
    expect(ui.sidebarOpen).toBe(true);

    pointer(dialog, 'pointerdown', 250, 300, 'mouse');
    pointer(dialog, 'pointermove', 50, 300, 'mouse');
    pointer(dialog, 'pointerup', 50, 300, 'mouse');
    await nextTick();
    expect(ui.sidebarOpen).toBe(true);
    wrapper.unmount();
  });

  it('öffnet eine frühere Sitzung und schließt; die aktuelle bleibt, wo sie ist', async () => {
    const { ui, wrapper } = await openSidebar();
    const conversation = useConversationStore();
    const open = vi.spyOn(conversation, 'openSession').mockResolvedValue();
    await wrapper.findAll('.m-session')[1]!.trigger('click');
    expect(open).toHaveBeenCalledWith('s-yesterday');
    expect(ui.sidebarOpen).toBe(false);

    ui.sidebarOpen = true;
    await flushPromises();
    await wrapper.findAll('.m-session')[0]!.trigger('click');
    expect(open).toHaveBeenCalledTimes(1); // s1 ist schon geöffnet
    expect(ui.mobileView).toBe('ask');
    wrapper.unmount();
  });

  it('legt über „Neue Sitzung“ eine Sitzung an und wechselt zu „Fragen“', async () => {
    const { ui, wrapper } = await openSidebar();
    const conversation = useConversationStore();
    const start = vi.spyOn(conversation, 'startNewSession').mockResolvedValue();
    ui.setMobileView('start');
    await wrapper.get('.m-side-new').trigger('click');
    expect(start).toHaveBeenCalledTimes(1);
    expect(ui.sidebarOpen).toBe(false);
    expect(ui.mobileView).toBe('ask');
    wrapper.unmount();
  });

  it('zeigt unten Benutzer, Rolle und Status, führt zu Reports und meldet ab', async () => {
    apiMock.demoLogout.mockResolvedValue(null);
    const auth = useAuthStore();
    auth.enabled = true;
    auth.authenticated = true;
    const { ui, wrapper } = await openSidebar();
    expect(wrapper.get('.m-side-user .user-name').text()).toBe('Anna Berger');
    expect(wrapper.get('.m-side-user .user-title').text()).toBe('Schichtleiter');
    expect(wrapper.get('.status-band').text()).toContain('09:42');

    const actions = wrapper.findAll('.m-side-action');
    expect(actions.map((a) => a.text())).toEqual(['Reports', 'Abmelden', 'Über die Demo']);
    await actions[0]!.trigger('click');
    expect(ui.mobileView).toBe('reports');
    expect(ui.sidebarOpen).toBe(false);

    await actions[1]!.trigger('click');
    await flushPromises();
    expect(apiMock.demoLogout).toHaveBeenCalledTimes(1);
    expect(auth.authenticated).toBe(false);
    wrapper.unmount();
  });

  it('öffnet „Über die Demo“, schließt dabei die Seitenleiste', async () => {
    const { ui, wrapper } = await openSidebar();
    await wrapper.get('.m-side-about').trigger('click');
    expect(ui.aboutOpen).toBe(true);
    expect(ui.sidebarOpen).toBe(false);
    wrapper.unmount();
  });

  it('bietet Abmelden nur bei eingeschalteter Demo an', async () => {
    const { wrapper } = await openSidebar();
    expect(wrapper.findAll('.m-side-action').map((a) => a.text())).toEqual([
      'Reports',
      'Über die Demo',
    ]);
    wrapper.unmount();
  });

  it('meldet einen Ladefehler, statt leer zu bleiben', async () => {
    apiMock.sessions.mockRejectedValue(new Error('offline'));
    const { wrapper } = await openSidebar();
    expect(wrapper.get('.m-side-note').text()).toContain('konnten nicht geladen werden');
    wrapper.unmount();
  });
});

describe('Favoritenleiste (aufklappbar über dem Eingabefeld)', () => {
  function mountBand() {
    useFavoritesStore().favorites = structuredClone(FAVORITES);
    useConversationStore().hydrate(sessionPackage(), []);
    return mount(FavoritesBand);
  }

  it('ist zu, zeigt Titel und Anzahl, klappt auf und zu und merkt sich den Zustand', async () => {
    const ui = useUiStore();
    const wrapper = mountBand();
    const toggle = wrapper.get('.fav-m-toggle');
    expect(toggle.text()).toContain('Favoriten');
    expect(wrapper.get('.fav-m-count').text()).toBe('2');
    expect(toggle.attributes('aria-expanded')).toBe('false');
    expect(wrapper.get('.fav-m-inner').attributes('inert')).toBeDefined();

    await toggle.trigger('click');
    expect(ui.favoritesOpen).toBe(true);
    expect(toggle.attributes('aria-expanded')).toBe('true');
    expect(wrapper.get('.fav-m-inner').attributes('inert')).toBeUndefined();
    expect(wrapper.findAll('.fav-m-item').map((b) => b.text())).toEqual(['Übergabe', 'Eigener']);
    await vi.waitFor(async () => expect(await platform.storage.get('opc.favoritesOpen')).toBe('1'));

    // Neustart: der Zustand kommt aus dem Plattform-Speicher.
    setActivePinia(createPinia());
    const restarted = useUiStore();
    await restarted.init();
    expect(restarted.favoritesOpen).toBe(true);

    await toggle.trigger('click');
    expect(useUiStore().favoritesOpen).toBe(false);
    await vi.waitFor(async () => expect(await platform.storage.get('opc.favoritesOpen')).toBe('0'));
  });

  it('stellt die gewählte Frage und klappt danach wieder zu', async () => {
    const ui = useUiStore();
    ui.setFavoritesOpen(true);
    const conversation = useConversationStore();
    const askFrom = vi.fn().mockResolvedValue(undefined);
    conversation.askFrom = askFrom;
    const wrapper = mountBand();

    await wrapper.findAll('.fav-m-item')[1]!.trigger('click');
    expect(askFrom).toHaveBeenCalledWith('Wie läuft RT-02?');
    expect(ui.favoritesOpen).toBe(false);
  });

  it('ist in einer leeren Sitzung ausgeblendet, weil die Vorschläge die Favoriten zeigen', async () => {
    const wrapper = mountBand();
    expect(wrapper.get('#fav-band').classes()).not.toContain('is-session-empty');
    useConversationStore().turns = [];
    await nextTick();
    expect(wrapper.get('#fav-band').classes()).toContain('is-session-empty');
  });
});

describe('Leere Sitzung (Begrüßung und Vorschläge)', () => {
  beforeEach(() => mockMobile());
  afterEach(() => vi.unstubAllGlobals());

  it('begrüßt mit Namen, Ort, Schicht und Uhrzeit und zeigt die Favoriten als Vorschläge', async () => {
    useFavoritesStore().favorites = structuredClone(FAVORITES);
    const conversation = useConversationStore();
    const askFrom = vi.fn().mockResolvedValue(undefined);
    conversation.askFrom = askFrom;
    const wrapper = mount(ConversationView);

    expect(wrapper.get('.m-empty-title').text()).toBe('Guten Morgen, Anna.');
    expect(wrapper.get('.m-empty-sub').text()).toBe('Halle A · Frühschicht · 09:42');
    const cards = wrapper.findAll('.m-suggest');
    expect(cards.map((c) => c.get('.m-suggest-title').text())).toEqual(['Übergabe', 'Eigener']);
    // Der Titel „Eigener“ kürzt die Frage nicht ab: die Frage steht darunter.
    expect(cards[1]!.get('.m-suggest-question').text()).toBe('Wie läuft RT-02?');

    await cards[0]!.trigger('click');
    expect(askFrom).toHaveBeenCalledWith('Was ist bei der Schichtübergabe wichtig?');
  });

  it('zeigt sie nicht mehr, sobald die Sitzung Fragen hat', async () => {
    useFavoritesStore().favorites = structuredClone(FAVORITES);
    const conversation = useConversationStore();
    const wrapper = mount(ConversationView);
    expect(wrapper.find('.m-empty').exists()).toBe(true);
    conversation.hydrate(sessionPackage(), []);
    await nextTick();
    expect(wrapper.find('.m-empty').exists()).toBe(false);
  });
});

describe('Verlauf läuft mit, solange man nicht hochgescrollt hat', () => {
  let height = 0;
  let top = 0;
  const CLIENT = 400;

  beforeEach(() => {
    mockMobile();
    height = 800;
    top = 0;
    Object.defineProperty(HTMLElement.prototype, 'scrollHeight', {
      configurable: true,
      get: () => height,
    });
    Object.defineProperty(HTMLElement.prototype, 'clientHeight', {
      configurable: true,
      get: () => CLIENT,
    });
    Object.defineProperty(HTMLElement.prototype, 'scrollTop', {
      configurable: true,
      get: () => top,
      set: (value: number) => {
        top = value;
      },
    });
    // Die letzte Frage beginnt weit unten: das Ende des Inhalts bestimmt das Ziel.
    Object.defineProperty(HTMLElement.prototype, 'offsetTop', {
      configurable: true,
      get: () => 5000,
    });
  });
  afterEach(() => {
    for (const property of ['scrollHeight', 'clientHeight', 'scrollTop', 'offsetTop']) {
      delete (HTMLElement.prototype as unknown as Record<string, unknown>)[property];
    }
    vi.unstubAllGlobals();
  });

  async function grow(newHeight: number, change: () => void): Promise<void> {
    height = newHeight;
    change();
    await flushPromises();
  }

  it('folgt dem einlaufenden Inhalt bis ans Ende und stoppt nach dem Hochscrollen', async () => {
    const conversation = useConversationStore();
    conversation.hydrate(sessionPackage(), []);
    const wrapper = mount(ConversationView, { attachTo: document.body });
    await flushPromises();
    expect(top).toBe(400); // geladener Verlauf: ganz unten (800 - 400)

    // Eine Frage wird gestellt, die Wartekarte wächst um jeden Werkzeugaufruf.
    await grow(1000, () => {
      conversation.pending = { question: 'Neu?', toolCalls: [], card: null, error: null };
    });
    expect(top).toBe(600);
    await grow(1100, () => conversation.pending?.toolCalls.push({ tool: 'a' } as never));
    expect(top).toBe(700);

    // Nutzer scrollt nach oben: es folgt nichts mehr.
    top = 200;
    await wrapper.get('#conv').trigger('scroll');
    await grow(1300, () => conversation.pending?.toolCalls.push({ tool: 'b' } as never));
    expect(top).toBe(200);

    // Zurück ans Ende: es folgt wieder.
    top = 900; // 1300 - 400
    await wrapper.get('#conv').trigger('scroll');
    await grow(1500, () => conversation.pending?.toolCalls.push({ tool: 'c' } as never));
    expect(top).toBe(1100);
    wrapper.unmount();
  });

  it('richtet eine neue Frage wieder neu aus, auch wenn vorher hochgescrollt wurde', async () => {
    const conversation = useConversationStore();
    conversation.hydrate(sessionPackage(), []);
    const wrapper = mount(ConversationView, { attachTo: document.body });
    await flushPromises();
    top = 50;
    await wrapper.get('#conv').trigger('scroll'); // nach oben gescrollt

    await grow(1200, () => {
      conversation.pending = { question: 'Neu?', toolCalls: [], card: null, error: null };
    });
    expect(top).toBe(800); // 1200 - 400
    wrapper.unmount();
  });
});

describe('Sitzungsdialog (Desktop)', () => {
  it('öffnet beim Antippen, zeigt einen Schließen-Knopf und schließt ihn', async () => {
    const dialog = stubDialog();
    apiMock.sessions.mockResolvedValue([]);
    const ui = useUiStore();
    const wrapper = mount(SessionsDialog, { attachTo: document.body });

    ui.sessionsOpen = true;
    await flushPromises();
    expect(dialog.showModal).toHaveBeenCalledTimes(1);
    const close = wrapper.get('.close-btn');
    expect(close.attributes('aria-label')).toBe('Schließen');

    await close.trigger('click');
    await flushPromises();
    expect(ui.sessionsOpen).toBe(false);
    expect(dialog.close).toHaveBeenCalledTimes(1);
    wrapper.unmount();
  });

  it('schließt beim Tippen auf den Hintergrund', async () => {
    stubDialog();
    apiMock.sessions.mockResolvedValue([]);
    const ui = useUiStore();
    const wrapper = mount(SessionsDialog, { attachTo: document.body });
    ui.sessionsOpen = true;
    await flushPromises();

    await wrapper.get('dialog').trigger('click'); // Ziel ist der Dialog selbst, also der Hintergrund
    expect(ui.sessionsOpen).toBe(false);
    wrapper.unmount();
  });
});

describe('Scrollposition je Reiter', () => {
  /** Ein scrollender Container, der wie im Browser beim Ausblenden (`display: none`) auf 0 zurückfällt. */
  function scroller(id: string): HTMLElement {
    const element = document.createElement('div');
    element.id = id;
    let top = 0;
    Object.defineProperty(element, 'scrollTop', {
      get: () => top,
      set: (value: number) => {
        top = value;
      },
    });
    document.body.append(element);
    return element;
  }

  const Harness = defineComponent({
    setup() {
      useScrollMemory();
      return () => null;
    },
  });

  function setup(): Record<'start' | 'ask' | 'inbox' | 'feed', HTMLElement> {
    const elements = {
      start: scroller('mview-start'),
      ask: scroller('conv'),
      inbox: scroller('mview-inbox'),
      feed: scroller('mview-feed'),
    };
    mount(Harness);
    return elements;
  }

  it('stellt die Position beim Zurückkehren wieder her', async () => {
    const views = setup();
    const ui = useUiStore();
    ui.setMobileView('inbox');
    await nextTick();
    views.inbox.scrollTop = 240;

    ui.setMobileView('feed');
    views.inbox.scrollTop = 0; // der Browser setzt ausgeblendete Container zurück
    await nextTick();
    expect(views.feed.scrollTop).toBe(0);

    ui.setMobileView('inbox');
    await nextTick();
    expect(views.inbox.scrollTop).toBe(240);
  });

  it('behält die Position des Verlaufs, solange keine Frage dazukommt', async () => {
    const views = setup();
    const ui = useUiStore();
    ui.setMobileView('ask');
    await nextTick();
    views.ask.scrollTop = 130;

    ui.setMobileView('start');
    views.ask.scrollTop = 0;
    await nextTick();
    ui.setMobileView('ask');
    await nextTick();
    expect(views.ask.scrollTop).toBe(130);
  });

  it('überlässt den Verlauf der eigenen Ausrichtung, wenn währenddessen eine Frage gestellt wurde', async () => {
    const views = setup();
    const ui = useUiStore();
    const conversation = useConversationStore();
    ui.setMobileView('ask');
    await nextTick();
    views.ask.scrollTop = 130;

    ui.setMobileView('start');
    views.ask.scrollTop = 0;
    conversation.pending = { question: 'Neu?', toolCalls: [], card: null, error: null };
    await nextTick();
    ui.setMobileView('ask');
    await nextTick();
    expect(views.ask.scrollTop).toBe(0);
  });
});
