<script setup lang="ts">
import { useInboxStore } from '@/stores/inbox';
import InboxList from './InboxList.vue';

const inbox = useInboxStore();
</script>

<template>
  <div id="mview-inbox" class="mview" aria-label="Postfach">
    <h2 class="m-page-title">Postfach · {{ inbox.openCount }} offen</h2>
    <InboxList variant="mobile" />
  </div>
</template>
