// Reaktive Variante von `isMobile()`: folgt dem Wechsel der Fenstergröße und der Ausrichtung.
import { onBeforeUnmount, onMounted, ref, type Ref } from 'vue';
import { MOBILE_QUERY, isMobile } from '@/stores/ui';

export function useIsMobile(): Ref<boolean> {
  const mobile = ref(isMobile());
  let query: MediaQueryList | undefined;
  const update = (): void => {
    mobile.value = isMobile();
  };
  onMounted(() => {
    query = globalThis.matchMedia?.(MOBILE_QUERY);
    query?.addEventListener?.('change', update);
    update();
  });
  onBeforeUnmount(() => query?.removeEventListener?.('change', update));
  return mobile;
}
