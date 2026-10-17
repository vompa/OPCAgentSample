<script setup lang="ts">
import { useUiStore } from '@/stores/ui';

const ui = useUiStore();
</script>

<template>
  <!-- Die Ansage-Region bleibt immer im Baum, nur die Karte blendet ein und aus. -->
  <div id="toast" class="toast-host" role="status" aria-live="polite">
    <Transition name="toast">
      <div v-if="ui.toastMessage" class="toast">{{ ui.toastMessage }}</div>
    </Transition>
  </div>
</template>
