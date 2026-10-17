<script setup lang="ts">
import { computed, ref } from 'vue';
import { useConversationStore, type Turn } from '@/stores/conversation';
import { formatParameters, timeText, totalDuration } from '@/utils/format';
import AnswerChart from './AnswerChart.vue';
import AnswerTable from './AnswerTable.vue';
import KeepIds from './KeepIds.vue';

const props = defineProps<{ turn: Turn }>();
const conversation = useConversationStore();

const toolsOpen = ref(false);
const card = computed(() => props.turn.answerCard);
const duration = computed(() => totalDuration(props.turn.toolCalls));
const sources = computed(() =>
  card.value.sources.length > 0 ? ` · Quellen: ${card.value.sources.join(', ')}` : '',
);
</script>

<template>
  <article class="card" aria-label="Antwort von Claude">
    <div class="card-meta">
      <span :class="card.notice ? 'chip chip-notice' : 'chip chip-claude'">
        {{ turn.preview && turn.local ? 'Vorschau' : card.notice ? 'Vorbereitet' : 'Claude' }}
      </span>
      <span v-if="turn.repeat" class="chip chip-repeat"
        >Wiederholung · gleiche Antwort wie eben</span
      >
      <span v-if="card.prompt" class="chip chip-mono">Prompt: {{ card.prompt }}</span>
      <span v-if="card.form" class="chip">{{ card.form }}</span>
      <span class="card-meta-right"
        >{{ turn.toolCalls.length }} MCP-Aufrufe · {{ duration }} ms</span
      >
    </div>
    <p v-if="card.notice" class="card-notice" role="note">{{ card.notice }}</p>
    <p class="card-headline"><KeepIds :text="card.headline" /></p>
    <div v-if="card.todo.length > 0" class="todo">
      <span class="todo-label">Was tun?</span>
      <span v-for="(item, index) in card.todo" :key="index" class="todo-text"
        ><KeepIds :text="item"
      /></span>
    </div>
    <div v-if="card.table.rows.length > 0" class="values-label">Die Werte</div>
    <AnswerTable :table="card.table" />
    <AnswerChart v-if="card.chart" :chart="card.chart" />
    <div v-if="card.why.length > 0" class="why">
      <span class="why-label">Warum</span>
      <p v-for="(item, index) in card.why" :key="index"><KeepIds :text="item" /></p>
    </div>
    <template v-if="turn.toolCalls.length > 0">
      <button
        type="button"
        class="tools-toggle"
        :aria-expanded="toolsOpen"
        @click="toolsOpen = !toolsOpen"
      >
        <span class="mono">{{ turn.toolCalls.length }} Tools · {{ duration }} ms</span>
        <span>{{ toolsOpen ? 'Ausblenden' : 'Tool-Aufrufe' }}</span>
      </button>
      <ol v-show="toolsOpen" class="tools-list">
        <li v-for="(call, index) in turn.toolCalls" :key="index" class="trace-call">
          <span class="trace-tool">
            {{ call.tool }} · {{ call.durationMs == null ? '–' : `${call.durationMs} ms` }}
          </span>
          <span class="trace-args">{{ formatParameters(call.parameters) }}</span>
        </li>
      </ol>
    </template>
    <footer class="card-foot">
      <span class="foot-text">Datenstand {{ card.dataAsOf }}{{ sources }}</span>
      <span v-if="turn.restored || turn.repeat" class="stale">
        <span v-if="turn.restored" class="stale-chip">Stand {{ timeText(turn.dataAsOfUtc) }}</span>
        <button
          type="button"
          class="requery-btn"
          :disabled="conversation.busy"
          :aria-label="`Neu fragen: ${turn.question}`"
          @click="conversation.ask(turn.question, { fresh: true, report: turn.report })"
        >
          Neu fragen
        </button>
      </span>
    </footer>
  </article>
</template>
