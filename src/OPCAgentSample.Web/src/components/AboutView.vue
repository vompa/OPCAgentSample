<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted } from 'vue';
import { useAuthStore } from '@/stores/auth';
import { useUiStore } from '@/stores/ui';
import AccessForm from './AccessForm.vue';
import IconSvg from './IconSvg.vue';

const REPO_URL = 'https://github.com/vompa/OPCAgentSample';
const IMPRINT_URL = 'https://vompa.dev/impressum.html';
const PRIVACY_URL = 'https://vompa.dev/datenschutz.html';
/** Bilder liegen in public/img (je als WebP mit PNG-Rückfall); als Bindung, damit der Build sie unverändert lässt. */
function image(name: string, extension: 'webp' | 'png'): string {
  return `/img/${name}.${extension}`;
}

const PAGE_TITLE = 'Schleifwerk · KI-Leitstand: Frag deine Halle';

interface FlowStep {
  tag: string;
  title: string;
  text: string;
}

interface Role {
  name: string;
  scope: string;
  questions: string[];
}

interface Limit {
  label: string;
  value: string;
  text: string;
  tone?: 'dark' | 'wide';
}

const HALL_FACTS = [
  { value: '4', label: 'Rundtisch-Pressen' },
  { value: '2', label: 'Aushärteöfen' },
  { value: '2', label: 'Verpackstellen' },
  { value: '3', label: 'Schichten am Tag' },
  { value: '17', label: 'MCP-Tools, nur lesend' },
];

const FLOW: FlowStep[] = [
  {
    tag: 'OPC UA',
    title: 'Die Halle',
    text: 'Der Simulator liefert Live-Werte und Alarme. Die Historie ab 05.10.2026 kommt per HistoryRead.',
  },
  {
    tag: 'Collector',
    title: 'Liest mit',
    text: 'Folgt den Werten per Subscription und lädt nach einem Ausfall die Lücke nach.',
  },
  {
    tag: 'Redis',
    title: 'Speichert',
    text: 'Redis hält den aktuellen Zustand und die verdichteten Zeitbereiche. Ein Langzeitspeicher (z. B. PostgreSQL) lässt sich leicht ergänzen.',
  },
  {
    tag: 'MCP-Server',
    title: 'Rechnet',
    text: '17 Tools, nur lesend. Kennzahlen und Ampeln rechnet der Server deterministisch.',
  },
  {
    tag: 'Claude Haiku',
    title: 'Erklärt',
    text: 'Wählt Tools, verknüpft Ergebnisse und formuliert die Antwort.',
  },
  {
    tag: 'Antwortkarte',
    title: 'Zeigt',
    text: 'Antwort mit Quellen und Datenstand, daneben die Tool-Spur jedes Aufrufs.',
  },
];

const ROLES: Role[] = [
  {
    name: 'Schichtleiter',
    scope: 'Halle A, 8 Stationen',
    questions: [
      'Was muss ich von der Nachtschicht wissen?',
      'Wie ist die Lage in der Halle gerade?',
      'Wie lange reicht das Material an allen Stationen?',
      'Schaffen wir das Schichtziel?',
      'Welche Störungen und Alarme gab es in dieser Schicht?',
    ],
  },
  {
    name: 'Hallenleiterin',
    scope: 'Halle A mit Wartungsplanung',
    questions: [
      'Bereite die Tagesbesprechung vor.',
      'Wie läuft die Halle heute?',
      'Wo verlieren wir diese Woche am meisten Zeit?',
      'Wie ist die Qualitätslage in der Halle?',
      'Welche Wartungen stehen heute und morgen an?',
    ],
  },
  {
    name: 'Produktionsleiter',
    scope: 'Werk mit den Hallen A bis C',
    questions: [
      'Fass mir die Woche zusammen.',
      'Welche Schicht hat diese Woche die schlechteste OEE und warum?',
      'Welche Aufträge sind diese Woche gefährdet?',
      'Wie entwickelt sich der Anteil ungeplanter Stillstände?',
      'Was haben uns Ausschuss und Stillstände diese Woche gekostet?',
    ],
  },
];

const FREE_QUESTIONS = [
  'Warum hängt RT-04 hinterher?',
  'Warum steht VP-02?',
  'Sind die Öfen heute der Engpass?',
  'Können wir die Formwartung an RT-02 mit dem Rüsten bündeln?',
  'Lohnt sich eine Überholung von RT-03?',
];

const LIMITS: Limit[] = [
  {
    label: 'Demo-Uhr',
    value: 'Do 15.10.2026, 09:42',
    text: 'Die Simulationsuhr steht fest auf KW 42. Jeder Start beginnt am selben Punkt.',
    tone: 'dark',
  },
  {
    label: 'Modell',
    value: 'Claude Haiku',
    text: 'Ein echtes Modell antwortet. Wortlaut und Tool-Weg können abweichen.',
  },
  {
    label: 'Kontingent',
    value: '20 Fragen am Tag',
    text: 'Je Besucher, insgesamt 150 am Tag. Jede Frage ruft die kostenpflichtige Claude-API auf.',
  },
  {
    label: 'Zugriff',
    value: 'Nur lesend',
    text: 'Kein Start, kein Stopp, keine Sollwerte.',
  },
  {
    label: 'Daten',
    value: 'Flüchtig',
    text: 'Sie entstehen bei jedem Start neu. Verlauf, Favoriten und Kontingentzähler gehen dabei verloren.',
  },
  {
    label: 'Betrieb',
    value: 'Scale-to-zero',
    text: 'Ohne Anfrage schaltet sich die Demo nach etwa fünf Minuten ab. Der erste Aufruf startet alle Container und dauert länger; lokal ist die Historie nach rund 40 Sekunden komplett.',
    tone: 'wide',
  },
];

const SECURE = [
  {
    title: 'Zugangscode mit Hash',
    text: 'Der Server kennt nur den SHA-256-Hash des Codes. Die Sitzung hält ein HttpOnly-Cookie.',
  },
  {
    title: 'Login-Drossel',
    text: 'Je Adresse 5 Fehlversuche in 10 Minuten, insgesamt 40. Danach wird das Anmelden vorübergehend abgewiesen.',
  },
  {
    title: 'API-Key mit Rolle für /mcp',
    text: 'Externe MCP-Clients brauchen einen Key. Jeder Key trägt genau eine Rolle und sieht nur deren Daten.',
  },
  {
    title: 'Audit',
    text: 'Jeder Tool-Aufruf wird protokolliert und in der Tool-Spur der Oberfläche angezeigt.',
  },
  {
    title: 'Rate Limit',
    text: 'Der MCP-Endpunkt nimmt höchstens 30 Anfragen pro Minute an.',
  },
  {
    title: 'Strenge CSP',
    text: 'Nur eigene Skripte, Stile und Schriften. Keine Inline-Skripte, keine externen Ressourcen, keine Frames.',
  },
];

const HONEST = [
  {
    title: 'Echtes Modell',
    text: 'Wortlaut und Tool-Weg können abweichen. Die Zahlen kommen deterministisch vom Server.',
  },
  { title: 'Simulierte Daten', text: 'Sie sind nicht als Quelle gedacht.' },
  {
    title: 'Anthropic-API',
    text: 'Deine Fragen gehen zur Beantwortung an die Anthropic-API.',
  },
  {
    title: 'Kein Ersatz',
    text: 'Das Andon-Dashboard bleibt für den Blick auf die Wand, die Echtzeit-Alarmierung bleibt im Leitsystem.',
  },
  {
    title: 'Keine Personen',
    text: 'Keine personenbezogene Leistungsauswertung. Die Besetzung gibt es nur auf Team- und Schichtebene.',
  },
];

const TECH = [
  '.NET 10',
  'OPC UA',
  'MCP',
  'Redis',
  'Vue 3',
  'Azure Container Apps',
  'OpenTelemetry',
];

const PROJECT_LINKS = [
  { label: 'Quellcode auf GitHub', href: REPO_URL, primary: true },
  { label: 'Spezifikation', href: `${REPO_URL}/blob/main/docs/spec.md`, primary: false },
  {
    label: 'Aufbau und Kosten in Azure',
    href: `${REPO_URL}/blob/main/infra/README.md`,
    primary: false,
  },
];

const auth = useAuthStore();
const ui = useUiStore();

/** Ohne Sitzung ersetzt die Seite die Anmeldung; geöffnet aus der Anwendung führt sie zurück in die Demo. */
const needsLogin = computed(() => auth.needsLogin);
const accessNote = computed(() =>
  auth.enabled ? 'Du bist angemeldet.' : 'Diese Instanz braucht keinen Zugangscode.',
);

let previousTitle = '';
onMounted(() => {
  previousTitle = document.title;
  document.title = PAGE_TITLE;
  window.scrollTo(0, 0);
});
onBeforeUnmount(() => {
  document.title = previousTitle;
});
</script>

<template>
  <div class="about-page">
    <a class="skip-link" href="#inhalt">Zum Inhalt springen</a>

    <header class="about-top">
      <div class="about-wrap about-top-in">
        <a
          class="about-brand"
          href="#top"
          aria-label="Schleifwerk · KI-Leitstand, zum Seitenanfang"
        >
          <span class="brand-logo" aria-hidden="true">
            <svg
              width="18"
              height="18"
              viewBox="0 0 24 24"
              fill="none"
              stroke="#FFFFFF"
              stroke-width="2"
            >
              <circle cx="12" cy="12" r="9" />
              <circle cx="12" cy="12" r="2.5" />
              <path d="M12 3v4M12 17v4M3 12h4M17 12h4" />
            </svg>
          </span>
          <span class="about-brand-text"
            ><span class="about-brand-long">Schleifwerk · </span>KI-Leitstand</span
          >
        </a>
        <nav class="about-nav" aria-label="Abschnitte der Seite">
          <a href="#so-funktioniert-es">So funktioniert es</a>
          <a href="#rollen">Fragen</a>
          <a href="#grenzen">Grenzen</a>
          <a href="#sicherheit">Sicherheit</a>
          <a href="#projekt">Projekt</a>
        </nav>
        <button
          v-if="!needsLogin"
          type="button"
          class="about-btn about-btn-light"
          @click="ui.closeAbout()"
        >
          Zurück zur Demo
        </button>
        <a v-else class="about-btn about-btn-light" href="#zugang">Demo ausprobieren</a>
      </div>
    </header>

    <main id="inhalt" tabindex="-1">
      <section id="top" class="about-hero" aria-labelledby="about-title">
        <div class="about-wrap about-hero-grid">
          <div class="about-hero-copy">
            <p class="about-eyebrow">Live-Demo mit simulierten Daten</p>
            <h1 id="about-title" class="about-title">
              Frag deine Halle.
              <span class="about-title-accent">Die KI erklärt, warum.</span>
            </h1>
            <p class="about-lead">
              Eine simulierte Schleifmittel-Halle, ausgewertet von einem MCP-Server und erklärt von
              Claude. Alles nur lesend.
            </p>
            <div class="about-cta">
              <button
                v-if="!needsLogin"
                type="button"
                class="about-btn about-btn-primary"
                @click="ui.closeAbout()"
              >
                Zurück zur Demo
              </button>
              <a v-else class="about-btn about-btn-primary" href="#zugang">Demo ausprobieren</a>
              <a class="about-btn about-btn-ghost" href="#so-funktioniert-es">So funktioniert es</a>
            </div>
          </div>

          <div class="about-shots">
            <picture class="about-shot-desktop">
              <source :srcset="image('web-desktop', 'webp')" type="image/webp" />
              <img
                :src="image('web-desktop', 'png')"
                width="1200"
                height="750"
                fetchpriority="high"
                decoding="async"
                alt="Weboberfläche am PC: links die Anlagen der Halle A mit Ampeln, in der Mitte Claudes Antwort auf die Frage nach der Nachtschicht mit Tabelle und Balken, rechts die Spur der MCP-Aufrufe."
              />
            </picture>
            <div class="about-device">
              <picture>
                <source :srcset="image('web-mobile', 'webp')" type="image/webp" />
                <img
                  :src="image('web-mobile', 'png')"
                  width="468"
                  height="1013"
                  decoding="async"
                  alt="Handy-Ansicht der Übersicht: Zähler für Problem, Warnung und Läuft, offene Fälle mit dem Knopf Mit Claude analysieren und die untere Navigation."
                />
              </picture>
            </div>
          </div>
        </div>
      </section>

      <section class="about-claim" aria-label="Kernaussage">
        <div class="about-wrap">
          <p class="about-claim-text">
            Ein Dashboard zeigt, <em>dass</em> etwas ist. Die KI erklärt, <em>warum</em> und
            <em>was zu tun ist</em>, auf Basis derselben verlässlichen Daten.
          </p>
          <p class="about-claim-sub">
            Reports beantworten die Fragen von gestern, die KI-Schnittstelle die Fragen von heute.
          </p>
        </div>
      </section>

      <section id="so-funktioniert-es" class="about-section" aria-labelledby="h-flow">
        <div class="about-wrap">
          <h2 id="h-flow" class="about-h2">So funktioniert es</h2>
          <p class="about-intro">
            Die Demo bildet eine Schleifmittel-Halle nach. Alle Werte sind simuliert, die
            Schnittstellen sind echt: OPC UA, MCP und die Claude-API.
          </p>
          <ul class="about-facts" aria-label="Die simulierte Halle">
            <li v-for="fact in HALL_FACTS" :key="fact.label">
              <span class="about-fact-value">{{ fact.value }}</span>
              <span class="about-fact-label">{{ fact.label }}</span>
            </li>
          </ul>
          <ol class="about-flow" aria-label="Weg einer Antwort">
            <li v-for="step in FLOW" :key="step.tag" class="about-flow-step">
              <span class="about-flow-tag">{{ step.tag }}</span>
              <h3>{{ step.title }}</h3>
              <p>{{ step.text }}</p>
            </li>
          </ol>
        </div>
      </section>

      <section id="rollen" class="about-section about-section-tint" aria-labelledby="h-roles">
        <div class="about-wrap">
          <h2 id="h-roles" class="about-h2">Drei Rollen, eine Datenbasis</h2>
          <p class="about-intro">
            Dieselben Daten, drei Blickwinkel. Die Rolle bestimmt, was der MCP-Server liefert, und
            Claude sieht nur, was die Person sehen darf. Die Benutzer sind fiktiv.
          </p>
          <ul class="about-roles">
            <li v-for="role in ROLES" :key="role.name" class="about-role">
              <div class="about-role-head">
                <h3>{{ role.name }}</h3>
                <p>{{ role.scope }}</p>
              </div>
              <ul class="about-asks" :aria-label="`Beispielfragen: ${role.name}`">
                <li v-for="question in role.questions" :key="question">{{ question }}</li>
              </ul>
            </li>
          </ul>
          <div class="about-free">
            <h3>Oder frei fragen</h3>
            <ul>
              <li v-for="question in FREE_QUESTIONS" :key="question">{{ question }}</li>
            </ul>
          </div>
        </div>
      </section>

      <section id="grenzen" class="about-section" aria-labelledby="h-limits">
        <div class="about-wrap about-numbers">
          <div>
            <h2 id="h-limits" class="about-h2">Zahlen und Grenzen</h2>
            <p class="about-intro">Was die Demo kann und wo sie bewusst aufhört.</p>
            <dl class="about-limits">
              <div
                v-for="limit in LIMITS"
                :key="limit.label"
                :class="['about-limit', limit.tone && `is-${limit.tone}`]"
              >
                <dt>{{ limit.label }}</dt>
                <dd class="about-limit-value">{{ limit.value }}</dd>
                <dd class="about-limit-text">{{ limit.text }}</dd>
              </div>
            </dl>
          </div>
          <figure class="about-reports">
            <picture>
              <source :srcset="image('reports-desktop', 'webp')" type="image/webp" />
              <img
                :src="image('reports-desktop', 'png')"
                width="960"
                height="600"
                loading="lazy"
                decoding="async"
                alt="Reports-Seite der Demo mit Kennzahl-Kacheln, Antwortzeiten, Verlauf der letzten 24 Stunden, MCP-Werkzeugen und Datenaufnahme."
              />
            </picture>
            <figcaption>
              Die Reports zeigen Nutzung und Betrieb der Demo, ohne Frage- und Antworttexte.
            </figcaption>
          </figure>
        </div>
      </section>

      <section id="sicherheit" class="about-section about-section-tint" aria-labelledby="h-safe">
        <div class="about-wrap">
          <h2 id="h-safe" class="about-h2">Sicherheit und Ehrlichkeit</h2>
          <div class="about-duo">
            <article class="about-panel">
              <h3>Sicher gebaut</h3>
              <ul class="about-facts-list">
                <li v-for="item in SECURE" :key="item.title">
                  <span class="about-mark is-ok"
                    ><IconSvg name="check" :size="14" :stroke-width="2.6"
                  /></span>
                  <span
                    ><strong>{{ item.title }}:</strong> {{ item.text }}</span
                  >
                </li>
              </ul>
            </article>
            <article class="about-panel about-panel-honest">
              <h3>Ehrlich gesagt</h3>
              <ul class="about-facts-list">
                <li v-for="item in HONEST" :key="item.title">
                  <span class="about-mark is-info"
                    ><IconSvg name="info" :size="14" :stroke-width="2.4"
                  /></span>
                  <span
                    ><strong>{{ item.title }}:</strong> {{ item.text }}</span
                  >
                </li>
              </ul>
            </article>
          </div>
        </div>
      </section>

      <section id="zugang" class="about-access" aria-labelledby="h-access">
        <div class="about-wrap about-access-grid">
          <div class="about-access-copy">
            <h2 id="h-access" class="about-h2">Zugang zur Demo</h2>
            <p>
              Jede Frage ruft die kostenpflichtige Claude-API auf. Der Zugangscode schützt die Demo
              deshalb vor Dauerlast. Eingeladene öffnen ihren persönlichen Link und sind sofort
              drin.
            </p>
            <ul class="about-badges" aria-label="Eckdaten">
              <li>Nur lesend</li>
              <li>Echtes Modell</li>
              <li>Tool-Spur sichtbar</li>
            </ul>
          </div>
          <div class="about-access-card">
            <AccessForm v-if="needsLogin" />
            <div v-else class="access-done">
              <h3 class="access-title">{{ accessNote }}</h3>
              <button type="button" class="about-btn about-btn-primary" @click="ui.closeAbout()">
                Zurück zur Demo
              </button>
            </div>
          </div>
        </div>
      </section>

      <section id="projekt" class="about-section" aria-labelledby="h-project">
        <div class="about-wrap about-project">
          <div>
            <h2 id="h-project" class="about-h2">Aus dem Projekt</h2>
            <p class="about-intro">
              Der Quellcode ist öffentlich: Simulator, Collector, MCP-Server, Weboberfläche und die
              Bicep-Dateien für Azure.
            </p>
            <ul class="about-chips" aria-label="Technik">
              <li v-for="tech in TECH" :key="tech">{{ tech }}</li>
            </ul>
          </div>
          <ul class="about-links">
            <li v-for="link in PROJECT_LINKS" :key="link.href">
              <a
                :class="['about-link-row', { 'is-primary': link.primary }]"
                :href="link.href"
                target="_blank"
                rel="noopener noreferrer"
              >
                <IconSvg v-if="link.primary" name="github" :size="20" />
                <span>{{ link.label }}</span>
                <IconSvg name="arrow" :size="18" />
                <span class="visually-hidden">(öffnet in neuem Tab)</span>
              </a>
            </li>
          </ul>
        </div>
      </section>
    </main>

    <footer class="about-foot">
      <div class="about-wrap about-foot-in">
        <p class="about-foot-brand">Schleifwerk · KI-Leitstand</p>
        <p class="about-foot-note">Reiner Lesezugriff, simulierte Daten</p>
        <nav class="about-foot-legal" aria-label="Rechtliches">
          <a :href="IMPRINT_URL" target="_blank" rel="noopener noreferrer">Impressum</a>
          <a :href="PRIVACY_URL" target="_blank" rel="noopener noreferrer">Datenschutz</a>
        </nav>
      </div>
    </footer>
  </div>
</template>
