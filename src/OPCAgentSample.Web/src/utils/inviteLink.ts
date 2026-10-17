// Einladungslink: https://<host>/#code=<key>. Das Fragment geht nie an den Server; der Key wird sofort aus der Adresse entfernt
// und nirgends gespeichert (weder localStorage noch Token-Speicher).

const CODE_PARAMETER = 'code';

function decode(value: string): string {
  try {
    return decodeURIComponent(value);
  } catch {
    return value; // ungültige Prozent-Kodierung: den Rohwert versuchen
  }
}

/**
 * Nimmt `code` aus dem Fragment der Adresse: Gibt den Key zurück (leer = keiner) und entfernt den Eintrag per
 * `history.replaceState` aus der Adresszeile. Pfad, Query und andere Fragment-Teile bleiben unberührt.
 */
export function takeInviteCode(): string | null {
  const parts = window.location.hash.replace(/^#/, '').split('&');
  let code: string | null = null;
  let found = false;
  const rest: string[] = [];
  for (const part of parts) {
    const separator = part.indexOf('=');
    if (separator > 0 && decode(part.slice(0, separator)) === CODE_PARAMETER) {
      found = true;
      const value = decode(part.slice(separator + 1)).trim();
      if (code === null && value) code = value;
    } else if (part) {
      rest.push(part);
    }
  }

  if (!found) return null;

  const hash = rest.length ? `#${rest.join('&')}` : '';
  const { pathname, search } = window.location;
  window.history.replaceState(window.history.state, '', `${pathname}${search}${hash}`);
  return code;
}
