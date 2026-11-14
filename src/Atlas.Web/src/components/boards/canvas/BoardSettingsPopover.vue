<script setup lang="ts">
import t from "../../../i18n/boards";
import { ref } from "vue";
import { PopoverContent, PopoverPortal, PopoverRoot, PopoverTrigger } from "reka-ui";
import { gridSizes, type BoardSettings } from "../../../boards/boardApi";
import { useEditorTheme } from "../../../editor/editorSetup";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import { Switch } from "../../ui/switch";
import "./boardPopover.css";

/**
 * The board's settings (spec §3.7), shared by everyone on it: background, snapping to the grid and its step, the timeline's origin
 * and scale, and „Přejít na…“ a date. A Writer changes them (read-only shows them); each change is one `settings` change.
 */
const props = defineProps<{ settings: BoardSettings; canEdit: boolean }>();
const emit = defineEmits<{ change: [patch: Partial<BoardSettings>]; goTo: [date: string] }>();
/** Open also from outside (the command panel's „Nastavení tabule“). */
const open = defineModel<boolean>("open", { default: false });
const backgrounds: [BoardSettings["background"], string, string][] = [["dots", t.settings.dots, "grip-vertical"], ["lines", t.settings.lines, "table"], ["none", t.settings.none, "ban"], ["timeline", t.settings.timeline, "calendar"]];
const theme = useEditorTheme();
const today = () => new Date().toISOString().slice(0, 10);
const target = ref(today());

function background(value: BoardSettings["background"]) {
  emit("change", { background: value, ...(value === "timeline" && !props.settings.timeline && { timeline: { origin: today(), pxPerDay: 24 } }) });
}
function origin(value: string | number) {
  if (/^\d{4}-\d{2}-\d{2}$/.test(String(value)) && props.settings.timeline) emit("change", { timeline: { ...props.settings.timeline, origin: String(value) } });
}
function scale(value: string | number) {
  const pxPerDay = Number(value);
  if (Number.isFinite(pxPerDay) && pxPerDay >= 0.01 && pxPerDay <= 2000 && props.settings.timeline) emit("change", { timeline: { ...props.settings.timeline, pxPerDay } });
}
</script>

<template>
  <PopoverRoot v-model:open="open">
    <PopoverTrigger as-child>
      <Button variant="ghost" size="icon-sm" type="button" :aria-label="t.settings.title" :title="t.settings.title" data-board-settings-trigger><AtlasIcon name="settings" :size="16" /></Button>
    </PopoverTrigger>
    <PopoverPortal>
      <!-- The page editor's context panel (openMarkdownContextPopover's look): its fields, choices and buttons. -->
      <PopoverContent class="md-editor atlas-document-editor md-context-popover board-settings nodrag nopan nowheel" :class="theme" side="top" align="end" :side-offset="8"
        :collision-padding="8" :aria-label="t.settings.title" data-positioned data-board-settings>
        <div class="md-context-popover-body md-context-fields">
          <!-- A reader sees how the board is set and may still go to a date; why the rest is greyed out, said once on top. -->
          <p v-if="!canEdit" class="md-context-field-wide board-picker-keys" data-board-settings-readonly><AtlasIcon name="lock" :size="13" /> {{ t.settings.readOnly }}</p>
          <div class="md-context-field md-context-field-wide" role="radiogroup" :aria-label="t.settings.background">{{ t.settings.background }}
            <div class="md-context-choices board-settings-backgrounds">
              <button v-for="[value, text, icon] in backgrounds" :key="value" type="button" role="radio" :aria-checked="settings.background === value" :disabled="!canEdit"
                :data-board-background-option="value" @click="background(value)"><AtlasIcon :name="icon" :size="16" />{{ text }}</button>
            </div>
          </div>
          <label class="md-context-field-wide board-settings-row">
            <Switch :model-value="settings.snapToGrid" :disabled="!canEdit" data-board-snap @update:model-value="emit('change', { snapToGrid: !!$event })" />
            {{ t.settings.snap }} <kbd>Ctrl+&apos;</kbd>
          </label>
          <div class="md-context-field md-context-field-wide" role="radiogroup" :aria-label="t.settings.grid">{{ t.settings.grid }}
            <div class="md-context-choices board-settings-sizes">
              <button v-for="size in gridSizes" :key="size" type="button" role="radio" :aria-checked="settings.gridSize === size" :disabled="!canEdit || !settings.snapToGrid"
                @click="emit('change', { gridSize: size })">{{ size }}</button>
            </div>
          </div>
          <template v-if="settings.background === 'timeline' && settings.timeline">
            <label class="md-context-field">{{ t.settings.origin }}<input class="md-input" type="date" :value="settings.timeline.origin" :disabled="!canEdit" @input="origin(($event.target as HTMLInputElement).value)" /></label>
            <label class="md-context-field">{{ t.settings.scale }}<input class="md-input" type="number" min="0.01" max="2000" step="any" :value="settings.timeline.pxPerDay" :disabled="!canEdit"
              @input="scale(($event.target as HTMLInputElement).value)" /></label>
            <div class="md-context-field-wide board-settings-row">
              <input v-model="target" class="md-input" type="date" :aria-label="t.settings.goToDate" />
              <button type="button" class="md-btn" data-board-go-to @click="emit('goTo', target)"><AtlasIcon name="arrow-right" :size="16" />{{ t.settings.goTo }}</button>
              <button type="button" class="md-btn" @click="emit('goTo', today())"><AtlasIcon name="calendar" :size="16" />{{ t.settings.today }}</button>
            </div>
          </template>
        </div>
      </PopoverContent>
    </PopoverPortal>
  </PopoverRoot>
</template>

<style scoped>
.board-settings-backgrounds { grid-template-columns: 1fr 1fr; padding: 0; }
.board-settings-sizes { grid-template-columns: repeat(4, 1fr); padding: 0; }
.board-settings-sizes button { justify-content: center; }
.md-context-choices button:disabled { opacity: .5; cursor: not-allowed; }
/* The editor's checked choice; its bordered body buttons rule would otherwise hide it. */
.md-context-choices button[aria-checked="true"] { border-color: var(--accent-line, var(--accent)); color: var(--accent-ink, var(--accent)); background: var(--accent-soft);
  transition: background-color var(--md-motion-fast), border-color var(--md-motion-fast); }
.board-settings-row { display: flex; align-items: center; gap: 8px; color: var(--text); font-size: 13px; }
/* The editor's rule for buttons in a popover body (border, padding, transparent) drew the switch as an empty box (BD-14). */
.board-settings .board-settings-row :deep([data-slot="switch"]) { padding: 0; border-color: transparent; border-radius: 999px; background: var(--input-line); }
.board-settings .board-settings-row :deep([data-slot="switch"][data-state="checked"]) { background: var(--accent); }
.board-settings-row kbd { margin-left: auto; color: var(--text-dim, var(--muted)); font-size: 11px; }
.board-settings-row .md-input { flex: 1; }
</style>
