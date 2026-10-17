// Erzeugt die Referenzdaten (JSON) aus dem Mockup (docs/mockup/Main.dc.html und Mobil.dc.html).
// Aufruf: node extract-reference-data.mjs   (im Ordner Demo/ReferenceData; die JSON-Dateien werden daneben geschrieben)
// Die Mockup-Dateien sind keine eigenständigen Seiten: Die Daten stehen als JavaScript-Objekte im Script-Block.
// Das Skript schneidet die Zuweisungen aus, wertet sie in einer leeren VM aus und schreibt sie im API-Format.
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const here = dirname(fileURLToPath(import.meta.url));
const mockupDir = join(here, '..', '..', '..', '..', 'docs', 'mockup');
const main = readFileSync(join(mockupDir, 'Main.dc.html'), 'utf8');
const mobile = readFileSync(join(mockupDir, 'Mobil.dc.html'), 'utf8');

const ROLE_IDS = { sl: 'shift_lead', hl: 'hall_lead', pl: 'production_lead' };

/** Schneidet den Ausdruck hinter "var NAME = " bis zum Semikolon auf Klammerebene 0 aus (Zeichenketten beachtet). */
function extractExpression(source, name, from = 0) {
  const start = source.indexOf(`var ${name} = `, from);
  if (start < 0) throw new Error(`var ${name} nicht gefunden`);
  let i = start + `var ${name} = `.length;
  const begin = i;
  let depth = 0;
  let quote = null;
  for (; i < source.length; i++) {
    const c = source[i];
    if (quote) {
      if (c === '\\') i++;
      else if (c === quote) quote = null;
      continue;
    }
    if (c === "'" || c === '"' || c === '`') quote = c;
    else if ('([{'.includes(c)) depth++;
    else if (')]}'.includes(c)) depth--;
    else if (c === ';' && depth === 0) return source.slice(begin, i);
  }
  throw new Error(`Ende von ${name} nicht gefunden`);
}

function evaluate(expression, context = {}) {
  return vm.runInNewContext(`(${expression})`, { ...context });
}

const read = (source, name, context) => evaluate(extractExpression(source, name), context);

/** Zerlegt "09:42 · Quellen: a, b" in Datenstand-Text und Quellenliste. */
function splitStand(stand) {
  const [asOf, sources = ''] = stand.split(' · Quellen: ');
  return { dataAsOf: asOf.replace(/^Stand /, ''), sources: sources ? sources.split(', ') : [] };
}

/** Wandelt die JavaScript-Parameterschreibweise des Mockups ({ scope: "Halle A" }) in ein Objekt. */
function parseParameters(text) {
  try {
    return evaluate(text);
  } catch {
    return { text };
  }
}

function toAnswer(id, raw, shortTitles) {
  const { dataAsOf, sources } = splitStand(raw.stand);
  return {
    id,
    role: ROLE_IDS[raw.role],
    question: raw.q,
    shortTitle: shortTitles[id] ?? null,
    card: {
      headline: raw.head,
      todo: raw.option ? [raw.option] : [],
      table: {
        columns: ['Thema', 'Daten', 'Bewertung'],
        rows: raw.rows.map(([topic, data, rating]) => ({ cells: [topic, data], rating })),
      },
      chart: raw.bars.length === 0
        ? null
        : {
            title: raw.chartTitle,
            max: raw.max,
            unit: raw.unit,
            decimals: raw.dec ?? 0,
            bars: raw.bars.map(([label, value, rating]) => ({ label, value, rating })),
          },
      why: raw.insight ? [raw.insight] : [],
      dataAsOf,
      sources,
      prompt: raw.prompt ?? null,
      form: raw.form,
    },
    toolCalls: raw.trace.map(([tool, parameters, durationMs, dataSource]) => ({
      tool,
      parameters: parseParameters(parameters),
      durationMs,
      source: 'reference',
      dataSource,
    })),
  };
}

// ---- Antworten (Objekt A), Reihenfolge der Favoriten (ORDER), Kurztitel (SHORT) ----
const answersRaw = read(main, 'A');
const order = read(main, 'ORDER');
const shortTitles = read(main, 'SHORT');
const answers = Object.entries(answersRaw)
  .filter(([, raw]) => raw.role) // freie Antworten ohne Rolle gibt es im Objekt A nicht
  .map(([id, raw]) => toAnswer(id, raw, shortTitles));
writeJson('answers.json', {
  note: 'Erzeugt aus docs/mockup/Main.dc.html (Objekt A, ORDER, SHORT) mit extract-reference-data.mjs. Nicht von Hand ändern.',
  sampleByRole: Object.fromEntries(Object.entries(order).map(([r, ids]) => [ROLE_IDS[r], ids[0]])),
  answers,
});

// Das Postfach (INBOX) stammt nicht mehr aus dem Mockup: Es entsteht aus den Eskalationsregeln (Demo/RuleInboxSource.cs).
// Hallenübersicht und Meldungen (GROUPS, FEED) kommen seit der Datenaufnahme aus Redis.

// ---- Rollenprofile (USERS, ROLES, Statusleiste, Meldungsfilter) ----
const usersMain = read(main, 'USERS');
const usersMobile = read(mobile, 'USERS');
const rolesMain = read(main, 'ROLES');
const rolesMobileMatch = /var roles = (\[\[.*?\]\])\.map/s.exec(mobile);
const rolesMobile = Object.fromEntries(evaluate(rolesMobileMatch[1]));
const barScope = evaluate(/var barScope = (\{.*?\})\[role\]/s.exec(main)[1]);
const feedFilter = evaluate(/var feedFilter = (\{.*?\})\[role\]/s.exec(main)[1]);
const profiles = {};
for (const [r, roleId] of Object.entries(ROLE_IDS)) {
  const u = usersMain[r];
  profiles[roleId] = {
    label: rolesMain.find((x) => x.id === r).label,
    shortLabel: rolesMobile[r],
    initials: u.initials,
    firstName: usersMobile[r].first,
    title: u.title,
    mobileTitle: usersMobile[r].title,
    scope: u.scope,
    workTime: u.shift,
    notification: u.notify,
    scopeText: barScope[r],
    feedFilterText: feedFilter[r],
  };
}
writeJson('profiles.json', {
  note: 'Erzeugt aus docs/mockup (USERS, ROLES, barScope, feedFilter).',
  profiles,
});

function writeJson(fileName, value) {
  writeFileSync(join(here, fileName), JSON.stringify(value, null, 2) + '\n', 'utf8');
  console.log(`geschrieben: ${fileName}`);
}
