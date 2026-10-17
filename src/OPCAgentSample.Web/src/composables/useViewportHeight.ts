// Mobil: Die App-Höhe folgt dem sichtbaren Bereich (visualViewport). Bei geöffneter Tastatur schrumpft die Seite, statt
// dass der Browser sie verschiebt (iOS verkleinert den Layout-Viewport nicht, Android je nach Einstellung).
import { onBeforeUnmount, onMounted } from 'vue';
import { isMobile } from '@/stores/ui';

const PROPERTY = '--app-h';
/** Ab dieser Vergrößerung (Pinch-Zoom) gilt der Viewport nicht mehr als Bildschirmgröße. */
const ZOOM_TOLERANCE = 1.01;

export function useViewportHeight(): void {
  let viewport: VisualViewport | undefined;

  function apply(): void {
    const root = document.documentElement;
    if (!viewport || !isMobile() || viewport.scale > ZOOM_TOLERANCE) {
      root.style.removeProperty(PROPERTY);
      return;
    }
    root.style.setProperty(PROPERTY, `${Math.round(viewport.height)}px`);
    // iOS schiebt die Seite beim Fokussieren nach oben; der Kopf der Anwendung soll nicht aus dem Bild laufen.
    if (viewport.offsetTop > 0) window.scrollTo(0, 0);
  }

  onMounted(() => {
    viewport = window.visualViewport ?? undefined;
    viewport?.addEventListener('resize', apply);
    viewport?.addEventListener('scroll', apply);
    apply();
  });
  onBeforeUnmount(() => {
    viewport?.removeEventListener('resize', apply);
    viewport?.removeEventListener('scroll', apply);
    document.documentElement.style.removeProperty(PROPERTY);
  });
}
