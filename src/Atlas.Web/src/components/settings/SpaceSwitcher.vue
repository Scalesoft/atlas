<script setup lang="ts">
import t from "../../i18n/settings-grants";
import { computed, ref, watch } from "vue";
import type { Space } from "../../api";
import AtlasIcon from "../AtlasIcon.vue";
import { Popover, PopoverContent, PopoverTrigger } from "../ui/popover";
import SearchableChoiceList from "./SearchableChoiceList.vue";
import type { ChoiceOption } from "./types";
import "./settings.css";
import "./settings-tailwind.css";

/**
 * Moves between the spaces the account manages without leaving space settings: a chip in the settings
 * rail names the open space and opens a searchable list of all of them. The shell answers `switch` by
 * opening the same section of the chosen space, behind its unsaved-changes guard.
 *
 * It is navigation, not a form field. Where a choice fills a draft value (a step of the vault wizard,
 * of a grant), put SearchableChoiceList inline in the step instead; the switcher only adds what
 * navigation needs and a field must not have: a chip for the open space, a popover, and browsing
 * first, so a space opens on Enter or a click and never on an arrow key.
 *
 * With one managed space the chip is a plain label; with none nothing renders.
 */
type SwitcherSpace = Pick<Space, "id" | "name" | "icon" | "color">;
const props = defineProps<{ current: SwitcherSpace; spaces: readonly SwitcherSpace[] }>();
const emit = defineEmits<{ switch: [spaceId: string] }>();

const open = ref(false);
/** The space marked while browsing the list; marking opens nothing. */
const marked = ref<string | null>(props.current.id);
// Browsing starts from the open space each time the list opens, and when that space changes under it.
watch([open, () => props.current.id], () => {
  if (open.value) marked.value = props.current.id;
});
const options = computed<ChoiceOption[]>(() =>
  props.spaces.map((space) => ({ value: space.id, label: space.name, icon: space.icon, badge: space.id === props.current.id ? t.spaceSwitcher.open : undefined })),
);

function go(spaceId: string) {
  open.value = false;
  if (spaceId !== props.current.id) emit("switch", spaceId);
}
// reka's RadioGroupItem clicks the item an arrow key moves the focus to, so `pick` also fires while
// browsing. Only a pick that follows a pointer press, or Enter on an item, opens a space.
const pointer = ref(false);
function picked(spaceId: string) {
  if (pointer.value) go(spaceId);
}
function entered(event: KeyboardEvent) {
  const spaceId = (event.target as Element | null)?.closest?.('[role="radio"]')?.getAttribute("data-value");
  if (spaceId) go(spaceId);
}
</script>

<template>
  <Popover v-if="spaces.length > 1" v-model:open="open">
    <PopoverTrigger as-child>
      <!-- The name says what the chip does and keeps the visible text in it, so it is still found by the space's name. -->
      <button type="button" class="space-switcher" data-space-switcher :title="current.name" :aria-label="t.spaceSwitcher.chip(current.name)">
        <span class="space-icon" :data-color="current.color" aria-hidden="true"><AtlasIcon :name="current.icon" :size="14" /></span>
        <span class="space-switcher-name">{{ current.name }}</span>
        <AtlasIcon name="down" :size="14" class="space-switcher-caret" />
      </button>
    </PopoverTrigger>
    <PopoverContent class="settings-ui w-80 max-w-[calc(100vw-24px)] rounded-lg border-0 p-0" align="start" :aria-label="t.spaceSwitcher.switch">
      <div @pointerdown.capture="pointer = true" @keydown.capture="pointer = false" @keydown.enter="entered">
        <SearchableChoiceList v-model="marked" :options="options" :label="t.spaceSwitcher.other" :placeholder="t.spaceSwitcher.search" @pick="picked" />
      </div>
      <p class="st-note space-switcher-help">{{ t.spaceSwitcher.help }}</p>
    </PopoverContent>
  </Popover>
  <div v-else-if="spaces.length" class="space-switcher" data-space-switcher :title="current.name">
    <span class="space-icon" :data-color="current.color" aria-hidden="true"><AtlasIcon :name="current.icon" :size="14" /></span>
    <span class="space-switcher-name">{{ current.name }}</span>
  </div>
</template>

<style scoped>
/* One row of the rail: it takes the width it is given and the name ellipsises, so no width needs a rule of its own. */
.space-switcher {
  display: flex; align-items: center; gap: 8px; width: 100%; min-width: 0; min-height: 40px; padding: 0 8px;
  border: 1px solid var(--line); border-radius: var(--radius-md); background: var(--surface); color: var(--text);
  font: inherit; font-weight: 600; text-align: left;
}
button.space-switcher {
  cursor: pointer;
  transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out), transform var(--atlas-motion-fast) var(--atlas-ease-out);
}
@media (hover: hover) and (pointer: fine) {
  button.space-switcher:hover { border-color: color-mix(in srgb, var(--accent) 45%, var(--line)); }
}
button.space-switcher:active { transform: scale(var(--atlas-press)); }
button.space-switcher:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
button.space-switcher[data-state="open"] { border-color: var(--accent); }
.space-switcher-name { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.space-switcher-caret { flex: none; color: var(--muted); transition: rotate var(--atlas-motion-fast) var(--atlas-ease-out); }
button.space-switcher[data-state="open"] .space-switcher-caret { rotate: 180deg; }
/* The keys of the list, said quietly under it (the text is settings.css's note). */
.space-switcher-help { padding: 8px 12px 10px; }
</style>
