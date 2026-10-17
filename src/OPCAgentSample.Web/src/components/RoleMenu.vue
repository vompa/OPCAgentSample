<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue';
import { useAppStore } from '@/stores/app';
import { useIdentityStore } from '@/stores/identity';
import IconSvg from './IconSvg.vue';

const identity = useIdentityStore();
const app = useAppStore();

const open = ref(false);
const root = ref<HTMLElement | null>(null);
const trigger = ref<HTMLButtonElement | null>(null);
const menu = ref<HTMLElement | null>(null);

const roles = computed(() => identity.me?.roles ?? []);
const current = computed(() => roles.value.find((role) => role.id === identity.role));

function items(): HTMLElement[] {
  return Array.from(menu.value?.querySelectorAll<HTMLElement>('[role="menuitemradio"]') ?? []);
}

/** Öffnet das Menü und setzt den Fokus auf die aktive Rolle (oder, per Pfeiltaste, auf den Rand der Liste). */
async function show(focus: 'active' | 'first' | 'last' = 'active'): Promise<void> {
  open.value = true;
  await nextTick();
  const entries = items();
  const target =
    focus === 'first'
      ? entries[0]
      : focus === 'last'
        ? entries.at(-1)
        : (entries.find((entry) => entry.getAttribute('aria-checked') === 'true') ?? entries[0]);
  target?.focus();
}

function hide(returnFocus: boolean): void {
  open.value = false;
  if (returnFocus) trigger.value?.focus();
}

function toggle(): void {
  if (open.value) hide(false);
  else void show();
}

function onTriggerKeydown(event: KeyboardEvent): void {
  if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return;
  event.preventDefault();
  void show(event.key === 'ArrowDown' ? 'first' : 'last');
}

function onMenuKeydown(event: KeyboardEvent): void {
  const entries = items();
  const index = entries.indexOf(document.activeElement as HTMLElement);
  let next: number | null = null;
  switch (event.key) {
    case 'ArrowDown':
      next = (index + 1) % entries.length;
      break;
    case 'ArrowUp':
      next = (index - 1 + entries.length) % entries.length;
      break;
    case 'Home':
      next = 0;
      break;
    case 'End':
      next = entries.length - 1;
      break;
    case 'Escape':
      event.preventDefault();
      hide(true);
      return;
    case 'Tab':
      hide(false); // Das Menü ist kein Fokusfang: Tab geht weiter.
      return;
    default:
      return;
  }
  event.preventDefault();
  entries[next]?.focus();
}

function select(id: string): void {
  hide(true);
  if (id !== identity.role) void app.loadRole(id);
}

function onPointerDown(event: PointerEvent): void {
  if (!root.value?.contains(event.target as Node)) hide(false);
}

watch(open, (isOpen) => {
  if (isOpen) document.addEventListener('pointerdown', onPointerDown);
  else document.removeEventListener('pointerdown', onPointerDown);
});
onBeforeUnmount(() => document.removeEventListener('pointerdown', onPointerDown));
</script>

<template>
  <div v-if="roles.length > 0" ref="root" class="role-menu">
    <button
      id="role-menu-btn"
      ref="trigger"
      type="button"
      class="role-menu-btn"
      aria-haspopup="menu"
      aria-controls="role-menu-list"
      :aria-expanded="open"
      @click="toggle"
      @keydown="onTriggerKeydown"
    >
      <span class="visually-hidden">Ansicht für </span
      ><span class="role-menu-name">{{ current?.shortLabel ?? '' }}</span>
      <IconSvg class="role-menu-chevron" name="chevron" :size="16" :stroke-width="2.4" />
    </button>
    <div
      v-if="open"
      id="role-menu-list"
      ref="menu"
      class="role-menu-list"
      role="menu"
      aria-labelledby="role-menu-btn"
      @keydown="onMenuKeydown"
    >
      <button
        v-for="role in roles"
        :key="role.id"
        type="button"
        class="role-menu-item"
        role="menuitemradio"
        :aria-checked="role.id === identity.role"
        tabindex="-1"
        @click="select(role.id)"
      >
        <span class="role-menu-item-text">
          <span class="role-menu-item-name">{{ role.shortLabel }}</span>
          <span class="role-menu-item-sub">{{ role.label }}</span>
        </span>
        <IconSvg
          v-if="role.id === identity.role"
          class="role-menu-check"
          name="check"
          :size="20"
          :stroke-width="2.6"
        />
      </button>
    </div>
  </div>
</template>
