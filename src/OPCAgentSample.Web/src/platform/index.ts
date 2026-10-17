// Plattformdienste der Oberfläche. Die Kernlogik kennt nur die Schnittstellen; Tests und Capacitor tauschen sie aus.
import { IndexedDbCache, type DataCache } from './cache';
import { BrowserStorage, type KeyValueStorage } from './storage';
import { WebPushChannel, type PushChannel } from './push';
import { WebSpeechInput, type SpeechInput } from './speech';

export interface Platform {
  storage: KeyValueStorage;
  /** Zuletzt gesehene Live-Daten je Rolle (für den nächsten Besuch). */
  cache: DataCache;
  speech: SpeechInput;
  push: PushChannel;
}

export const platform: Platform = {
  storage: new BrowserStorage(),
  cache: new IndexedDbCache(),
  speech: new WebSpeechInput(),
  push: new WebPushChannel(),
};

/** Ersetzt einzelne Dienste (Tests, native Hülle). */
export function configurePlatform(overrides: Partial<Platform>): void {
  Object.assign(platform, overrides);
}
