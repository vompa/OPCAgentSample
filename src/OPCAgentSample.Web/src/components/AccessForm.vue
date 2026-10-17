<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { useAuthStore } from '@/stores/auth';

// Die Adresse steht nur in Teilen im Quelltext und wird erst im Browser zusammengesetzt (gegen Adress-Sammler).
const MAIL_USER = 'vompa.dev';
const MAIL_DOMAIN = 'gmail.com';
const MAIL_SUBJECT = 'Zugangscode für die Demo Schleifwerk KI-Leitstand';

const auth = useAuthStore();
const code = ref('');
const input = ref<HTMLInputElement | null>(null);
const mailLink = ref<HTMLAnchorElement | null>(null);

onMounted(() => {
  const link = mailLink.value;
  if (link) {
    link.href = `mailto:${MAIL_USER}@${MAIL_DOMAIN}?subject=${encodeURIComponent(MAIL_SUBJECT)}`;
  }
});

async function submit(): Promise<void> {
  const value = code.value.trim();
  if (!value || auth.busy) return;
  const ok = await auth.login(value);
  if (!ok) {
    code.value = '';
    input.value?.focus();
  }
}
</script>

<template>
  <form class="access-form" aria-labelledby="access-title" novalidate @submit.prevent="submit">
    <h3 id="access-title" class="access-title">Zugangscode eingeben</h3>

    <label class="access-label" for="access-code">Zugangscode</label>
    <input
      id="access-code"
      ref="input"
      v-model="code"
      class="access-input"
      type="password"
      name="access-code"
      autocomplete="off"
      autocapitalize="off"
      spellcheck="false"
      required
      :aria-invalid="auth.error ? 'true' : undefined"
      :aria-describedby="auth.error ? 'access-error access-help' : 'access-help'"
    />

    <p v-if="auth.error" id="access-error" class="access-error" role="alert">{{ auth.error }}</p>

    <button type="submit" class="about-btn about-btn-primary" :disabled="auth.busy || !code.trim()">
      {{ auth.busy ? 'Anmelden …' : 'Anmelden' }}
    </button>

    <p id="access-help" class="access-help">
      Noch keinen Code?
      <a ref="mailLink" class="access-mail" href="#zugang">Zugangscode auf Anfrage</a>
    </p>
  </form>
</template>
