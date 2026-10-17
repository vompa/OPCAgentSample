// Fehler der HTTP-API: Statuscode, Meldung des Servers, optional ein Code und, bei 429, die Wartezeit.
export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
    /** Wartezeit in Sekunden (Kopf `Retry-After`), sonst nicht gesetzt. */
    readonly retryAfterSeconds?: number,
    /** Maschinenlesbarer Code aus dem Problem-JSON des Servers (z. B. `session-not-found`). */
    readonly code?: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

/** Statuscodes, mit denen ein schlafendes oder gerade startendes Backend (Container App, Ingress) antwortet. */
const STARTING_STATUSES: readonly number[] = [502, 503, 504];

/**
 * Das Backend schläft oder startet noch? Das gilt für 502/503/504 und für Netzwerkfehler oder Zeitüberschreitungen
 * (kein `ApiError`), nicht für Antworten des Servers wie 401 oder 429.
 */
export function isStartingUp(error: unknown): boolean {
  if (error instanceof ApiError) return STARTING_STATUSES.includes(error.status);
  return true; // TypeError (Netzwerk), TimeoutError/AbortError (Zeitüberschreitung) und Ähnliches
}
