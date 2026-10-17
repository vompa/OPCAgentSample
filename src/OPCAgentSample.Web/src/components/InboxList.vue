<script setup lang="ts">
import { useConversationStore } from '@/stores/conversation';
import { useInboxStore } from '@/stores/inbox';
import IconSvg from './IconSvg.vue';

defineProps<{ variant: 'desk' | 'mobile' }>();

const inbox = useInboxStore();
const conversation = useConversationStore();

const BADGE = {
  crit: 'badge-crit',
  warn: 'badge-warn',
  info: 'badge-info',
  ok: 'badge-ok',
} as const;
</script>

<template>
  <p v-if="inbox.items.length === 0" class="empty-note">Das Postfach ist leer.</p>
  <ul v-else class="inbox-list">
    <li
      v-for="item in inbox.items"
      :key="item.id"
      :class="['inbox-item', { 'is-taken': item.takenBy != null }]"
    >
      <div class="inbox-item-top">
        <span :class="['badge', BADGE[item.level]]">{{ item.priorityText }}</span>
        <span class="inbox-due">{{ variant === 'desk' ? `Frist: ${item.due}` : item.due }}</span>
      </div>
      <div class="inbox-title">{{ item.title }}</div>
      <div class="inbox-detail">{{ item.detail }}</div>
      <div v-if="item.takenBy != null" class="inbox-taken">
        <IconSvg v-if="variant === 'desk'" name="check" :size="16" :stroke-width="2.5" />
        <span v-if="variant === 'desk'"
          >Übernommen von {{ item.takenBy }} · {{ item.takenAtText }}</span
        >
        <template v-else>Übernommen von {{ item.takenBy }} · {{ item.takenAtText }}</template>
      </div>
      <div :class="variant === 'desk' ? 'inbox-actions' : 'm-inbox-actions'">
        <button
          v-if="item.question"
          type="button"
          :class="variant === 'desk' ? 'btn btn-dark' : 'm-btn'"
          @click="conversation.askFrom(item.question)"
        >
          {{ variant === 'desk' ? 'Mit Claude analysieren' : 'Analysieren' }}
        </button>
        <button
          v-if="item.takenBy == null"
          type="button"
          :class="variant === 'desk' ? 'btn' : 'm-btn m-btn-light'"
          @click="inbox.take(item.id)"
        >
          Übernehmen
        </button>
      </div>
    </li>
  </ul>
</template>
