<script setup lang="ts">
import type { AnswerTable } from '@/api/types';
import { RATING_TEXT } from '@/utils/format';
import KeepIds from './KeepIds.vue';

defineProps<{ table: AnswerTable }>();
</script>

<template>
  <div v-if="table.rows.length > 0" class="table-wrap">
    <table class="table">
      <thead>
        <tr>
          <th v-for="column in table.columns" :key="column" scope="col">{{ column }}</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="(row, index) in table.rows" :key="index">
          <td class="cell-topic"><KeepIds :text="row.cells[0] ?? ''" /></td>
          <td class="cell-data"><KeepIds :text="row.cells[1] ?? ''" /></td>
          <td class="cell-rating">
            <span :class="['badge', `badge-${row.rating}`]">{{ RATING_TEXT[row.rating] }}</span>
          </td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
