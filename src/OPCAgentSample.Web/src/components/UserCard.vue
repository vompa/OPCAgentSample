<script setup lang="ts">
import { useAuthStore } from '@/stores/auth';
import { useIdentityStore } from '@/stores/identity';

const identity = useIdentityStore();
const auth = useAuthStore();
</script>

<template>
  <template v-if="identity.me">
    <div class="user-head">
      <div class="user-avatar" aria-hidden="true">{{ identity.me.user.initials }}</div>
      <div>
        <h2 class="user-name" tabindex="-1">{{ identity.me.user.displayName }}</h2>
        <div class="user-title">{{ identity.me.user.title }}</div>
      </div>
    </div>
    <dl class="user-facts">
      <dt>Sicht</dt>
      <dd>{{ identity.me.role.scope }}</dd>
      <dt>Arbeitszeit</dt>
      <dd>{{ identity.me.user.workTime }}</dd>
      <dt>Rechte</dt>
      <dd>{{ identity.me.role.rights.join(', ') }}</dd>
      <dt>Benachrichtigung</dt>
      <dd>{{ identity.me.user.notification }}</dd>
    </dl>
    <p class="user-note">
      Die Rolle bestimmt, welche Daten der MCP-Server liefert. Claude sieht nur, was diese Person
      sehen darf.
    </p>
    <button v-if="auth.enabled" type="button" class="btn user-logout" @click="auth.logout()">
      Abmelden
    </button>
  </template>
</template>
