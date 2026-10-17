<script setup lang="ts">
import { useFeedStore } from '@/stores/feed';

defineProps<{ variant: 'desk' | 'mobile' }>();

const store = useFeedStore();

/** Schweregrad als Wort: Farbe allein trägt die Bedeutung nicht (WCAG 1.4.1). */
const LEVEL_TEXT = { info: 'Info', ok: 'OK', warn: 'Warnung', crit: 'Kritisch' } as const;
</script>

<template>
  <p v-if="(store.feed?.items ?? []).length === 0" class="empty-note">Keine Meldungen.</p>
  <ol v-else :class="variant === 'desk' ? 'feed-list' : 'm-feed-list'">
    <li
      v-for="entry in store.feed?.items"
      :key="`${entry.time}|${entry.source}|${entry.text}`"
      :class="[variant === 'mobile' ? 'm-feed-item' : null, { 'is-new': entry.fresh }]"
    >
      <span class="feed-time">{{ entry.time }}</span>
      <span :class="['feed-dot', `feed-dot-${entry.level}`]" aria-hidden="true"></span>
      <span :class="variant === 'mobile' ? 'feed-level' : 'visually-hidden'"
        >{{ LEVEL_TEXT[entry.level] }}{{ variant === 'mobile' ? '' : ':' }}</span
      >
      <div class="feed-body">
        <span class="feed-source">{{ entry.source }}</span>
        <span class="feed-text">{{ entry.text }}</span>
      </div>
    </li>
  </ol>
</template>
