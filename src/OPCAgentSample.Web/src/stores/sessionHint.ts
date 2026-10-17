// Lokaler, nicht geheimer Hinweis „angemeldet bis <Zeitpunkt>“ aus der Login-Antwort. Er erlaubt, beim Öffnen sofort zu wecken
// und ohne Erklärseite zu starten. Der Zugangscode und das Sitzungstoken werden NICHT gespeichert; ob die Sitzung wirklich noch
// gilt, entscheidet der Server (401 löscht den Hinweis).
import { platform } from '@/platform';

export const SESSION_HINT_KEY = 'opc.sessionUntil';

/** Liest den Hinweis; abgelaufen oder unlesbar = null (und der Eintrag wird verworfen). */
export async function readSessionHint(now = Date.now()): Promise<string | null> {
  const value = await platform.storage.get(SESSION_HINT_KEY);
  if (!value) return null;
  if (Number.isFinite(Date.parse(value)) && Date.parse(value) > now) return value;
  await platform.storage.set(SESSION_HINT_KEY, '');
  return null;
}

export function writeSessionHint(expiresAtUtc: string): Promise<void> {
  return platform.storage.set(SESSION_HINT_KEY, expiresAtUtc);
}

export function clearSessionHint(): Promise<void> {
  return platform.storage.set(SESSION_HINT_KEY, '');
}
