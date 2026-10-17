<script setup lang="ts">
import type { MachineFinding, MachineRange } from '@/api/types';
import RatingMark from '@/components/RatingMark.vue';
import { useConversationStore } from '@/stores/conversation';

defineProps<{ findings: MachineFinding[]; emptyText: string; range: MachineRange }>();

const conversation = useConversationStore();
</script>

<template>
  <p v-if="findings.length === 0" class="empty-note">{{ emptyText }}</p>
  <ul v-else class="mc-findings">
    <li
      v-for="finding in findings"
      :key="finding.id"
      :class="['mc-finding', `is-${finding.rating}`]"
    >
      <div class="mc-finding-body">
        <RatingMark :rating="finding.rating" :show-word="false" />
        <div>
          <p class="mc-finding-title">
            {{ finding.title
            }}<span class="visually-hidden">
              ({{ finding.rating === 'crit' ? 'kritisch' : 'Warnung' }})</span
            >
          </p>
          <p class="mc-finding-text">{{ finding.text }}</p>
        </div>
      </div>
      <button
        type="button"
        class="btn btn-dark mc-analyze"
        :aria-label="`Mit Claude analysieren: ${finding.title}`"
        @click="
          conversation.askFrom(finding.question, {
            report: { kind: 'machines', range, findingId: finding.id },
          })
        "
      >
        Mit Claude analysieren
      </button>
    </li>
  </ul>
</template>
