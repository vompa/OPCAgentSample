<script setup lang="ts">
import { computed } from 'vue';
import type { MachineFinding, MachineRange, MachineStation } from '@/api/types';
import FindingList from '@/components/FindingList.vue';
import IconSvg from '@/components/IconSvg.vue';
import OeeStack from '@/components/OeeStack.vue';
import ParetoBars from '@/components/ParetoBars.vue';
import RatingMark from '@/components/RatingMark.vue';
import TrendBand from '@/components/TrendBand.vue';
import { formatNumber } from '@/utils/format';
import { euroText, minutesText, percentValue } from '@/utils/machineFormat';

const props = defineProps<{
  station: MachineStation;
  range: MachineRange;
  rangeLabel: string;
  findings: MachineFinding[];
  showCosts: boolean;
}>();

const emit = defineEmits<{ back: [] }>();

const reliability = computed(() => props.station.reliability);
const mould = computed(() => props.station.maintenance.mould);
const longestScrap = computed(() =>
  Math.max(1, ...props.station.scrapByReason.map((reason) => reason.count)),
);
const processTitle = computed(() =>
  props.station.type === 'press'
    ? 'Taktzeit und Presskraftaufbau'
    : props.station.type === 'oven'
      ? 'Ofentemperatur'
      : 'Leistung der Verpackung',
);
</script>

<template>
  <section class="mc-detail" :aria-labelledby="`mc-detail-${station.id}`">
    <header class="mc-detail-head">
      <button type="button" class="btn mc-back" @click="emit('back')">
        <IconSvg name="chevron" :size="14" :stroke-width="3" />Alle Stationen
      </button>
      <div class="mc-detail-title">
        <h3 :id="`mc-detail-${station.id}`" class="panel-title">
          {{ station.id }} <span class="mc-muted">· {{ station.name }}</span>
        </h3>
        <p class="panel-sub">{{ rangeLabel }}</p>
      </div>
      <RatingMark :rating="station.rating" />
    </header>

    <FindingList
      v-if="findings.length > 0"
      :findings="findings"
      :range="range"
      empty-text=""
      class="mc-station-findings"
    />

    <div class="mc-detail-grid">
      <section v-if="station.oee" class="rp-card" aria-labelledby="mc-oee-title">
        <h4 id="mc-oee-title" class="panel-title">OEE zerlegt</h4>
        <OeeStack :oee="station.oee" :title="`${station.id}: Verluste und OEE`" />
        <p class="mc-shift-oee">
          <span class="mc-shift-oee-label">OEE nach Schicht</span>
          <span v-for="shift in station.shiftOee" :key="shift.shift"
            >{{ shift.shift }} <strong>{{ percentValue(shift.oeePercent) }}</strong></span
          >
        </p>
      </section>
      <section v-else class="rp-card" aria-labelledby="mc-occ-title">
        <h4 id="mc-occ-title" class="panel-title">Belegung</h4>
        <p class="mc-big">{{ percentValue(station.occupancyPercent) }}</p>
        <p class="rp-tile-note">
          Öfen werden über die zeitgewichtete Platzbelegung bewertet, nicht über die OEE. Ab 95 %
          gilt der Ofen als Engpass (Warnung).
        </p>
      </section>

      <section class="rp-card" aria-labelledby="mc-pareto-title">
        <h4 id="mc-pareto-title" class="panel-title">Stillstand nach Grund</h4>
        <ParetoBars
          :title="`${station.id}: Stillstand nach Grund`"
          :items="station.pareto"
          :show-euro="showCosts"
        />
        <p class="rp-tile-note">
          Zusammen {{ minutesText(station.downtimeMinutes) }} ({{
            percentValue(station.downtimeSharePercent, 1)
          }}
          der Planbelegungszeit)<template v-if="showCosts"
            >, Kosten {{ euroText(station.downtimeEuro) }} (Stillstand) und
            {{ euroText(station.scrapEuro) }} (Ausschuss)</template
          >.
        </p>
      </section>
    </div>

    <section v-if="station.trends.length > 0" class="rp-card" aria-labelledby="mc-process-title">
      <h4 id="mc-process-title" class="panel-title">{{ processTitle }}</h4>
      <div class="mc-trends">
        <TrendBand v-for="trend in station.trends" :key="trend.key" :trend="trend" />
      </div>
    </section>

    <div class="mc-detail-trio">
      <section v-if="station.type === 'press'" class="rp-card" aria-labelledby="mc-scrap-title">
        <h4 id="mc-scrap-title" class="panel-title">Ausschuss nach Grund</h4>
        <p v-if="station.scrapByReason.length === 0" class="empty-note">
          Kein Ausschuss mit erfasstem Grund im Zeitraum.
        </p>
        <template v-else>
          <ul class="mc-bars" aria-label="Ausschuss nach Grund">
            <li v-for="reason in station.scrapByReason" :key="reason.reason" class="mc-bar-row">
              <span class="mc-bar-label">{{ reason.reason }}</span>
              <span class="mc-bar-track" aria-hidden="true"
                ><span
                  class="mc-bar-fill is-scrap"
                  :style="{ width: `${(reason.count / longestScrap) * 100}%` }"
                ></span
              ></span>
              <span class="mc-bar-value"
                >{{ formatNumber(reason.count) }} Takte
                <span class="mc-bar-sub">{{ formatNumber(reason.percent, 0) }} %</span></span
              >
            </li>
          </ul>
          <p class="rp-tile-note">
            Ausschussquote {{ percentValue(station.scrapPercent, 1)
            }}<template v-if="showCosts"> · Kosten {{ euroText(station.scrapEuro) }}</template
            >. Die Gründe zählen Takte mit Ausschuss (Rohdaten ab KW 41).
          </p>
        </template>
      </section>

      <section class="rp-card" aria-labelledby="mc-rel-title">
        <h4 id="mc-rel-title" class="panel-title">Zuverlässigkeit</h4>
        <dl class="rp-facts mc-facts">
          <div class="rp-fact">
            <dt>MTBF</dt>
            <dd>
              {{
                reliability.mtbfHours === null ? '–' : `${formatNumber(reliability.mtbfHours, 1)} h`
              }}
            </dd>
          </div>
          <div class="rp-fact">
            <dt>MTTR</dt>
            <dd>
              {{ reliability.mttrMinutes === null ? '–' : minutesText(reliability.mttrMinutes) }}
            </dd>
          </div>
          <div class="rp-fact">
            <dt>Störungen</dt>
            <dd>{{ reliability.failures }}</dd>
          </div>
        </dl>
        <div
          v-if="reliability.failures > 0"
          class="rp-table-scroll"
          tabindex="0"
          role="region"
          :aria-label="`${station.id}: Störungen nach Schicht`"
        >
          <table class="rp-table">
            <thead>
              <tr>
                <th scope="col">Schicht</th>
                <th scope="col" class="num">Störungen</th>
                <th scope="col" class="num">MTTR</th>
                <th scope="col" class="num">Reaktion</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="shift in reliability.byShift" :key="shift.shift">
                <th scope="row">{{ shift.shift }}</th>
                <td class="num" data-label="Störungen">{{ shift.failures }}</td>
                <td class="num" data-label="MTTR">
                  {{ shift.mttrMinutes === null ? '–' : minutesText(shift.mttrMinutes) }}
                </td>
                <td class="num" data-label="Reaktion">
                  {{ shift.reactionMinutes === null ? '–' : minutesText(shift.reactionMinutes) }}
                </td>
              </tr>
            </tbody>
          </table>
        </div>
        <p v-else class="empty-note">Keine Störung im Zeitraum.</p>
        <p class="rp-tile-note">
          Reaktionszeit der Instandhaltung: nur nachts (Bereitschaft) erfasst.
        </p>
      </section>

      <section class="rp-card" aria-labelledby="mc-maint-title">
        <h4 id="mc-maint-title" class="panel-title">Wartung und Standzeit</h4>
        <div v-if="mould" class="mc-mould">
          <p class="mc-mould-line">
            <span>Form {{ mould.mouldId }}</span>
            <strong>{{ mould.wearPercent }} % der Standzeit</strong>
            <RatingMark :rating="mould.rating" />
          </p>
          <div
            class="mc-hbar mc-hbar-wide"
            role="img"
            :aria-label="`Verschleiß ${mould.wearPercent} Prozent`"
          >
            <span
              :class="['mc-hbar-fill', `is-${mould.rating}`]"
              :style="{ width: `${Math.min(100, mould.wearPercent)}%` }"
            ></span>
          </div>
          <p class="rp-tile-note">
            {{ formatNumber(mould.strokes) }} von {{ formatNumber(mould.strokeLimit) }} Hüben, noch
            {{ formatNumber(mould.remainingStrokes)
            }}<template v-if="mould.limitDay">
              · Grenze voraussichtlich {{ mould.limitDay }}</template
            >
          </p>
        </div>
        <ul v-if="station.maintenance.items.length > 0" class="mc-maint-list">
          <li
            v-for="item in station.maintenance.items"
            :key="item.id"
            :class="{ 'is-overdue': item.overdue }"
          >
            <span class="mc-maint-title">{{ item.title }}</span>
            <span class="mc-maint-due"
              >{{ item.overdue ? 'Überfällig: ' : 'Fällig ' }}{{ item.dueText }} · Stillstand
              {{ item.stopMinutes }} min</span
            >
          </li>
        </ul>
        <p v-if="!mould && station.maintenance.items.length === 0" class="empty-note">
          In den nächsten 14 Tagen ist keine Wartung fällig.
        </p>
      </section>
    </div>
  </section>
</template>
