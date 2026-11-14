<script setup lang="ts">
import t from "../../i18n/components-library";
import { computed, ref } from "vue";
import { RadioGroupItem, RadioGroupRoot } from "reka-ui";
import type { Space } from "../../api";
import type { SpaceCover, SpaceCoverChoice } from "../../spaceCover";
import { findSpaceCovers, spaceCoverEntries, spaceCoverGroupOptions, spaceCoverName } from "../../spaceCoverCatalog";
import { spaceCoverDrawingUrl } from "../../spaceCoverDrawings";
import { focusFirst } from "../../lib/pickEnter";
import { useSearchKeys } from "../../lib/searchKeys";
import AtlasIcon from "../AtlasIcon.vue";
import OptionPills from "../settings/OptionPills.vue";
import SearchField from "../settings/SearchField.vue";
import SpaceCoverImage from "./SpaceCoverImage.vue";

/**
 * The image of a space chosen by looking: every built-in drawing as a thumbnail in the space's own colours, after
 * the two choices that are no drawing, Automaticky (the server picks by name, description and icon) and Bez obrázku.
 * A search over names, tags and stems and the four groups narrow it. One radio group: one Tab stop, arrows move.
 *
 * Arrows also select, and the owner saves a choice at once, so every `choose` says how it was made: `settle` is true
 * for the keyboard, and `settled` follows when focus leaves the grid. The owner then saves a walk by keyboard once,
 * where it rests, and a click at once. `confirm` is Enter or Space on an option, with that option: the owner waits for
 * it where a choice by keyboard must not be saved on its own (it would replace an uploaded image). The option the key
 * is on need not be the checked one: Home, End and, in a browser, often an arrow move focus without checking.
 *
 * Choose it over SearchableChoiceList when the options are told apart by their picture, not by a row of text, and
 * over AppearancePicker for anything but the icons and colours of the appearance catalog, which the parent's form
 * saves. It holds no state of the choice: `cover` is what is checked.
 */
const props = defineProps<{ space: Pick<Space, "id" | "icon" | "color">; cover: Pick<SpaceCover, "source" | "catalogKey">; autoKey?: string | null; disabled?: boolean }>();
const emit = defineEmits<{ choose: [choice: SpaceCoverChoice, settle: boolean]; settled: []; confirm: [choice: SpaceCoverChoice] }>();

// A colon cannot occur in a catalog key.
const AUTO = ":auto";
const NONE = ":none";
const query = ref("");
const group = ref("");
const filtered = computed(() => !!query.value.trim() || !!group.value);
const found = computed(() => findSpaceCovers(query.value, group.value));
// An upload is none of the options: "", not undefined, with which the group would keep the choice itself and stay
// silent about a click on the option it holds.
const checked = computed(() => (props.cover.source === "Auto" ? AUTO : props.cover.source === "None" ? NONE : props.cover.source === "Catalog" ? props.cover.catalogKey ?? "" : ""));
// Without the stored image, so every thumbnail is its own drawing in the space's colours.
const thumbnailSpace = computed(() => ({ id: props.space.id, icon: props.space.icon, color: props.space.color }));
const autoName = computed(() => spaceCoverName(props.autoKey));

// Whether the last input on the grid was a key (true) or a pointer.
let keyboard = false;
function inputBy(key: boolean) {
  keyboard = key;
}
function choiceOf(key: string): SpaceCoverChoice {
  return key === AUTO ? { source: "Auto" } : key === NONE ? { source: "None" } : { source: "Catalog", catalogKey: key };
}
function pick(value: unknown) {
  const key = String(value);
  // Space clicks the option it is on: one that is checked already is no new choice.
  if (keyboard && key === checked.value) return;
  emit("choose", choiceOf(key), keyboard);
}
/** Enter or Space, with the option it was pressed on. A key that is held down repeats, which is no second press. */
function confirm(event: KeyboardEvent) {
  const key = event.repeat ? null : (event.target as Element | null)?.closest?.('[role="radio"]')?.getAttribute("data-value");
  if (key) emit("confirm", choiceOf(key));
}
/** The search's first drawing: Enter in the field chooses it as a click would, Tab as an arrow does (it waits for Enter over an uploaded image), Down enters the grid on it, the first Esc empties the search (P3, P8). */
const first = computed(() => (query.value.trim() ? found.value[0]?.id : undefined));
// Enter is a deliberate choice, as a click; Tab only takes the drawing on its way out, so over an uploaded image it waits for Enter like an arrow does (SpaceCoverField).
let tabbed = false;
const keys = useSearchKeys({ text: () => query.value, clear: () => { query.value = ""; },
  take: () => { if (!first.value || props.disabled) return false; emit("choose", choiceOf(first.value), tabbed); } });
function searchKeys(event: KeyboardEvent) {
  tabbed = event.key === "Tab";
  return keys.onKeydown(event);
}
const options = ref<{ $el: HTMLElement }>();
const enterOptions = () => focusFirst(options.value?.$el, '[role="radio"]');
function focusLeft(event: FocusEvent) {
  if (!(event.currentTarget as HTMLElement).contains(event.relatedTarget as Node | null)) emit("settled");
}
</script>

<template>
  <div class="space-cover-gallery" data-space-cover-gallery>
    <div class="space-cover-gallery-filters">
      <SearchField v-model="query" :label="t.gallery.search" :placeholder="t.gallery.searchFrom(spaceCoverEntries.length)" :disabled="disabled" data-space-cover-search @keydown="searchKeys" @keydown.down.prevent="enterOptions" />
      <OptionPills v-model="group" :options="spaceCoverGroupOptions" :label="t.gallery.groups" hide-label :disabled="disabled" data-space-cover-groups />
    </div>
    <!-- Captured, so the way a choice is made is known before the group reports it. -->
    <RadioGroupRoot
      ref="options"
      :model-value="checked"
      class="space-cover-options"
      :aria-label="t.gallery.options"
      :disabled="disabled"
      loop
      data-space-cover-options
      @keydown.capture="inputBy(true)"
      @keydown.enter.space="confirm"
      @pointerdown.capture="inputBy(false)"
      @focusout="focusLeft"
      @update:model-value="pick"
    >
      <template v-if="!filtered">
        <RadioGroupItem :value="AUTO" class="space-cover-option" :data-value="AUTO" :title="autoName ? t.gallery.autoTitle(autoName) : t.gallery.autoTitleUnknown">
          <SpaceCoverImage class="space-cover-thumbnail" :space="thumbnailSpace" :src="spaceCoverDrawingUrl(autoKey)" instant :mark-size="72" />
          <span class="space-cover-option-name">{{ t.gallery.auto }}</span>
        </RadioGroupItem>
        <RadioGroupItem :value="NONE" class="space-cover-option" :data-value="NONE" :title="t.gallery.noneTitle">
          <SpaceCoverImage class="space-cover-thumbnail" :space="thumbnailSpace" instant :mark-size="72" />
          <span class="space-cover-option-name">{{ t.gallery.none }}</span>
        </RadioGroupItem>
      </template>
      <RadioGroupItem v-for="entry in found" :key="entry.id" :value="entry.id" class="space-cover-option" :data-value="entry.id" :data-first="entry.id === first || undefined" :title="entry.name">
        <SpaceCoverImage class="space-cover-thumbnail" :space="thumbnailSpace" :src="spaceCoverDrawingUrl(entry.id)" instant :mark-size="72" />
        <span class="space-cover-option-name">{{ entry.name }}</span>
      </RadioGroupItem>
      <p v-if="!found.length" class="space-cover-empty" role="status"><AtlasIcon name="search" :size="15" />{{ t.gallery.empty }}</p>
    </RadioGroupRoot>
  </div>
</template>

<style scoped>
.space-cover-gallery { display: grid; gap: 10px; min-width: 0; }
.space-cover-gallery-filters { display: flex; flex-wrap: wrap; align-items: center; gap: 8px 12px; min-width: 0; }
.space-cover-gallery-filters > :deep(.search-field) { flex: 1 1 220px; }
/* Its own scroll: about a hundred thumbnails must not push the rest of the page away. */
.space-cover-options {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(132px, 1fr));
  align-content: start;
  gap: 8px;
  max-height: min(440px, 60vh);
  min-width: 0;
  overflow-y: auto;
  padding: 2px 6px 8px 2px;
  scrollbar-width: thin;
  scrollbar-gutter: stable;
}
/* Thumbnails outside the scrolled view are neither laid out nor painted; they are also asked for lazily. Until then
   an option keeps the content height it has in the narrowest column, so the scrollbar does not jump as rows render:
   a thumbnail of (132 − 10) × 11 / 32, the 4 px gap and a label line of 12 × 1.55 + 2 px. */
.space-cover-option {
  display: grid;
  gap: 4px;
  min-width: 0;
  padding: 4px;
  border: 1px solid var(--line);
  /* The thumbnail (7 px) sits 5 px in (1 px border + 4 px padding): 7 + 5 keeps the two corners concentric. */
  border-radius: 12px;
  background: var(--surface);
  color: var(--text);
  font: inherit;
  font-size: 12px;
  text-align: left;
  cursor: pointer;
  content-visibility: auto;
  contain-intrinsic-size: auto 67px;
  transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out), box-shadow var(--atlas-motion-fast) var(--atlas-ease-out), scale var(--atlas-motion-fast) var(--atlas-ease-out);
}
@media (hover: hover) and (pointer: fine) {
  .space-cover-option:hover:not([data-disabled]) { border-color: color-mix(in srgb, var(--accent) 50%, var(--line)); }
}
.space-cover-option:active:not([data-disabled]) { scale: var(--atlas-press); }
/* Inside: the grid clips what its options draw outside themselves. */
.space-cover-option:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
.space-cover-option[data-state="checked"] { border-color: var(--accent); box-shadow: inset 0 0 0 1px var(--accent); }
.space-cover-option[data-disabled] { cursor: not-allowed; opacity: .6; }
/* The drawing Enter in the search chooses, while the search has the focus. */
.space-cover-gallery:has([data-space-cover-search]:focus) .space-cover-option[data-first]:not([data-state="checked"]) { border-color: color-mix(in srgb, var(--accent) 50%, var(--line)); }
.space-cover-thumbnail { aspect-ratio: 32 / 11; border-radius: 7px; }
.space-cover-option-name { overflow: hidden; padding: 0 2px 2px; text-overflow: ellipsis; white-space: nowrap; }
.space-cover-empty { display: flex; grid-column: 1 / -1; align-items: center; gap: 8px; margin: 8px 0; color: var(--muted); font-size: 13px; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
</style>
