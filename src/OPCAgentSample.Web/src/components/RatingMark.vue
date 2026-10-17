<script setup lang="ts">
import type { Rating } from '@/api/types';
import { RATING_WORDS } from '@/utils/machineFormat';

/** Bewertung als Form (Kreis, Dreieck, Raute) plus Wort: Die Farbe allein trägt nie die Aussage. */
withDefaults(defineProps<{ rating: Rating; showWord?: boolean }>(), { showWord: true });
</script>

<template>
  <span :class="['mc-rating', `is-${rating}`]">
    <span class="mc-mark" aria-hidden="true"></span>
    <span :class="{ 'visually-hidden': !showWord }">{{ RATING_WORDS[rating] }}</span>
  </span>
</template>
