<script setup lang="ts">
import { computed } from 'vue';
import { splitIdentifiers } from '@/utils/format';

const props = defineProps<{ text: string }>();
const parts = computed(() => splitIdentifiers(props.text));
</script>

<template>
  <template v-for="(part, index) in parts" :key="index"
    ><span v-if="part.keep" class="keep-together">{{ part.text }}</span
    ><template v-else>{{ part.text }}</template></template
  >
</template>
