// Hält den Live-Strom nur offen, solange jemand die Seite benutzt (Scale-to-zero: offene Tabs dürfen den Server nicht wachhalten).
// - Tab länger als HIDDEN_PAUSE_MS verborgen: Verbindung schließen; beim Sichtbarwerden wieder öffnen.
// - Der Server hat `idle` gemeldet (Verbindung pausiert): die nächste Eingabe (pointerdown/keydown) verbindet neu.

export const HIDDEN_PAUSE_MS = 60_000;

/** Der Teil des Live-Clients, den die Überwachung braucht. */
export interface PausableLive {
  pause(): void;
  resume(): void;
}

export class LiveActivity {
  private hiddenTimer: ReturnType<typeof setTimeout> | undefined;

  constructor(
    private readonly live: PausableLive,
    private readonly doc: Document = document,
  ) {}

  attach(): void {
    this.doc.addEventListener('visibilitychange', this.onVisibility);
    this.doc.addEventListener('pointerdown', this.onInput);
    this.doc.addEventListener('keydown', this.onInput);
    if (this.doc.hidden) this.armHiddenTimer();
  }

  detach(): void {
    this.doc.removeEventListener('visibilitychange', this.onVisibility);
    this.doc.removeEventListener('pointerdown', this.onInput);
    this.doc.removeEventListener('keydown', this.onInput);
    clearTimeout(this.hiddenTimer);
  }

  private armHiddenTimer(): void {
    clearTimeout(this.hiddenTimer);
    this.hiddenTimer = setTimeout(() => {
      if (this.doc.hidden) this.live.pause();
    }, HIDDEN_PAUSE_MS);
  }

  private readonly onVisibility = (): void => {
    if (this.doc.hidden) {
      this.armHiddenTimer();
      return;
    }
    clearTimeout(this.hiddenTimer);
    this.live.resume();
  };

  private readonly onInput = (): void => {
    this.live.resume();
  };
}
