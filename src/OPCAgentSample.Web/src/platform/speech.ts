// Spracheingabe hinter einer Schnittstelle (Browser: Web Speech API, Capacitor: natives Plugin).

export interface SpeechSession {
  /** Beendet die Aufnahme; `onEnd` folgt. */
  stop(): void;
}

export interface SpeechCallbacks {
  onResult(text: string): void;
  onError(): void;
  onEnd(): void;
}

export interface SpeechInput {
  readonly supported: boolean;
  start(callbacks: SpeechCallbacks): SpeechSession;
}

interface RecognitionResultEvent {
  results: ArrayLike<ArrayLike<{ transcript: string }>>;
}

interface Recognition {
  lang: string;
  interimResults: boolean;
  maxAlternatives: number;
  addEventListener(type: string, listener: (event: RecognitionResultEvent) => void): void;
  start(): void;
  stop(): void;
}

type RecognitionConstructor = new () => Recognition;

function recognitionConstructor(): RecognitionConstructor | undefined {
  const scope = globalThis as unknown as Record<string, RecognitionConstructor | undefined>;
  return scope.SpeechRecognition ?? scope.webkitSpeechRecognition;
}

/** Web Speech API; ohne Unterstützung bleibt `supported` false und die Oberfläche blendet den Knopf aus. */
export class WebSpeechInput implements SpeechInput {
  get supported(): boolean {
    return recognitionConstructor() !== undefined;
  }

  start(callbacks: SpeechCallbacks): SpeechSession {
    const Ctor = recognitionConstructor();
    if (!Ctor) throw new Error('Spracheingabe wird nicht unterstützt.');
    const recognition = new Ctor();
    recognition.lang = 'de-AT';
    recognition.interimResults = false;
    recognition.maxAlternatives = 1;
    recognition.addEventListener('result', (event) => {
      const text = event.results[0]?.[0]?.transcript;
      if (text) callbacks.onResult(text);
    });
    recognition.addEventListener('error', () => callbacks.onError());
    recognition.addEventListener('end', () => callbacks.onEnd());
    recognition.start();
    return { stop: () => recognition.stop() };
  }
}
