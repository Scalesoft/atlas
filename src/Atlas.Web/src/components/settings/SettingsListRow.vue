<script setup lang="ts">
import CommandIcon from "../CommandIcon.vue";
import type { StatusTone } from "./types";

/**
 * One row of a settings list: leading icon (or `#leading`, e.g. a SubjectAvatar), title and a
 * line of detail, then `#meta` (badges) and `#actions`. The default slot renders below the row,
 * for an InlineReveal with details or an inline confirmation. `highlighted` marks a row a flow has
 * just created; it rises in and glows once.
 */
withDefaults(defineProps<{ title: string; description?: string; icon?: string; iconTone?: StatusTone; highlighted?: boolean }>(), {
  description: undefined,
  icon: undefined,
  iconTone: undefined,
  highlighted: false,
});
</script>

<template>
  <li class="settings-list-row" :data-new="highlighted || undefined">
    <div class="settings-list-row-line">
      <div class="settings-list-row-main">
        <slot name="leading">
          <span v-if="icon" class="st-icon-tile settings-list-row-icon" :data-tone="iconTone ?? 'neutral'"><CommandIcon :name="icon" :size="17" /></span>
        </slot>
        <span class="settings-list-row-text">
          <strong><slot name="title">{{ title }}</slot></strong>
          <small v-if="description || $slots.description"><slot name="description">{{ description }}</slot></small>
        </span>
      </div>
      <div v-if="$slots.meta" class="settings-list-row-meta">
        <slot name="meta" />
      </div>
      <div v-if="$slots.actions" class="settings-list-row-actions">
        <slot name="actions" />
      </div>
    </div>
    <slot />
  </li>
</template>

<style scoped>
/* One row of a settings list: who or what · its state (badges) · actions. The state has its own
   column so actions line up on the right in every row, however many badges a row carries; in a
   narrow list the state moves under the title. */
.settings-list-row { min-width: 0; container-type: inline-size; padding-inline: var(--st-row-pad-x, 0px); border-top: 1px solid var(--st-line-soft, color-mix(in srgb, var(--line) 75%, transparent)); }
.settings-list-row:first-child { border-top: 0; }
.settings-list-row-line { display: grid; grid-template-columns: minmax(0, 1fr) auto auto; align-items: center; gap: 8px 14px; min-height: 64px; padding: 12px 0; min-width: 0; }
/* Without actions the empty third track still took its 14 px gap, so a meta-only row ended its badges short of the card inset. */
.settings-list-row-line:not(:has(> .settings-list-row-actions)) { grid-template-columns: minmax(0, 1fr) auto; }
/* The tile and the gap are those of the card header and of a row with an icon (32 px, 12 px), so a header and its rows share one title line. */
.settings-list-row-main { grid-column: 1; grid-row: 1; display: flex; align-items: center; gap: 12px; min-width: 0; }
.settings-list-row-icon { width: 32px; height: 32px; border-radius: 10px; }
.settings-list-row-text { display: grid; gap: 2px; min-width: 0; }
.settings-list-row-text strong { font-size: 14px; font-weight: 600; line-height: 1.4; overflow-wrap: anywhere; }
.settings-list-row-text small { color: var(--muted); font-size: 13px; line-height: 1.45; overflow-wrap: anywhere; }
.settings-list-row-meta { grid-column: 2; grid-row: 1; display: flex; flex-wrap: wrap; align-items: center; justify-content: flex-end; gap: 6px 8px; min-width: 0; max-width: 380px; }
.settings-list-row-actions { grid-column: 3; grid-row: 1; display: flex; flex-wrap: wrap; align-items: center; justify-content: flex-end; gap: 4px; min-width: 0; }
/* Switch before badges and actions consume the entire title column. */
@container (max-width: 600px) {
  .settings-list-row-line { grid-template-columns: minmax(0, 1fr) auto; }
  .settings-list-row-actions { grid-column: 2; }
  .settings-list-row-meta { grid-column: 1 / -1; grid-row: 2; justify-content: flex-start; max-width: none; padding-left: 44px; }
  /* The badges start under the title: a row led by a person's or a group's avatar (36 px) starts it 4 px further in than the 32 px tile. */
  .settings-list-row:has(.settings-list-row-main > .subject-avatar) .settings-list-row-meta { padding-left: 48px; }
}
/* A phone or side panel: each part gets a full line. Sharing a narrow line gave the badges
   zero content width whenever there was more than one action. */
@container (max-width: 340px) {
  .settings-list-row-main { grid-column: 1 / -1; }
  .settings-list-row-meta { grid-column: 1 / -1; grid-row: 2; padding-left: 0; }
  .settings-list-row-actions { grid-column: 1 / -1; grid-row: 3; justify-content: flex-start; }
  .settings-list-row-line:not(:has(> .settings-list-row-meta)) .settings-list-row-actions { grid-row: 2; }
}
</style>
