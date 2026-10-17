// Meldet "nicht angemeldet" (401) von beliebigen API-Aufrufen an die Anmeldung, ohne dass der Client den Store kennt.
let handler: (() => void) | null = null;

/** Registriert die Funktion, die bei einem 401 aufgerufen wird (null entfernt sie). */
export function setUnauthorizedHandler(next: (() => void) | null): void {
  handler = next;
}

export function notifyUnauthorized(): void {
  handler?.();
}
