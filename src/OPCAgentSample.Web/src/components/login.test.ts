import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError } from '@/api/errors';
import { apiMock, resetApiMock } from '@/test/apiMock';
import { useAuthStore } from '@/stores/auth';
import { useIdentityStore } from '@/stores/identity';
import { ME } from '@/test/fixtures';
import DataNotice from './DataNotice.vue';
import AccessForm from './AccessForm.vue';
import StatusBar from './StatusBar.vue';
import UserCard from './UserCard.vue';
import { useLiveStore } from '@/stores/live';

vi.mock('@/api/client', () => ({ api: apiMock }));

beforeEach(() => {
  setActivePinia(createPinia());
  resetApiMock();
});

describe('AccessForm', () => {
  const mountLogin = () => mount(AccessForm, { attachTo: document.body });

  it('hat beschriftetes Feld, Anfrage-Link per E-Mail und Knopf in Tippgröße-Klasse', () => {
    const wrapper = mountLogin();

    const input = wrapper.get('#access-code');
    expect(wrapper.get('label[for="access-code"]').text()).toBe('Zugangscode');
    expect(input.attributes('type')).toBe('password');
    expect(wrapper.text()).toContain('Zugangscode auf Anfrage');
    expect(wrapper.get('button[type="submit"]').attributes('disabled')).toBeDefined(); // ohne Eingabe

    // Die Adresse steht nicht im Quelltext der Komponente, sondern wird erst beim Einbinden zusammengesetzt.
    const mail = wrapper.get('a.access-mail');
    expect(mail.attributes('href')).toMatch(/^mailto:[^@]+@[^?]+\?subject=/);
    wrapper.unmount();
  });

  it('fokussiert beim Öffnen kein Feld (die Seite soll nicht springen)', () => {
    const wrapper = mountLogin();
    expect(document.activeElement).not.toBe(wrapper.get('#access-code').element);
    wrapper.unmount();
  });

  it('sendet den Code, zeigt bei falschem Code den Fehler (role=alert) und leert das Feld', async () => {
    apiMock.demoLogin.mockRejectedValue(new ApiError(401, 'x'));
    const wrapper = mountLogin();
    await wrapper.get('#access-code').setValue('  falsch  ');

    await wrapper.get('form').trigger('submit');
    await flushPromises();

    expect(apiMock.demoLogin).toHaveBeenCalledWith('falsch');
    expect(wrapper.get('[role="alert"]').text()).toBe('Der Zugangscode ist nicht gültig.');
    expect(wrapper.get('#access-code').attributes('aria-invalid')).toBe('true');
    expect((wrapper.get('#access-code').element as HTMLInputElement).value).toBe('');
    expect(document.activeElement).toBe(wrapper.get('#access-code').element);
    wrapper.unmount();
  });

  it('nennt bei 429 die Wartezeit', async () => {
    apiMock.demoLogin.mockRejectedValue(new ApiError(429, 'x', 600));
    const wrapper = mountLogin();
    await wrapper.get('#access-code').setValue('x');

    await wrapper.get('form').trigger('submit');
    await flushPromises();

    expect(wrapper.get('[role="alert"]').text()).toContain('10 Minuten');
    wrapper.unmount();
  });

  it('meldet bei richtigem Code an und gibt die Anwendung frei', async () => {
    apiMock.demoLogin.mockResolvedValue({ token: 't', expiresAtUtc: '2999-01-01T00:00:00Z' });
    const auth = useAuthStore();
    auth.checked = true;
    auth.enabled = true;
    const wrapper = mountLogin();
    expect(auth.needsLogin).toBe(true);
    await wrapper.get('#access-code').setValue('richtig');

    await wrapper.get('form').trigger('submit');
    await flushPromises();

    expect(auth.ready).toBe(true);
    expect(wrapper.find('[role="alert"]').exists()).toBe(false);
    wrapper.unmount();
  });

  it('zeigt nach abgelaufener Sitzung den Hinweis', () => {
    useAuthStore().error = 'Die Sitzung ist abgelaufen. Bitte erneut anmelden.';
    const wrapper = mountLogin();
    expect(wrapper.get('[role="alert"]').text()).toContain('abgelaufen');
    wrapper.unmount();
  });
});

describe('Abmelden im Benutzermenü', () => {
  it('ist nur bei eingeschalteter Demo sichtbar und meldet ab', async () => {
    useIdentityStore().me = ME;
    apiMock.demoLogout.mockResolvedValue(null);
    const auth = useAuthStore();
    const wrapper = mount(UserCard);
    expect(wrapper.find('.user-logout').exists()).toBe(false);

    auth.enabled = true;
    auth.authenticated = true;
    await wrapper.vm.$nextTick();
    await wrapper.get('.user-logout').trigger('click');
    await flushPromises();

    expect(apiMock.demoLogout).toHaveBeenCalledTimes(1);
    expect(auth.authenticated).toBe(false);
  });
});

describe('Hinweise zu Daten und Live-Strom', () => {
  const withStatus = (patch: Partial<typeof ME.status>) => {
    useIdentityStore().me = { ...ME, status: { ...ME.status, ...patch } };
  };

  it('zeigt beim Kaltstart "Historie wird geladen" statt Zahlen als fertig auszugeben', () => {
    withStatus({ historyLoading: true });
    const wrapper = mount(DataNotice);

    expect(wrapper.get('[role="status"]').text()).toContain('Historie wird geladen');
    expect(wrapper.get('[role="status"]').classes()).toContain('is-loading');
  });

  it('zeigt den Zeitbasis-Hinweis des Servers und sonst nichts', () => {
    withStatus({ dataWarning: 'Die Simulationsuhr liegt vor dem Datenstand.' });
    const wrapper = mount(DataNotice);
    expect(wrapper.get('[role="status"]').text()).toBe(
      'Die Simulationsuhr liegt vor dem Datenstand.',
    );

    withStatus({});
    return wrapper.vm
      .$nextTick()
      .then(() => expect(wrapper.find('.data-notice').exists()).toBe(false));
  });

  it('blendet in der Statusleiste Pause, Kaltstart und Zeitbasis im selben Platzhalter ein', async () => {
    withStatus({});
    const live = useLiveStore();
    const wrapper = mount(StatusBar);
    const hint = () => wrapper.find('.live-hint:not([hidden])');
    expect(hint().exists()).toBe(false);

    live.connection = 'paused';
    await wrapper.vm.$nextTick();
    expect(hint().text()).toContain('Live pausiert');

    live.connection = 'open';
    withStatus({ historyLoading: true });
    await wrapper.vm.$nextTick();
    expect(hint().text()).toContain('Historie wird geladen');

    withStatus({ dataWarning: 'Zeitbasis passt nicht.' });
    await wrapper.vm.$nextTick();
    expect(hint().text()).toContain('Zeitbasis');
    expect(hint().attributes('title')).toBe('Zeitbasis passt nicht.');

    live.connection = 'lost';
    await wrapper.vm.$nextTick();
    expect(hint().text()).toContain('Live getrennt');
  });
});
