import { afterEach, describe, expect, it } from 'vitest';
import { takeInviteCode } from './inviteLink';

function open(url: string): void {
  window.history.replaceState({ marker: 1 }, '', url);
}

afterEach(() => open('/'));

describe('takeInviteCode', () => {
  it('liefert den Key und entfernt das Fragment, Pfad und Query bleiben', () => {
    open('/app/?lang=de#code=Abc123');

    expect(takeInviteCode()).toBe('Abc123');

    expect(window.location.pathname).toBe('/app/');
    expect(window.location.search).toBe('?lang=de');
    expect(window.location.hash).toBe('');
    expect(window.location.href).not.toContain('code');
    expect(window.history.state).toEqual({ marker: 1 });
  });

  it('dekodiert Prozent-Kodierung und lässt andere Fragment-Teile unberührt', () => {
    open('/#a=1&code=Ab%20c%2B1&conv');

    expect(takeInviteCode()).toBe('Ab c+1');

    expect(window.location.hash).toBe('#a=1&conv');
  });

  it('ignoriert ein Fragment ohne code und ändert die Adresse nicht', () => {
    open('/?x=1#conv');

    expect(takeInviteCode()).toBeNull();

    expect(window.location.search).toBe('?x=1');
    expect(window.location.hash).toBe('#conv');
  });

  it('entfernt auch einen leeren code, meldet dann aber keinen Key', () => {
    open('/#code=&b=2');

    expect(takeInviteCode()).toBeNull();

    expect(window.location.hash).toBe('#b=2');
  });

  it('übersteht ungültige Prozent-Kodierung', () => {
    open('/#code=%E0%A4%A');

    expect(takeInviteCode()).toBe('%E0%A4%A');
    expect(window.location.hash).toBe('');
  });

  it('verwechselt keine Parameter mit ähnlichem Namen', () => {
    open('/#mycode=1&codes=2');

    expect(takeInviteCode()).toBeNull();
    expect(window.location.hash).toBe('#mycode=1&codes=2');
  });
});
