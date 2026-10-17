// Erklärseite: erscheint ohne Sitzung statt der Anmeldung, meldet mit dem Code an, wird von Einladungslinks übersprungen
// und ist angemeldet wie lokal ohne Demo-Zugang über „Über die Demo“ erreichbar.
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '@/api/errors';
import { apiMock, resetApiMock } from '@/test/apiMock';
import App from '@/App.vue';
import { useAppStore } from '@/stores/app';
import { useAuthStore } from '@/stores/auth';
import { SESSION_HINT_KEY } from '@/stores/sessionHint';
import { useUiStore } from '@/stores/ui';
import AboutView from './AboutView.vue';

vi.mock('@/api/client', () => ({ api: apiMock }));

// Der Demo-Zugang ist ein Build-Schalter; die Oberfläche fragt den Server nicht danach.
vi.mock('@/config', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/config')>()),
  DEMO_ACCESS: true,
}));

let mounted: VueWrapper | null = null;

beforeEach(() => {
  setActivePinia(createPinia());
  resetApiMock();
  window.history.replaceState(null, '', '/');
  window.localStorage.clear();
  // jsdom kennt kein Scrollen.
  window.scrollTo = vi.fn() as unknown as typeof window.scrollTo;
  Element.prototype.scrollIntoView = vi.fn() as unknown as Element['scrollIntoView'];
  vi.spyOn(useAppStore(), 'init').mockResolvedValue(undefined);
});

afterEach(() => {
  mounted?.unmount();
  mounted = null;
  vi.restoreAllMocks();
  window.history.replaceState(null, '', '/');
});

async function mountApp(state: { enabled: boolean; authenticated: boolean }): Promise<VueWrapper> {
  // Ohne Demo-Zugang (enabled false) meldet die Anwendung sofort bereit; angemeldet heißt: gültiger lokaler Hinweis.
  useAuthStore().enabled = state.enabled;
  if (state.authenticated) window.localStorage.setItem(SESSION_HINT_KEY, '2999-01-01T00:00:00Z');
  mounted = mount(App, { attachTo: document.body });
  await flushPromises();
  return mounted;
}

const appRootHidden = (wrapper: VueWrapper): boolean => {
  const root = wrapper.find('#app-root');
  return !root.exists() || (root.element as HTMLElement).style.display === 'none';
};

describe('Erklärseite ohne Sitzung', () => {
  it('ersetzt die Anmeldeansicht und enthält Überschrift, Abschnitte und Code-Feld', async () => {
    const wrapper = await mountApp({ enabled: true, authenticated: false });

    expect(wrapper.find('.about-page').exists()).toBe(true);
    expect(wrapper.get('h1').text()).toContain('Frag deine Halle');
    expect(wrapper.findAll('h1')).toHaveLength(1);
    for (const id of [
      'so-funktioniert-es',
      'rollen',
      'grenzen',
      'sicherheit',
      'zugang',
      'projekt',
    ]) {
      expect(wrapper.find(`#${id}`).exists(), id).toBe(true);
    }
    expect(wrapper.get('#zugang #access-code').attributes('type')).toBe('password');
    expect(wrapper.find('.about-top button').exists()).toBe(false); // kein Rückweg ohne Sitzung
    expect(appRootHidden(wrapper)).toBe(true);
    expect(document.title).toContain('Frag deine Halle');
  });

  it('nennt Redis als Speicher mit dem Hinweis auf einen ergänzbaren Langzeitspeicher, PostgreSQL ist keine Technik mehr', async () => {
    const wrapper = await mountApp({ enabled: true, authenticated: false });

    const steps = wrapper.findAll('.about-flow-step').map((step) => step.text());
    const storage = steps.find((text) => text.includes('Speichert'));
    expect(storage).toContain('Redis');
    expect(storage).toContain(
      'Redis hält den aktuellen Zustand und die verdichteten Zeitbereiche.',
    );
    expect(storage).toContain(
      'Ein Langzeitspeicher (z. B. PostgreSQL) lässt sich leicht ergänzen.',
    );
    expect(steps.filter((text) => text.includes('PostgreSQL und Redis'))).toHaveLength(0);

    const chips = wrapper.findAll('#projekt li').map((item) => item.text());
    expect(chips).toContain('Redis');
    expect(chips).not.toContain('PostgreSQL');
  });

  it('meldet mit dem Code aus dem Zugangsbereich an und zeigt danach die Anwendung', async () => {
    apiMock.demoLogin.mockResolvedValue({ token: 't', expiresAtUtc: '2999-01-01T00:00:00Z' });
    const wrapper = await mountApp({ enabled: true, authenticated: false });

    await wrapper.get('#access-code').setValue('richtig');
    await wrapper.get('form.access-form').trigger('submit');
    await flushPromises();

    expect(apiMock.demoLogin).toHaveBeenCalledWith('richtig');
    expect(wrapper.find('.about-page').exists()).toBe(false);
    expect(wrapper.find('#app-root').exists()).toBe(true);
    expect(useAppStore().init).toHaveBeenCalled();
  });

  it('zeigt Fehler im Zugangsbereich und bleibt auf der Seite', async () => {
    apiMock.demoLogin.mockRejectedValue(new ApiError(401, 'x'));
    const wrapper = await mountApp({ enabled: true, authenticated: false });

    await wrapper.get('#access-code').setValue('falsch');
    await wrapper.get('form.access-form').trigger('submit');
    await flushPromises();

    expect(wrapper.find('.about-page').exists()).toBe(true);
    expect(wrapper.find('#zugang [role="alert"]').exists()).toBe(true);
  });

  it('wird von einem Einladungslink übersprungen', async () => {
    window.history.replaceState(null, '', '/#code=einladung');
    apiMock.demoLogin.mockResolvedValue({ token: 't', expiresAtUtc: '2999-01-01T00:00:00Z' });
    const wrapper = await mountApp({ enabled: true, authenticated: false });

    expect(apiMock.demoLogin).toHaveBeenCalledWith('einladung');
    expect(wrapper.find('.about-page').exists()).toBe(false);
    expect(wrapper.find('#app-root').exists()).toBe(true);
    expect(window.location.hash).toBe('');
  });
});

describe('Über die Demo in der Anwendung', () => {
  it('öffnet die Seite angemeldet über den Kopf und führt zurück in die Demo', async () => {
    const wrapper = await mountApp({ enabled: true, authenticated: true });
    expect(wrapper.find('.about-page').exists()).toBe(false);

    await wrapper.get('.header-about').trigger('click');
    expect(wrapper.find('.about-page').exists()).toBe(true);
    expect(appRootHidden(wrapper)).toBe(true);
    expect(wrapper.find('#access-code').exists()).toBe(false);
    expect(wrapper.get('#zugang').text()).toContain('Du bist angemeldet.');

    await wrapper.get('.about-top button').trigger('click');
    expect(wrapper.find('.about-page').exists()).toBe(false);
    expect(appRootHidden(wrapper)).toBe(false);
  });

  it('bleibt ohne Demo-Zugang direkt in der App und ist über den Link erreichbar', async () => {
    const wrapper = await mountApp({ enabled: false, authenticated: false });
    expect(wrapper.find('.about-page').exists()).toBe(false);
    expect(wrapper.find('#app-root').exists()).toBe(true);

    useUiStore().openAbout();
    await flushPromises();

    expect(wrapper.get('#zugang').text()).toContain('braucht keinen Zugangscode');
    expect(wrapper.find('#access-code').exists()).toBe(false);
  });

  it('kehrt nach dem Abmelden zur Seite zurück, ohne sie als geöffnet zu merken', async () => {
    apiMock.demoLogout.mockResolvedValue(null);
    const wrapper = await mountApp({ enabled: true, authenticated: true });
    useUiStore().openAbout();
    await flushPromises();

    await useAuthStore().logout();
    await flushPromises();

    expect(wrapper.find('.about-page').exists()).toBe(true);
    expect(wrapper.find('#access-code').exists()).toBe(true);
    expect(useUiStore().aboutOpen).toBe(false);
  });
});

describe('AboutView (Inhalt)', () => {
  const mountAbout = () => {
    const auth = useAuthStore();
    auth.checked = true;
    auth.enabled = true;
    return mount(AboutView, { attachTo: document.body });
  };

  it('verweist auf Impressum, Datenschutz und das Repository', () => {
    const wrapper = mountAbout();
    const hrefs = wrapper.findAll('a').map((a) => a.attributes('href'));
    expect(hrefs).toContain('https://vompa.dev/impressum.html');
    expect(hrefs).toContain('https://vompa.dev/datenschutz.html');
    expect(hrefs).toContain('https://github.com/vompa/OPCAgentSample');
    const external = wrapper.findAll('a[target="_blank"]');
    expect(external.length).toBeGreaterThan(0);
    expect(external.every((a) => a.attributes('rel')?.includes('noopener'))).toBe(true);
    wrapper.unmount();
  });

  it('gibt allen Bildern Größe und Beschreibung, WebP mit PNG-Rückfall (kein Layoutsprung)', () => {
    const wrapper = mountAbout();
    const images = wrapper.findAll('img');
    expect(images.length).toBeGreaterThanOrEqual(3);
    for (const image of images) {
      expect(image.attributes('width')).toMatch(/^\d+$/);
      expect(image.attributes('height')).toMatch(/^\d+$/);
      expect(image.attributes('alt')?.length).toBeGreaterThan(20);
      expect(image.attributes('src')).toMatch(/\.png$/);
      expect(image.element.closest('picture')?.querySelector('source')?.getAttribute('type')).toBe(
        'image/webp',
      );
    }
    // Die Bilder des Einstiegs liegen über dem Falz (LCP) und laden sofort, die übrigen erst bei Bedarf.
    expect(images.slice(0, 2).every((i) => i.attributes('loading') === undefined)).toBe(true);
    expect(images.slice(2).every((i) => i.attributes('loading') === 'lazy')).toBe(true);
    wrapper.unmount();
  });

  it('nennt die Grenzen der Demo und verwendet keine Gedankenstriche', () => {
    const wrapper = mountAbout();
    const text = wrapper.text();
    expect(text).toContain('Do 15.10.2026, 09:42');
    expect(text).toContain('Claude Haiku');
    expect(text).toContain('Nur lesend');
    expect(text).toContain('Scale-to-zero');
    expect(text).not.toMatch(/[–—]/);
    wrapper.unmount();
  });
});
