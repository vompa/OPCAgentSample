// Meldungen (GET /api/feed, Live-Ereignis feed). Neueste zuerst.
import { defineStore } from 'pinia';
import { ref } from 'vue';
import type { FeedEntry, FeedResponse } from '@/api/types';

const FRESH_MS = 4000;

function feedKey(entry: FeedEntry): string {
  return `${entry.time}|${entry.source}|${entry.text}`;
}

export const useFeedStore = defineStore('feed', () => {
  const feed = ref<FeedResponse | null>(null);

  /** Fügt eine neue Meldung vorn ein; bereits bekannte (z. B. durch den Abgleich beim Start) entfallen. */
  function addLive(entry: FeedEntry): boolean {
    if (!feed.value) return false;
    if (feed.value.items.some((item) => feedKey(item) === feedKey(entry))) return false;

    const fresh: FeedEntry = { ...entry, fresh: true };
    feed.value = { ...feed.value, items: [fresh, ...feed.value.items] };
    setTimeout(() => {
      if (!feed.value) return;
      feed.value = {
        ...feed.value,
        items: feed.value.items.map((item) => (item === fresh ? { ...item, fresh: false } : item)),
      };
    }, FRESH_MS);
    return true;
  }

  return { feed, addLive };
});
