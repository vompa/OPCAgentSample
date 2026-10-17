<script setup lang="ts">
import { computed } from 'vue';
import { useIdentityStore } from '@/stores/identity';
import { useInboxStore } from '@/stores/inbox';
import { useLiveStore } from '@/stores/live';
import { useUiStore } from '@/stores/ui';
import IconSvg from './IconSvg.vue';

const identity = useIdentityStore();
const inbox = useInboxStore();
const live = useLiveStore();
const ui = useUiStore();

const inboxClass = computed(() => {
  if (inbox.openCount === 0) return '';
  return inbox.hasUrgent ? 'is-inbox-urgent' : 'is-inbox-soon';
});
const feedLabel = computed(() => {
  const alarms = live.alarms?.count ?? 0;
  return alarms > 0
    ? `Aktuelle Meldungen anzeigen, ${alarms} aktive Alarme`
    : 'Aktuelle Meldungen anzeigen';
});
const latest = computed(() => identity.me?.counters.latestFeed ?? null);

/** Hinweis im Platzhalter der Statusleiste (verschiebt keinen anderen Eintrag): Verbindung, Kaltstart oder Zeitbasis. */
const hint = computed(() => {
  if (live.connection === 'lost') return { text: 'Live getrennt · verbindet neu …', title: '' };
  if (live.connection === 'paused') {
    return {
      text: 'Live pausiert · Eingabe verbindet neu',
      title: 'Live-Strom bei Inaktivität pausiert',
    };
  }
  const status = identity.me?.status;
  if (status?.historyLoading) {
    return { text: 'Historie wird geladen …', title: 'Kennzahlen sind bis dahin unvollständig' };
  }
  if (status?.dataWarning) return { text: 'Hinweis zur Zeitbasis', title: status.dataWarning };
  return null;
});
</script>

<template>
  <div id="statusbar" class="statusbar" aria-label="Statusleiste">
    <template v-if="identity.me">
      <span :class="['statusbar-item', 'is-opc', { 'is-lost': !identity.me.status.opcConnected }]"
        ><span class="dot"></span
        >{{ identity.me.status.opcConnected ? 'OPC UA verbunden' : 'OPC UA getrennt' }}</span
      >
      <button
        type="button"
        class="statusbar-item"
        :aria-pressed="ui.traceVisible"
        :aria-label="`MCP-Band anzeigen, ${identity.me.status.mcpToolCount} Tools`"
        title="MCP-Band ein- oder ausblenden"
        data-focus-key="sb-trace"
        @click="ui.setTraceVisible(!ui.traceVisible)"
      >
        <IconSvg name="mcp" :size="14" />MCP · {{ identity.me.status.mcpToolCount }} Tools
      </button>
      <span class="statusbar-item is-scope" :title="identity.me.status.scopeText">{{
        identity.me.status.scopeText
      }}</span>
      <span class="statusbar-item is-mono">{{ identity.me.clock.localTime }}</span>
      <span class="statusbar-spacer">
        <!-- Hinweis liegt im Platzhalter: Andere Einträge verschieben sich dadurch nie. -->
        <span v-if="hint" class="live-hint" role="status" :title="hint.title || undefined">
          <span class="live-dot" aria-hidden="true"></span>{{ hint.text }}
        </span>
        <span v-else class="live-hint" hidden></span>
      </span>
      <button
        type="button"
        :class="['statusbar-item', 'is-button', { 'is-active': ui.deskReports }]"
        :aria-label="ui.deskReports ? 'Zurück zum Leitstand' : 'Reports anzeigen'"
        data-focus-key="sb-reports"
        @click="ui.toggleDeskReports()"
      >
        <IconSvg name="chart" :size="14" /><span>{{
          ui.deskReports ? 'Zurück zum Leitstand' : 'Reports'
        }}</span>
      </button>
      <button
        type="button"
        class="statusbar-item is-button is-feed"
        :aria-label="feedLabel"
        :aria-expanded="ui.panel === 'feed'"
        aria-controls="desk-panel"
        data-focus-key="sb-feed"
        @click="ui.togglePanel('feed')"
      >
        <IconSvg name="bell" :size="14" /><strong>{{ identity.me.counters.feedCount }}</strong
        ><span v-if="latest" class="feed-label">Meldungen</span>
        <span
          v-if="latest"
          :class="['feed-dot', `feed-dot-${latest.level}`]"
          aria-hidden="true"
        ></span>
        <span class="latest-text">{{
          latest ? `${latest.time}  ${latest.source}: ${latest.text}` : 'Keine Meldungen'
        }}</span>
      </button>
      <button
        type="button"
        :class="['statusbar-item', 'is-button', inboxClass]"
        aria-label="Postfach öffnen"
        :aria-expanded="ui.panel === 'inbox'"
        aria-controls="desk-panel"
        data-focus-key="sb-inbox"
        @click="ui.togglePanel('inbox')"
      >
        <IconSvg name="inbox" :size="14" /><span>Postfach</span
        ><strong>{{ inbox.openCount }} offen</strong>
      </button>
      <button
        type="button"
        class="statusbar-item is-button"
        aria-label="Benutzer anzeigen"
        :aria-expanded="ui.panel === 'user'"
        aria-controls="desk-panel"
        data-focus-key="sb-user"
        @click="ui.togglePanel('user')"
      >
        <span class="avatar-small" aria-hidden="true">{{ identity.me.user.initials }}</span>
        <span>{{ identity.me.user.displayName }} · {{ identity.me.role.label }}</span>
      </button>
    </template>
  </div>
</template>
