<script setup lang="ts">
import { computed, ref } from 'vue';
import { useConversationStore } from '@/stores/conversation';
import { useUiStore } from '@/stores/ui';
import { callSourceText, formatParameters, totalDuration, truncate } from '@/utils/format';
import type { ToolCall } from '@/api/types';
import { inertAttrs } from '@/utils/inert';
import IconSvg from './IconSvg.vue';

const MAX_BLOCKS = 8;
/** Höchstens so viele neue Aufrufe laufen gestaffelt ein (je 40 ms Versatz), der Rest gleichzeitig. */
const MAX_STAGGER = 5;

interface Entry {
  id: string;
  question: string;
  toolCalls: ToolCall[];
  live: boolean;
}

const conversation = useConversationStore();
const ui = useUiStore();

/** Von Hand auf- oder zugeklappte Blöcke; ohne Eintrag ist nur die aktuelle Frage offen. */
const manual = ref<Record<string, boolean>>({});

const entries = computed<Entry[]>(() => {
  const list: Entry[] = conversation.turns.map((turn) => ({
    id: `turn-${turn.sequence}`,
    question: turn.question,
    toolCalls: turn.toolCalls,
    live: false,
  }));
  const pending = conversation.pending;
  if (pending) {
    list.push({
      id: 'pending',
      question: pending.question,
      toolCalls: pending.toolCalls,
      live: !pending.error,
    });
  }
  return list;
});
const total = computed(() => entries.value.reduce((sum, entry) => sum + entry.toolCalls.length, 0));
const shown = computed(() => entries.value.slice(-MAX_BLOCKS).reverse());

function isOpen(entry: Entry, index: number): boolean {
  return manual.value[entry.id] ?? index === 0;
}

function toggle(entry: Entry, index: number): void {
  manual.value = { ...manual.value, [entry.id]: !isOpen(entry, index) };
}

function summaryOf(entry: Entry): string {
  if (entry.toolCalls.length === 0)
    return entry.live ? 'Claude wählt die Tools …' : 'Keine MCP-Aufrufe';
  return `${entry.toolCalls.length} MCP-Aufrufe · ${totalDuration(entry.toolCalls)} ms`;
}

function staggerOf(callIndex: number): string {
  return `${Math.min(callIndex, MAX_STAGGER) * 40}ms`;
}
</script>

<template>
  <aside id="trace" class="col-right" aria-label="MCP-Aufrufe" :hidden="!ui.traceVisible">
    <template v-if="ui.traceVisible">
      <div class="trace-scroll">
        <div class="trace-head">
          <h2 class="trace-title">MCP-Aufrufe</h2>
          <span class="trace-total">{{ total }} Aufrufe in dieser Sitzung</span>
        </div>
        <TransitionGroup tag="div" name="trace-block" class="trace-blocks">
          <section
            v-for="(entry, index) in shown"
            :key="entry.id"
            :class="[
              'trace-block',
              { 'is-newest': index === 0, 'is-collapsed': !isOpen(entry, index) },
            ]"
            :aria-label="`Aufrufe zu: ${truncate(entry.question, 40)}`"
          >
            <button
              type="button"
              class="trace-toggle"
              :aria-expanded="isOpen(entry, index)"
              :aria-controls="`trace-calls-${entry.id}`"
              :disabled="entry.toolCalls.length === 0"
              @click="toggle(entry, index)"
            >
              <IconSvg name="chevron" :size="12" :stroke-width="3" />
              <span class="trace-q">❯ {{ truncate(entry.question, 70) }}</span>
            </button>
            <div class="trace-sum">{{ summaryOf(entry) }}</div>
            <div :class="['trace-collapse', { 'is-open': isOpen(entry, index) }]">
              <TransitionGroup
                :id="`trace-calls-${entry.id}`"
                tag="ol"
                name="trace-call"
                class="trace-calls"
                v-bind="inertAttrs(!isOpen(entry, index))"
              >
                <li
                  v-for="(call, callIndex) in entry.toolCalls"
                  :key="callIndex"
                  :class="['trace-call', { 'is-denied': call.outcome === 'denied' }]"
                  :style="{ '--stagger': staggerOf(callIndex) }"
                >
                  <div class="trace-call-top">
                    <span class="trace-tool">{{ call.tool }}</span>
                    <span class="trace-ms">{{
                      call.durationMs == null ? '–' : `${call.durationMs} ms`
                    }}</span>
                  </div>
                  <span class="trace-args">{{ formatParameters(call.parameters) }}</span>
                  <span class="trace-src">{{ callSourceText(call) }}</span>
                </li>
              </TransitionGroup>
            </div>
          </section>
        </TransitionGroup>
        <p v-if="shown.length === 0" class="trace-sum">Noch keine Aufrufe in dieser Sitzung.</p>
      </div>
      <p class="trace-foot">
        Kennzahlen und Ampeln berechnet der MCP-Server deterministisch. Claude wählt die Tools,
        verknüpft und formuliert. Keine Steuerbefehle an Maschinen.
      </p>
    </template>
  </aside>
</template>
