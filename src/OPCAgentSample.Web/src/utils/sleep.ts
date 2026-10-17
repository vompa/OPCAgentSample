// Warten zwischen Versuchen; Tests ersetzen die Funktion, damit Backoff-Abläufe ohne echte Wartezeit laufen.
let implementation: (ms: number) => Promise<void> = (ms) =>
  new Promise((resolve) => setTimeout(resolve, ms));

export function sleep(ms: number): Promise<void> {
  return implementation(ms);
}

/** Für Tests. */
export function setSleep(next: (ms: number) => Promise<void>): void {
  implementation = next;
}
