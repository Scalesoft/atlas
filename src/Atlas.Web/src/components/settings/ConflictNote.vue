<script setup lang="ts">
import t from "../../i18n/settings-shell";
import AtlasIcon from "../AtlasIcon.vue";
import { Button } from "../ui/button";

/**
 * Someone else changed the same thing meanwhile (HTTP 409), said next to what failed. Nothing is
 * retried or replaced silently:
 *
 * - an immediate action (a grant, a toggle) has no draft, so the page has already reloaded the
 *   current state and only says so;
 * - a draft page (SaveBar) keeps the user's draft and offers `reloadLabel` ("Načíst znovu"), which
 *   discards the draft and shows the current state.
 *
 * Use ErrorState for a failure that needs a retry, ResultBanner for a success.
 */
withDefaults(defineProps<{ text: string; dismissible?: boolean; reloadLabel?: string; busy?: boolean }>(), {
  dismissible: true,
  reloadLabel: undefined,
  busy: false,
});
const emit = defineEmits<{ dismiss: []; reload: [] }>();
</script>

<template>
  <p class="conflict-note" role="alert" data-conflict-note>
    <AtlasIcon name="triangle-alert" :size="15" />
    <span>{{ text }}</span>
    <span v-if="reloadLabel" class="conflict-note-actions">
      <Button size="sm" variant="outline" type="button" :disabled="busy" data-conflict-reload @click="emit('reload')">{{ reloadLabel }}</Button>
    </span>
    <button v-if="dismissible" type="button" class="conflict-note-close" :aria-label="t.dismissNote" @click="emit('dismiss')"><AtlasIcon name="close" :size="15" /></button>
  </p>
</template>

<style scoped>
.conflict-note {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  margin: 0;
  padding: 10px 12px;
  border: 1px solid color-mix(in srgb, var(--warning) 45%, var(--line));
  border-radius: var(--st-radius);
  background: color-mix(in srgb, var(--warning-bg) 60%, var(--surface));
  font-size: 13.5px;
  font-weight: 550;
  line-height: 1.5;
  animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out);
}
.conflict-note > svg { flex-shrink: 0; margin-top: 3px; color: var(--warning); }
.conflict-note > span { flex: 1; min-width: 0; }
.conflict-note > .conflict-note-actions { display: flex; flex: 0 0 auto; flex-wrap: wrap; gap: 6px; margin-top: -3px; }
.conflict-note-close { display: grid; place-items: center; flex-shrink: 0; width: 24px; height: 24px; border: 0; border-radius: 6px; color: var(--muted); background: transparent; cursor: pointer; }
@media (hover: hover) and (pointer: fine) {
  .conflict-note-close:hover { color: var(--text); background: var(--hover); }
}
.conflict-note-close:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; --focus-offset: 1px; }
</style>
