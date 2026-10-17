<script setup lang="ts">
import { ICONS, type IconName } from './icons';

withDefaults(
  defineProps<{ name: IconName; size?: number; fill?: string; strokeWidth?: number }>(),
  { size: 16, fill: 'none', strokeWidth: 2 },
);
</script>

<template>
  <!-- Dekorativ (aria-hidden); die Beschriftung steht am umgebenden Knopf. -->
  <svg
    class="icon"
    :width="size"
    :height="size"
    viewBox="0 0 24 24"
    :fill="fill"
    stroke="currentColor"
    :stroke-width="strokeWidth"
    aria-hidden="true"
  >
    <component :is="part[0]" v-for="(part, index) in ICONS[name]" :key="index" v-bind="part[1]" />
  </svg>
</template>
