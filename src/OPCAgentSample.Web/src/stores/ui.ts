// Darstellungszustand: geöffnetes Fenster, mobile Ansicht, MCP-Band, Reports, kurze Rückmeldungen.
import { defineStore } from 'pinia';
import { ref } from 'vue';
import { platform } from '@/platform';

export type DeskPanel = 'inbox' | 'feed' | 'user';
export type MobileView = 'start' | 'ask' | 'inbox' | 'feed' | 'reports';
export type ReportsTab = 'machines' | 'usage';

const TRACE_KEY = 'opc.trace';
const FAVORITES_OPEN_KEY = 'opc.favoritesOpen';
const TOAST_MS = 3000;
export const MOBILE_QUERY = '(max-width: 900px)';

export function isMobile(): boolean {
  return globalThis.matchMedia?.(MOBILE_QUERY).matches ?? false;
}

export const useUiStore = defineStore('ui', () => {
  /** Desktop: geöffnetes Fenster über der Statusleiste. */
  const panel = ref<DeskPanel | null>(null);
  /** Das Fenster wurde per Tastatur (Esc) geschlossen: dann ohne Bewegung, nur mit kurzem Ausblenden. */
  const panelClosedByKeyboard = ref(false);
  const mobileView = ref<MobileView>('start');
  /** Desktop: Reports statt Verlauf und MCP-Band zeigen. */
  const deskReports = ref(false);
  /** Reiter der Report-Seite: Maschinen (Standard) oder KI-Nutzung. */
  const reportsTab = ref<ReportsTab>('machines');
  const traceVisible = ref(true);
  const sessionsOpen = ref(false);
  /** Die Erklärseite („Über die Demo“) ist geöffnet; ohne Sitzung zeigt die Anmeldung sie ohnehin. */
  const aboutOpen = ref(false);
  /** Mobil: Seitenleiste mit Sitzungen und Benutzer (öffnet von links). */
  const sidebarOpen = ref(false);
  /** Mobil: Favoritenleiste über dem Eingabefeld ist aufgeklappt (wird gemerkt). */
  const favoritesOpen = ref(false);
  /** Das Eingabefeld hat den Fokus (mobil: Tastatur offen, Kopf und Navigation weichen). */
  const composerFocused = ref(false);
  const toastMessage = ref('');
  let toastTimer: ReturnType<typeof setTimeout> | undefined;

  async function init(): Promise<void> {
    traceVisible.value = (await platform.storage.get(TRACE_KEY)) !== '0';
    favoritesOpen.value = (await platform.storage.get(FAVORITES_OPEN_KEY)) === '1';
  }

  function togglePanel(name: DeskPanel): void {
    panelClosedByKeyboard.value = false;
    panel.value = panel.value === name ? null : name;
  }

  function closePanel(byKeyboard = false): void {
    panelClosedByKeyboard.value = byKeyboard;
    panel.value = null;
  }

  function setMobileView(view: MobileView): void {
    mobileView.value = view;
  }

  function toggleDeskReports(): void {
    deskReports.value = !deskReports.value;
    if (deskReports.value) panel.value = null;
  }

  function showAsk(): void {
    deskReports.value = false;
    mobileView.value = 'ask';
    panel.value = null;
  }

  function openAbout(): void {
    sidebarOpen.value = false;
    panel.value = null;
    aboutOpen.value = true;
  }

  function closeAbout(): void {
    aboutOpen.value = false;
  }

  function setTraceVisible(visible: boolean): void {
    traceVisible.value = visible;
    void platform.storage.set(TRACE_KEY, visible ? '1' : '0');
  }

  function setFavoritesOpen(open: boolean): void {
    favoritesOpen.value = open;
    void platform.storage.set(FAVORITES_OPEN_KEY, open ? '1' : '0');
  }

  /** Kurze Rückmeldung (Statuszeile, wird vorgelesen). */
  function toast(message: string): void {
    toastMessage.value = message;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => {
      toastMessage.value = '';
    }, TOAST_MS);
  }

  return {
    panel,
    panelClosedByKeyboard,
    mobileView,
    deskReports,
    reportsTab,
    traceVisible,
    sessionsOpen,
    aboutOpen,
    sidebarOpen,
    favoritesOpen,
    composerFocused,
    toastMessage,
    init,
    togglePanel,
    closePanel,
    setMobileView,
    toggleDeskReports,
    showAsk,
    openAbout,
    closeAbout,
    setTraceVisible,
    setFavoritesOpen,
    toast,
  };
});
