<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue';
import { useConversationStore } from '@/stores/conversation';
import { useFavoritesStore } from '@/stores/favorites';
import { useUiStore } from '@/stores/ui';
import { inertAttrs } from '@/utils/inert';
import FavoriteEditor from './FavoriteEditor.vue';
import IconSvg from './IconSvg.vue';

const store = useFavoritesStore();
const conversation = useConversationStore();
const ui = useUiStore();

/** Mobil: In einer leeren Sitzung zeigen die Vorschläge in der Mitte die Favoriten, die Leiste wäre doppelt. */
const sessionEmpty = computed(() => conversation.turns.length === 0 && !conversation.pending);

function chooseMobile(question: string): void {
  ui.setFavoritesOpen(false);
  void conversation.askFrom(question);
}

const list = ref<HTMLElement | null>(null);
/** Das Band bleibt einzeilig; ragt etwas über den Rand, zeigt ein Verlauf auf dieser Seite, dass es weitergeht. */
const overflowStart = ref(false);
const overflowEnd = ref(false);
let observer: ResizeObserver | undefined;

function measureOverflow(): void {
  const element = list.value;
  if (!element) return;
  const hidden = element.scrollWidth - element.clientWidth;
  overflowStart.value = hidden > 1 && element.scrollLeft > 1;
  overflowEnd.value = hidden > 1 && element.scrollLeft < hidden - 1;
}

/** Mausrad dreht auf dem Band in die Seitwärtsbewegung (sonst wäre der Überlauf nur per Tastatur erreichbar). */
function onWheel(event: WheelEvent): void {
  const element = list.value;
  if (!element || Math.abs(event.deltaY) <= Math.abs(event.deltaX)) return;
  if (element.scrollWidth <= element.clientWidth) return;
  element.scrollLeft += event.deltaY;
  event.preventDefault();
}

/** Die Liste erscheint erst, wenn die Favoriten geladen sind: Beobachter und erste Messung hängen an der Referenz. */
watch(
  list,
  (element) => {
    observer?.disconnect();
    if (!element) return;
    if (typeof ResizeObserver !== 'undefined') {
      observer = new ResizeObserver(measureOverflow);
      observer.observe(element);
    }
    measureOverflow();
    // Nach dem Laden der Schriften ändern sich die Chipbreiten, ohne dass sich die Liste selbst ändert.
    void document.fonts?.ready.then(measureOverflow);
  },
  { flush: 'post' },
);
onBeforeUnmount(() => observer?.disconnect());
watch(
  () => [store.favorites?.items.length, store.editing],
  async () => {
    await nextTick();
    measureOverflow();
  },
);
</script>

<template>
  <div id="fav-band" :class="['fav-band', { 'is-session-empty': sessionEmpty }]">
    <template v-if="store.favorites">
      <div class="fav-row">
        <span class="fav-star" title="Favoriten" role="img" aria-label="Favoriten">
          <IconSvg name="star" :size="18" fill="currentColor" :stroke-width="1.5" />
        </span>
        <ul
          ref="list"
          :class="['fav-list', { 'has-more-before': overflowStart, 'has-more-after': overflowEnd }]"
          aria-label="Favoriten"
          @scroll.passive="measureOverflow"
          @wheel="onWheel"
        >
          <li
            v-for="(item, index) in store.favorites.items"
            :key="item.question"
            :class="['fav-item', { 'is-editing': store.editing }]"
          >
            <button
              type="button"
              :class="['fav', { 'is-custom': !item.isDefault }]"
              :title="item.question"
              @click="conversation.askFrom(item.question)"
            >
              {{ item.shortTitle }}
            </button>
            <button
              v-if="store.editing"
              type="button"
              class="fav-remove"
              :aria-label="`Favorit ${index + 1} entfernen: ${item.shortTitle}`"
              @click="store.remove(index)"
            >
              <IconSvg name="close" :size="11" :stroke-width="3" />
            </button>
          </li>
        </ul>
        <span class="fav-count" :title="`Favoriten (höchstens ${store.favorites.max})`"
          >{{ store.favorites.items.length }} / {{ store.favorites.max }}</span
        >
        <button
          type="button"
          class="icon-btn fav-edit-btn"
          aria-label="Favoriten bearbeiten"
          :aria-expanded="store.editing"
          aria-controls="fav-editor"
          @click="store.setEditing(!store.editing)"
        >
          <IconSvg name="pencil" :size="16" />
        </button>
      </div>
      <!-- Nur mobil: aufklappbare Leiste über dem Eingabefeld (zugeklappt eine Zeile, aufgeklappt zwei Spalten). -->
      <div v-if="store.favorites.items.length > 0" class="fav-m short">
        <button
          type="button"
          class="fav-m-toggle"
          :aria-expanded="ui.favoritesOpen"
          aria-controls="fav-m-panel"
          @click="ui.setFavoritesOpen(!ui.favoritesOpen)"
        >
          <IconSvg name="star" :size="18" fill="currentColor" :stroke-width="1.5" />
          <span class="fav-m-label">Favoriten</span>
          <span class="fav-m-count">{{ store.favorites.items.length }}</span>
          <IconSvg class="fav-m-chevron" name="chevron" :size="18" :stroke-width="2.4" />
        </button>
        <div id="fav-m-panel" :class="['fav-m-collapse', { 'is-open': ui.favoritesOpen }]">
          <div class="fav-m-inner" v-bind="inertAttrs(!ui.favoritesOpen)">
            <ul class="fav-m-grid">
              <li v-for="item in store.favorites.items" :key="item.question">
                <button
                  type="button"
                  :class="['fav-m-item', { 'is-custom': !item.isDefault }]"
                  :title="item.question"
                  @click="chooseMobile(item.question)"
                >
                  {{ item.shortTitle }}
                </button>
              </li>
            </ul>
          </div>
        </div>
      </div>
      <!-- Der Editor bleibt im Baum und klappt per Zeilenhöhe auf, damit der Verlauf nicht springt. -->
      <div :class="['fav-editor-collapse', { 'is-open': store.editing }]">
        <div class="fav-editor-inner" v-bind="inertAttrs(!store.editing)">
          <FavoriteEditor :favorites="store.favorites" />
        </div>
      </div>
    </template>
  </div>
</template>
