<script setup lang="ts">
import { computed } from 'vue';
import { useFeedStore } from '@/stores/feed';
import { useInboxStore } from '@/stores/inbox';
import { useUiStore, type MobileView } from '@/stores/ui';
import IconSvg from './IconSvg.vue';
import type { IconName } from './icons';

const ui = useUiStore();
const inbox = useInboxStore();
const feed = useFeedStore();

const TABS: { view: MobileView; label: string; icon: IconName }[] = [
  { view: 'start', label: 'Übersicht', icon: 'home' },
  { view: 'ask', label: 'Fragen', icon: 'chat' },
  { view: 'inbox', label: 'Postfach', icon: 'inbox' },
  { view: 'feed', label: 'Meldungen', icon: 'bell' },
  { view: 'reports', label: 'Reports', icon: 'chart' },
];

const badges = computed<Partial<Record<MobileView, number>>>(() => ({
  inbox: inbox.openCount,
  feed: feed.feed?.items.length ?? 0,
}));
</script>

<template>
  <nav id="bottom-nav" class="bottom-nav" aria-label="Hauptnavigation">
    <button
      v-for="tab in TABS"
      :key="tab.view"
      type="button"
      class="nav-btn"
      :aria-current="ui.mobileView === tab.view ? 'page' : undefined"
      @click="ui.setMobileView(tab.view)"
    >
      <span class="nav-icon">
        <IconSvg :name="tab.icon" :size="22" />
        <span
          v-if="badges[tab.view]"
          :class="['nav-badge', { 'is-urgent': tab.view === 'inbox' && inbox.hasUrgent }]"
          >{{ badges[tab.view] }}</span
        >
      </span>
      <span>{{ tab.label }}</span>
    </button>
  </nav>
</template>
