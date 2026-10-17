<script setup lang="ts">
import type { ClimateInfo } from '@/api/types';
import IconSvg from './IconSvg.vue';

withDefaults(defineProps<{ climate: ClimateInfo; variant?: 'climate' | 'm-climate' }>(), {
  variant: 'climate',
});
</script>

<template>
  <div :class="variant" :title="climate.tooltip">
    <span class="climate-part"
      ><IconSvg name="thermometer" :size="12" />{{ climate.temperature }}</span
    >
    <span class="climate-part"><IconSvg name="drop" :size="12" />{{ climate.humidity }}</span>
    <span>{{ climate.text }}</span>
  </div>
</template>
