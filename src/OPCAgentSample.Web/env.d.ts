/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Basisadresse der API; leer = gleiche Herkunft (Standard). Für Capacitor z. B. https://opc.example.com */
  readonly VITE_API_BASE?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}

interface ImportMetaEnv {
  /** `true`: Zugangstoken als Bearer-Header senden und speichern (Capacitor); sonst Cookie. */
  readonly VITE_AUTH_TOKEN?: string;
}

interface ImportMetaEnv {
  /** Öffentlicher VAPID-Schlüssel für Web-Push (Base64URL); leer = Push aus. */
  readonly VITE_VAPID_PUBLIC_KEY?: string;
}

interface ImportMetaEnv {
  /** `true`: Der Demo-Zugang (Zugangscode) ist an; ohne Anmeldung ruft die Oberfläche keinen API-Endpunkt auf. */
  readonly VITE_DEMO_ACCESS?: string;
}
