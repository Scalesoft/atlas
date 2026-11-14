<script setup lang="ts">
import settingsSearch from "../../i18n/settings-search";
import { ref } from "vue";
import { clearOnEscape, ownEscape } from "../../lib/searchKeys";
import AtlasIcon from "../AtlasIcon.vue";

/**
 * The one filter field of settings lists (Účty, Skupiny, Přístupy, import): a search icon inside the
 * field, a clear button once something is typed, the first Esc clears (the next one goes on to what closes around it, P3). It filters what is already shown;
 * choosing from a long list is SearchableChoiceList, several people at once SearchableTags.
 * Attributes (data-*, name, id) go to the input.
 */
defineOptions({ inheritAttrs: false });
const props = withDefaults(defineProps<{ label: string; placeholder?: string; wide?: boolean; disabled?: boolean; readonly?: boolean }>(), { placeholder: undefined, wide: false, disabled: false, readonly: false });
const model = defineModel<string>({ default: "" });
const input = ref<HTMLInputElement>();
function clear() {
  if (props.disabled || props.readonly) return;
  model.value = "";
  input.value?.focus();
}
/** The first Esc empties the field and stays here; the next one goes on to whatever closes around it (P3). */
function onKeydown(event: KeyboardEvent) {
  if (!props.disabled && !props.readonly) clearOnEscape(event, model.value, clear);
}
</script>

<template>
  <label class="search-field" :class="{ 'is-wide': wide }">
    <AtlasIcon name="search" :size="16" class="search-field-icon" />
    <input ref="input" v-model="model" v-bind="$attrs" class="search-field-input" type="search" autocomplete="off" :disabled="disabled" :readonly="readonly" :aria-label="label" :placeholder="placeholder ?? label" :data-md-own-escape="ownEscape(model)" @keydown="onKeydown" />
    <Transition name="st-fade">
      <button v-if="model && !readonly" type="button" class="search-field-clear" :disabled="disabled" :aria-label="settingsSearch.searchField.clear(label)" @click="clear"><AtlasIcon name="close" :size="14" /></button>
    </Transition>
  </label>
</template>

<style scoped>
.search-field { position: relative; display: block; width: 100%; max-width: 420px; }
.search-field.is-wide { max-width: none; }
.search-field-icon { position: absolute; top: 50%; left: 11px; translate: 0 -50%; color: var(--muted); pointer-events: none; transition: color var(--atlas-motion-fast); }
.search-field:focus-within .search-field-icon { color: var(--accent); }
/* Self-contained, so it works in the wiki workspace as well as in settings; the look matches .st-input. */
.search-field-input {
  width: 100%; min-width: 0; min-height: 36px; padding: 6px 34px;
  border: 1px solid var(--input-line); border-radius: var(--atlas-radius-md);
  background: var(--surface); color: var(--text); font: inherit; font-size: 13.5px;
  transition: border-color var(--atlas-motion-fast), box-shadow var(--atlas-motion-fast);
}
.search-field-input::placeholder { color: var(--muted); }
.search-field-input:focus-visible { outline: var(--state-focus-ring); outline-offset: var(--state-focus-offset); }
.search-field-input:disabled { opacity: .6; cursor: not-allowed; }
.search-field-input::-webkit-search-cancel-button { appearance: none; }
.search-field-clear {
  position: absolute; top: 50%; right: 6px; translate: 0 -50%;
  display: grid; place-items: center; width: 26px; height: 26px;
  border: 0; border-radius: 999px; background: transparent; color: var(--muted); cursor: pointer;
  transition: background-color var(--atlas-motion-fast), color var(--atlas-motion-fast);
}
@media (hover: hover) and (pointer: fine) {
  .search-field-clear:hover:not(:disabled) { background: var(--hover); color: var(--text); }
}
.search-field-clear:disabled { opacity: .6; cursor: not-allowed; }
.search-field-clear:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; --focus-offset: 1px; }
</style>
