<script setup lang="ts">
import { computed, ref, useId, watch } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { formatBytes } from "../../../i18n";
import t from "../../../i18n/settings-backups";
import { disclosureHooks } from "../disclosure";

/**
 * One ZIP file to upload (a Wiki.js export, a backup): a dashed drop target that is also the label
 * of a visually hidden file input, so clicking, the keyboard and dropping a file all work. It shows
 * the chosen file with its size and refuses files over `maxBytes` with the reason below it.
 * The file stays with the parent (v-model); nothing is uploaded here.
 */
const props = withDefaults(defineProps<{ label: string; maxBytes?: number; disabled?: boolean; accept?: string }>(), {
  maxBytes: undefined,
  disabled: false,
  accept: ".zip,application/zip",
});
const file = defineModel<File | null>({ default: null });
const uid = useId();
const over = ref(false);
const error = ref("");
// Re-creating the input clears its FileList, so the same file can be chosen again after a reset.
const inputKey = ref(0);
watch(file, (value) => {
  if (!value) inputKey.value++;
});

const limit = computed(() => (props.maxBytes ? formatBytes(props.maxBytes) : ""));
function take(selected: File | null) {
  error.value = "";
  if (selected && props.maxBytes && selected.size > props.maxBytes) {
    error.value = t.dropzone.tooBig(limit.value);
    file.value = null;
    inputKey.value++;
    return;
  }
  file.value = selected;
}
function choose(event: Event) {
  take((event.target as HTMLInputElement).files?.[0] ?? null);
}
function drop(event: DragEvent) {
  over.value = false;
  if (props.disabled) return;
  const dropped = event.dataTransfer?.files?.[0] ?? null;
  if (dropped) take(dropped);
}
</script>

<template>
  <div class="zip-dropzone-field">
    <label
      :for="`${uid}-file`"
      class="zip-dropzone"
      :data-active="over || undefined"
      :data-filled="file ? 'true' : undefined"
      :data-disabled="disabled || undefined"
      data-zip-dropzone
      @dragover.prevent="over = !disabled"
      @dragleave="over = false"
      @drop.prevent="drop"
    >
      <span class="st-icon-tile zip-dropzone-icon" :data-tone="file ? 'success' : undefined"><AtlasIcon :name="file ? 'file-text' : 'upload'" :size="20" /></span>
      <span class="zip-dropzone-text">
        <strong>{{ file ? file.name : label }}</strong>
        <small>{{ file ? t.dropzone.chooseAnother(formatBytes(file.size)) : limit ? t.dropzone.orClickLimit(limit) : t.dropzone.orClick }}</small>
      </span>
      <input :id="`${uid}-file`" :key="inputKey" type="file" :accept="accept" :aria-label="label" :disabled="disabled" class="zip-dropzone-input" @change="choose" />
    </label>
    <Transition :css="false" v-bind="disclosureHooks">
      <p v-if="error" class="zip-dropzone-error" role="alert"><AtlasIcon name="triangle-alert" :size="14" />{{ error }}</p>
    </Transition>
  </div>
</template>

<style scoped>
.zip-dropzone-field { display: flex; flex-direction: column; gap: 6px; min-width: 0; }
.zip-dropzone {
  position: relative;
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 16px;
  border: 1px dashed var(--line);
  border-radius: var(--st-radius);
  color: var(--text);
  cursor: pointer;
  transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out), background-color var(--atlas-motion-fast) var(--atlas-ease-out);
}
.zip-dropzone:focus-within, .zip-dropzone[data-active] { border-color: var(--accent); background: var(--accent-soft); }
@media (hover: hover) and (pointer: fine) {
  .zip-dropzone:hover:not([data-disabled]) { border-color: var(--accent); background: var(--accent-soft); }
}
.zip-dropzone:focus-within { outline: 2px solid var(--accent); outline-offset: 2px; }
/* The icon tile is the same soft accent as the engaged zone and would vanish into it: it turns to the surface colour. */
.zip-dropzone-icon { transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.zip-dropzone:not([data-filled]):focus-within .zip-dropzone-icon, .zip-dropzone:not([data-filled])[data-active] .zip-dropzone-icon { background: var(--surface); }
@media (hover: hover) and (pointer: fine) {
  .zip-dropzone:not([data-filled], [data-disabled]):hover .zip-dropzone-icon { background: var(--surface); }
}
.zip-dropzone[data-filled] { border-style: solid; }
.zip-dropzone[data-disabled] { cursor: not-allowed; opacity: .6; }
.zip-dropzone-icon { width: 40px; height: 40px; flex-shrink: 0; }
.zip-dropzone-text { display: grid; gap: 2px; min-width: 0; }
.zip-dropzone-text strong { font-size: 14px; overflow-wrap: anywhere; }
.zip-dropzone-text small { color: var(--muted); font-size: 12.5px; }
.zip-dropzone-input { position: absolute; width: 1px; height: 1px; padding: 0; margin: -1px; overflow: hidden; clip: rect(0 0 0 0); white-space: nowrap; border: 0; }
.zip-dropzone-error { display: flex; align-items: center; gap: 6px; margin: 0; color: var(--error); font-size: 12.5px; font-weight: 550; }
</style>
