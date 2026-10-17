<script setup lang="ts">
import { useFeedStore } from '@/stores/feed';
import FeedList from './FeedList.vue';

const feed = useFeedStore();
</script>

<template>
  <div id="mview-feed" class="mview" aria-label="Meldungen">
    <div>
      <h2 class="m-page-title">Meldungen</h2>
      <div class="panel-sub">{{ feed.feed?.filterText ?? '' }}</div>
    </div>
    <FeedList variant="mobile" />
  </div>
</template>
