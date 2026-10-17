// Web-Push für dringende Postfach-Fälle: nur die Schnittstelle der Oberfläche.
// Server-Seite (VAPID-Schlüssel, Abo-Speicher, Versand) ist bewusst noch nicht gebaut, siehe README „Weboberfläche“.

export type PushPermission = 'granted' | 'denied' | 'default' | 'unsupported';

/** Abo-Daten, die der Server zum Versenden braucht (Standardform der Push-API). */
export interface PushSubscriptionData {
  endpoint: string;
  keys: { p256dh: string; auth: string };
}

export interface PushChannel {
  readonly supported: boolean;
  permission(): PushPermission;
  /** Fragt die Erlaubnis und legt das Abo an; null, wenn abgelehnt oder nicht konfiguriert. */
  subscribe(): Promise<PushSubscriptionData | null>;
  unsubscribe(): Promise<void>;
}

/** Öffentlicher VAPID-Schlüssel (Base64URL); ohne ihn bleibt Push aus. */
const VAPID_PUBLIC_KEY = import.meta.env.VITE_VAPID_PUBLIC_KEY as string | undefined;

function base64UrlToBytes(value: string): Uint8Array<ArrayBuffer> {
  const padded = value
    .replace(/-/g, '+')
    .replace(/_/g, '/')
    .padEnd(Math.ceil(value.length / 4) * 4, '=');
  const raw = atob(padded);
  const bytes = new Uint8Array(new ArrayBuffer(raw.length));
  for (let i = 0; i < raw.length; i += 1) bytes[i] = raw.charCodeAt(i);
  return bytes;
}

function toBase64Url(buffer: ArrayBuffer | null): string {
  if (!buffer) return '';
  let text = '';
  for (const byte of new Uint8Array(buffer)) text += String.fromCharCode(byte);
  return btoa(text).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

export class WebPushChannel implements PushChannel {
  get supported(): boolean {
    return (
      'serviceWorker' in navigator &&
      'PushManager' in globalThis &&
      'Notification' in globalThis &&
      Boolean(VAPID_PUBLIC_KEY)
    );
  }

  permission(): PushPermission {
    return this.supported ? Notification.permission : 'unsupported';
  }

  async subscribe(): Promise<PushSubscriptionData | null> {
    if (!this.supported || !VAPID_PUBLIC_KEY) return null;
    if ((await Notification.requestPermission()) !== 'granted') return null;
    const registration = await navigator.serviceWorker.ready;
    const subscription = await registration.pushManager.subscribe({
      userVisibleOnly: true,
      applicationServerKey: base64UrlToBytes(VAPID_PUBLIC_KEY),
    });
    return {
      endpoint: subscription.endpoint,
      keys: {
        p256dh: toBase64Url(subscription.getKey('p256dh')),
        auth: toBase64Url(subscription.getKey('auth')),
      },
    };
  }

  async unsubscribe(): Promise<void> {
    if (!('serviceWorker' in navigator)) return;
    const registration = await navigator.serviceWorker.ready;
    await (await registration.pushManager.getSubscription())?.unsubscribe();
  }
}
