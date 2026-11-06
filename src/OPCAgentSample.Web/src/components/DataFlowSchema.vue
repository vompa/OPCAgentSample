<script setup lang="ts">
import { ref } from 'vue';
import IconSvg from './IconSvg.vue';

/** Schema der Erklärseite: Halle, Server und Client mit laufenden Datenleitungen. Die Zustände sind Beispiele, keine Live-Daten. */
type StationState = 'ok' | 'warn' | 'crit';

interface Station {
  name: string;
  state: StationState;
}

const STATE_LABEL: Record<StationState, string> = {
  ok: 'läuft',
  warn: 'Warnung',
  crit: 'Störung',
};

const STATIONS: Station[] = [
  { name: 'RT-01', state: 'ok' },
  { name: 'RT-02', state: 'ok' },
  { name: 'RT-03', state: 'ok' },
  { name: 'RT-04', state: 'warn' },
  { name: 'OF-01', state: 'ok' },
  { name: 'OF-02', state: 'ok' },
  { name: 'VP-01', state: 'ok' },
  { name: 'VP-02', state: 'crit' },
];

const paused = ref(false);

function togglePause(): void {
  paused.value = !paused.value;
}
</script>

<template>
  <figure :class="['flow-schema', { 'is-paused': paused }]">
    <div class="flow-grid">
      <section class="flow-zone flow-hall" aria-labelledby="flow-hall-title">
        <span class="flow-zone-tag">Maschinennetz · OT</span>
        <h3 id="flow-hall-title">Halle A</h3>
        <ul class="flow-stations">
          <li
            v-for="station in STATIONS"
            :key="station.name"
            :class="['flow-station', `is-${station.state}`]"
          >
            <span class="flow-station-name">{{ station.name }}</span>
            <span class="flow-dot" aria-hidden="true"></span>
            <span class="visually-hidden">{{ STATE_LABEL[station.state] }}</span>
          </li>
        </ul>
        <p class="flow-zone-note">Live-Werte, Alarme, Historie</p>
        <p class="flow-firewall">
          <IconSvg name="shield" :size="16" />
          <span
            ><strong>Firewall</strong> Von außen nicht erreichbar. Offen ist nur OPC UA auf Port
            4840, und nur für den Collector.</span
          >
        </p>
      </section>

      <!-- Einzige Leitung aus dem Hallennetz; der Balken steht für die Firewall. -->
      <span class="flow-gate" aria-hidden="true">
        <span class="flow-wire is-across is-data"></span>
        <span class="flow-wall"></span>
      </span>

      <section class="flow-zone flow-server" aria-labelledby="flow-server-title">
        <span class="flow-zone-tag">.NET · :5145</span>
        <h3 id="flow-server-title">Server</h3>
        <div class="flow-parts">
          <!-- Reihenfolge im DOM = Weg der Daten; die U-Form (Collector oben links, Claude oben rechts) setzt about.css. -->
          <p class="flow-part is-collector"><strong>Collector</strong> Subscription</p>
          <span class="flow-wire is-down is-data flow-link-store" aria-hidden="true"></span>
          <p class="flow-part is-redis"><strong>Redis</strong> Zustand, Verlauf</p>
          <span class="flow-wire is-right is-data flow-link-tools" aria-hidden="true"></span>
          <p class="flow-part is-mcp is-ai"><strong>MCP-Server</strong> 17 Tools, lesend</p>
          <div class="flow-link-model">
            <span class="flow-wire is-up is-ask" aria-hidden="true"></span>
            <p class="flow-optional"><strong>Anonymisierung</strong> ergänzbar</p>
            <span class="flow-wire is-up is-ask" aria-hidden="true"></span>
          </div>
          <p class="flow-part is-claude is-ai"><strong>Claude</strong> Anthropic-API</p>
        </div>
      </section>

      <span class="flow-wire is-across is-ask" aria-hidden="true"></span>

      <section class="flow-zone flow-client" aria-labelledby="flow-client-title">
        <span class="flow-zone-tag">Vue-PWA</span>
        <h3 id="flow-client-title">Client</h3>
        <div class="flow-phone">
          <p class="flow-bubble flow-question">
            Warum steht <span class="flow-nowrap">VP-02</span>?
          </p>
          <p class="flow-bubble flow-answer">Folie leer, seit 18 min</p>
        </div>
      </section>
    </div>

    <figcaption class="flow-caption">
      <span
        >Das Maschinennetz einer echten Halle ist von außen nicht erreichbar; die Firewall lässt nur
        den Collector auf einem Port durch, und er liest nur. In der Demo ersetzt ein Simulator die
        Halle, er läuft ohne Zugang von außen. Der Collector legt die Werte in Redis ab. Fragt
        jemand im Client, wählt Claude die passenden MCP-Tools, und die Antwort kommt mit Quellen
        zurück. Die Tool-Ergebnisse gehen heute unverändert an die Anthropic-API; eine
        Anonymisierung davor, die der MCP-Server nach der Antwort wieder auflöst, lässt sich
        ergänzen.</span
      >
      <button
        type="button"
        class="about-btn about-btn-ghost flow-pause"
        :aria-pressed="paused"
        @click="togglePause"
      >
        {{ paused ? 'Animation fortsetzen' : 'Animation anhalten' }}
      </button>
    </figcaption>
  </figure>
</template>
