<script setup lang="ts">
import t from "../i18n/components-b";
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue';
import { lazyComponent } from '../lazyComponent';
import { Button } from './ui/button';
import { api, ApiError, errorMessage } from '../api';
import type { PageDecoration } from '../pageDecorations';
import AtlasIcon from './AtlasIcon.vue';
import InlineReveal from './settings/InlineReveal.vue';
import '../page-icons.css';
// The picker (the whole icon catalog) opens on demand, so it stays out of the eager bundle. Pointing at or
// focusing the button fetches it, so the click itself does not wait.
const loadPicker = () => import('./settings/AppearancePicker.vue');
const AppearancePicker = lazyComponent(loadPicker);

/**
 * The icon of a page from the shared appearance catalog (AppearancePicker, variant ink): every icon
 * of the set with search and 60 colours. It unfolds in place under the page title instead of a
 * dialog (owner, 2026-09-28) and saves only on Použít (Ctrl+Enter, P5); the page content does not change.
 */
const props = defineProps<{ pageId: string; userId: string; canEdit: boolean; decoration?: PageDecoration | null; title?: string }>();
const emit = defineEmits<{ saved: [decoration: PageDecoration | null] }>();
const open = ref(false), busy = ref(false), conflict = ref(false), error = ref('');
const icon = ref<string | null>(null), color = ref<string | null>(null), original = ref<PageDecoration | null>(null);
const panel = ref<HTMLElement>();
let request: AbortController | undefined, generation = 0, refreshAfterInterrupt = false;
// The picker needs a value; a page without an icon shows none selected until one is picked.
const pickIcon = computed({ get: () => icon.value ?? '', set: (value: string) => { icon.value = value || null; color.value ??= 'slate-2'; } });
const pickColor = computed({ get: () => color.value ?? 'slate-2', set: (value: string) => { color.value = value; } });
const changed = computed(() => icon.value !== (original.value?.icon ?? null) || color.value !== (original.value?.color ?? null));
function copySelection(value: PageDecoration | null | undefined) {
  original.value = value ?? null; icon.value = value?.icon ?? null; color.value = value?.color ?? null;
}
async function show() {
  if (!props.canEdit || busy.value) return;
  if (open.value) { close(); return; }
  copySelection(props.decoration); error.value = ''; conflict.value = false; open.value = true;
  await nextTick(); panel.value?.querySelector<HTMLInputElement>('[data-appearance-search]')?.focus();
}
function close() { if (!busy.value) open.value = false; }
function clear() { icon.value = null; color.value = null; }
async function save() {
  if (!props.canEdit || busy.value || !changed.value || conflict.value) return;
  const controller = request = new AbortController(), current = ++generation;
  busy.value = true; error.value = '';
  try {
    const result = await api<{ decoration: PageDecoration | null }>(`/pages/${props.pageId}/decoration`, {
      method: 'PUT', signal: controller.signal,
      body: JSON.stringify({ icon: icon.value, color: color.value, expectedRevisionId: original.value?.revisionId ?? null }),
    });
    if (current !== generation || controller.signal.aborted) return;
    emit('saved', result.decoration); open.value = false;
  } catch (failure) {
    if (current !== generation || controller.signal.aborted) return;
    error.value = errorMessage(failure); conflict.value = failure instanceof ApiError && failure.status === 409;
  } finally { if (current === generation) busy.value = false; }
}
async function reload() {
  const controller = request = new AbortController(), current = ++generation;
  busy.value = true; error.value = '';
  try {
    const result = await api<{ decoration: PageDecoration | null }>(`/pages/${props.pageId}/decoration`, { signal: controller.signal });
    if (current !== generation || controller.signal.aborted) return;
    copySelection(result.decoration); emit('saved', result.decoration); conflict.value = false; refreshAfterInterrupt = false;
  } catch (failure) { if (current === generation && !controller.signal.aborted) error.value = errorMessage(failure); }
  finally { if (current === generation) busy.value = false; }
}
function stop() { generation++; request?.abort(); busy.value = false; open.value = false; }
watch([() => props.pageId, () => props.userId], () => { stop(); error.value = ''; refreshAfterInterrupt = false; });
watch(() => props.canEdit, value => {
  if (!value) { refreshAfterInterrupt ||= busy.value; stop(); }
  else if (refreshAfterInterrupt) void reload();
});
onBeforeUnmount(stop);
</script>
<template>
  <button v-if="canEdit" type="button" class="page-icon-edit" :aria-expanded="open" :disabled="busy" @pointerenter="loadPicker" @focus="loadPicker" @click="show"><AtlasIcon name="palette" :size="14" />{{ decoration?.icon ? t.icon.change : t.icon.add }}</button>
  <span v-if="error && !open" class="page-icon-error" role="alert">{{ error }} <Button variant="outline" type="button" :disabled="busy || !canEdit" @click="reload">{{ t.icon.retry }}</Button></span>
  <InlineReveal :open="open">
    <section ref="panel" class="page-icon-picker settings-ui" :aria-label="t.icon.pageIcon" @keydown.esc.stop="close" @keydown.ctrl.enter.prevent="save">
      <AppearancePicker v-model:icon="pickIcon" v-model:color="pickColor" variant="ink" :name="title || t.icon.sample" :description="icon ? t.icon.customIcon : t.icon.noIcon" :icons-label="t.icon.pageIcon" :disabled="busy" />
      <p v-if="error" class="page-icon-failure" role="alert"><AtlasIcon name="triangle-alert" :size="15" />{{ error }}</p>
      <div class="page-icon-actions">
        <Button variant="default" type="button" class="page-icon-save" :disabled="busy || !changed || conflict" @click="save"><AtlasIcon name="check" :size="15" />{{ busy ? t.icon.saving : t.icon.apply }}</Button>
        <Button variant="ghost" type="button" class="page-icon-cancel" :disabled="busy" @click="close">{{ t.icon.cancel }}</Button>
        <Button variant="outline" type="button" class="page-icon-clear" :disabled="busy || !icon" @click="clear"><AtlasIcon name="close" :size="14" />{{ t.icon.clear }}</Button>
        <Button variant="outline" v-if="conflict" type="button" class="page-icon-reload" :disabled="busy" @click="reload"><AtlasIcon name="refresh" :size="14" />{{ t.icon.reload }}</Button>
        <span class="page-icon-help">{{ t.icon.help }}</span>
      </div>
    </section>
  </InlineReveal>
</template>
