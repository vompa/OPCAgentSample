<script setup lang="ts">
import { computed, ref } from 'vue';
import type { FavoritesResponse } from '@/api/types';
import { useFavoritesStore } from '@/stores/favorites';

const props = defineProps<{ favorites: FavoritesResponse }>();
const store = useFavoritesStore();

const shortTitle = ref('');
const question = ref('');

const full = computed(() => props.favorites.items.length >= props.favorites.max);
const missingDefaults = computed(() =>
  props.favorites.defaults.filter(
    (d) => !props.favorites.items.some((i) => i.question === d.question),
  ),
);
const limitText = computed(() =>
  full.value
    ? `Alle ${props.favorites.max} Favoritenplätze belegt. Entferne einen, um Platz zu schaffen.`
    : `Noch ${props.favorites.max - props.favorites.items.length} von ${props.favorites.max} Plätzen frei. Der Kurztitel erscheint als Schaltfläche, die Frage wird beim Klick gestellt.`,
);

function submit(): void {
  if (store.add(shortTitle.value, question.value)) {
    shortTitle.value = '';
    question.value = '';
  }
}
</script>

<template>
  <form id="fav-editor" class="fav-editor" @submit.prevent="submit">
    <div class="fav-editor-row">
      <label class="visually-hidden" for="new-short">Kurztitel</label>
      <input
        id="new-short"
        v-model="shortTitle"
        class="text-input short-input"
        type="text"
        :maxlength="favorites.maxShortTitleLength"
        placeholder="Kurzname"
      />
      <label class="visually-hidden" for="new-question">Frage</label>
      <input
        id="new-question"
        v-model="question"
        class="text-input question-input"
        type="text"
        maxlength="500"
        placeholder="Frage, z. B. Wie lange reicht das Gewebe an RT-02?"
      />
      <button type="submit" class="btn btn-dark" :disabled="full">Hinzufügen</button>
    </div>
    <div class="fav-limit">{{ limitText }}</div>
    <div class="fav-suggest">
      <span v-if="missingDefaults.length > 0">Standard-Favoriten:</span>
      <button
        v-for="d in missingDefaults"
        :key="d.question"
        type="button"
        class="suggest-btn"
        :disabled="full"
        @click="store.add(d.shortTitle, d.question)"
      >
        + {{ d.shortTitle }}
      </button>
      <button type="button" class="suggest-btn" @click="store.reset()">
        Alle Standard-Favoriten zurückholen
      </button>
    </div>
  </form>
</template>
