/// <reference lib="webworker" />
// Service Worker: Offline-Hülle (vorab zwischengespeicherte Dateien) und Handler für Web-Push.
// Daten (/api) werden nie zwischengespeichert: Anlagenzustand und Antworten müssen immer frisch sein.
import {
  cleanupOutdatedCaches,
  createHandlerBoundToURL,
  precacheAndRoute,
} from 'workbox-precaching';
import { NavigationRoute, registerRoute } from 'workbox-routing';

declare const self: ServiceWorkerGlobalScope;

cleanupOutdatedCaches();
precacheAndRoute(self.__WB_MANIFEST);

// Navigation: App-Hülle aus dem Cache, außer für die API.
registerRoute(
  new NavigationRoute(createHandlerBoundToURL('/index.html'), {
    denylist: [/^\/api\//],
  }),
);

// Neue Version sofort aktivieren und übernehmen: sonst bliebe sie hinter einem alten Worker (z. B. aus der Zeit,
// als der Container die Oberfläche auslieferte) auf „waiting“ und die Seite zeigte weiter die alte App.
self.addEventListener('install', () => {
  void self.skipWaiting();
});
self.addEventListener('activate', (event) => {
  event.waitUntil(self.clients.claim());
});
self.addEventListener('message', (event) => {
  if (event.data?.type === 'SKIP_WAITING') void self.skipWaiting();
});

/** Nutzlast, die der (noch nicht gebaute) Server für dringende Postfach-Fälle sendet. */
interface PushPayload {
  title?: string;
  body?: string;
  /** Pfad, der beim Tippen auf die Benachrichtigung geöffnet wird. */
  url?: string;
  tag?: string;
}

self.addEventListener('push', (event) => {
  const payload: PushPayload = event.data ? (event.data.json() as PushPayload) : {};
  event.waitUntil(
    self.registration.showNotification(payload.title ?? 'KI-Leitstand', {
      body: payload.body ?? 'Neuer dringender Fall im Postfach.',
      tag: payload.tag ?? 'inbox-urgent',
      icon: '/icons/icon-192.png',
      badge: '/icons/icon-192.png',
      data: { url: payload.url ?? '/' },
    }),
  );
});

self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  const target = new URL(
    (event.notification.data as { url?: string } | undefined)?.url ?? '/',
    self.location.origin,
  ).href;
  event.waitUntil(
    self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then(async (windows) => {
      const open = windows.find((client) => 'focus' in client);
      if (open) {
        await open.focus();
        return;
      }
      await self.clients.openWindow(target);
    }),
  );
});
