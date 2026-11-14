<script setup lang="ts">
import { useId } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import CommandIcon from "../CommandIcon.vue";

// One settings page: title with an optional icon tile, a sentence of purpose, optional actions
// (a module's switch, a primary button) and a back link. Refresh 2.5 (boards 06–10): plain pages
// (Přehled, Lidé) carry no icon; module pages show their mark in a 44 px tile beside the title.
// Pages enter with the shared rise-and-fade; the parent keys the page by its route.
withDefaults(
  defineProps<{ title: string; description?: string; icon?: string; iconTone?: string; back?: { href: string; label: string } }>(),
  { description: undefined, icon: undefined, iconTone: undefined, back: undefined },
);
const titleId = `settings-page-${useId()}`;
</script>

<template>
  <section class="settings-page" :aria-labelledby="titleId">
    <a v-if="back" class="st-link settings-page-back" :href="back.href"><AtlasIcon name="back" :size="15" />{{ back.label }}</a>
    <header class="settings-page-header" :data-has-icon="icon ? 'true' : undefined">
      <span v-if="icon" class="st-icon-tile settings-page-tile" :data-tone="iconTone"><CommandIcon :name="icon" :size="22" class="settings-page-icon" /></span>
      <div class="settings-page-heading">
        <h2 :id="titleId">{{ title }}</h2>
        <p v-if="description">{{ description }}</p>
        <slot name="meta" />
      </div>
      <div v-if="$slots.actions" class="settings-page-actions"><slot name="actions" /></div>
    </header>
    <div class="settings-page-body"><slot /></div>
  </section>
</template>

<style scoped>
.settings-page { min-width: 0; animation: st-rise-in var(--atlas-motion-slow) var(--atlas-ease-out) backwards; }
.settings-page-back { margin-bottom: 12px; font-size: 13px; }
@media (hover: hover) and (pointer: fine) {
  .settings-page-back:hover > svg { translate: calc(var(--atlas-lift) * -1.5) 0; }
}
.settings-page-header { display: flex; flex-wrap: wrap; align-items: center; gap: 12px 14px; margin-bottom: 22px; }
.settings-page-header:not([data-has-icon]) { align-items: flex-start; }
.settings-page-tile { width: 44px; height: 44px; border-radius: 12px; align-self: flex-start; }
.settings-page-tile[data-tone="neutral"] { color: var(--text); }
.settings-page-heading { display: grid; gap: 4px; min-width: 0; flex: 1 1 min(240px, calc(100% - 58px)); overflow-wrap: anywhere; }
.settings-page-heading h2 { margin: 0; font-size: 24px; line-height: 1.25; font-weight: 650; letter-spacing: -.015em; overflow-wrap: anywhere; }
.settings-page-heading p { margin: 0; max-width: 760px; color: var(--muted); font-size: 14px; line-height: 1.5; }
.settings-page-actions { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.settings-page-body { display: grid; gap: 18px; min-width: 0; }
@media (max-width: 680px) {
  .settings-page-header { margin-bottom: 18px; }
  .settings-page-heading h2 { font-size: 21px; }
  .settings-page-tile { width: 38px; height: 38px; border-radius: 10px; }
}
</style>
