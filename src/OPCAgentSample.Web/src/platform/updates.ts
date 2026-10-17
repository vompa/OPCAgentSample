// Versionswechsel: Der neue Service Worker übernimmt sofort (skipWaiting + clients.claim in sw.ts).
// Hatte die Seite schon einen Service Worker, lädt sie einmal neu, damit das passende Bundle läuft.

interface ControllerChangeTarget {
  readonly controller: unknown;
  addEventListener(type: 'controllerchange', listener: () => void): void;
}

/** Lädt die Seite einmalig neu, wenn ein neuer Service Worker einen bestehenden ablöst. */
export function reloadOnServiceWorkerUpdate(
  container: ControllerChangeTarget | undefined,
  reload: () => void,
): void {
  if (!container || !container.controller) return;
  let reloaded = false;
  container.addEventListener('controllerchange', () => {
    if (reloaded) return;
    reloaded = true;
    reload();
  });
}
