<script lang="ts">
import type { PageTag } from "../pageTags";
/** The answers of the tag catalog by typed text, kept for the session per user (P17): the dialog of any page opens with the rows an earlier one showed, and the request only refines them. */
const known = new Map<string, Map<string, PageTag[]>>();
</script>

<script setup lang="ts">
import t from "../i18n/components-b";
import { computed, nextTick, onBeforeUnmount, ref, useId, watch, type ComponentPublicInstance } from 'vue';
import { Button } from './ui/button';
import { Input } from './ui/input';
import { Label } from "./ui/label";
import { fieldElement } from '../lib/templateRef';
import { api, ApiError, errorMessage } from '../api';
import { animateAtlas, motionMs } from '../motion/aiMotion';
import { cleanTagName, tagKey, tagTone } from '../pageTags';
import {useAiCapabilities} from "../composables/useAiCapabilities";
import {proposeTags} from "../ai/writingApi";
import TagSuggestions from "./creation/TagSuggestions.vue";
import AppDialog from './AppDialog.vue';
import AtlasIcon from './AtlasIcon.vue';
import LoadingNote from './settings/LoadingNote.vue';
import LoadingSkeleton from './LoadingSkeleton.vue';
import { createDebouncedLookup } from '../lib/debouncedLookup';
import { foldText } from '../lib/foldText';
import { useSearchKeys } from '../lib/searchKeys';
import { useToast } from '../composables/useToast';
import '../page-tags.css';
/**
 * The tags of a page and their editor (P5: tags save as they go, no Uložit). A tag added or removed in the dialog is sent at once and
 * shown at once (optimistic); a refused save puts the tags back with the reason, a tag removed by mistake comes back with Ctrl+Z.
 * The field is every search's (searchKeys.ts): the first match is preselected once something is typed, Enter or Tab takes it
 * (before the answer to the typed text came it waits for it), an empty field takes nothing, the first Esc empties the field and the
 * next one closes the dialog, keeping the tags. The suggestions of an earlier answer show at once and the network only refines them.
 */
// `initial`: the tags that came with the page (its bundle, 3.6-G N7), so opening it asks for nothing more.
/** `canWrite` false: the reader has no right to write here, so the control that would add tags is not drawn at all (omitted: as before, where the locked placeholder keeps the row's height). */
const props = defineProps<{ pageId: string; userId: string; canEdit: boolean; canWrite?: boolean; pageVersion?: number; initial?: readonly PageTag[] | null }>();
const {capabilities}=useAiCapabilities();
const toaster = useToast();
const tagContextKey=computed(()=>`${props.pageId}:${props.pageVersion ?? ""}`);
function aiTagRequest(signal:AbortSignal){return proposeTags(props.pageId,props.pageVersion!,signal);}
function applyAiTags(values:PageTag[]){const next=[...draft.value];for(const tag of values){if(next.length>=20)break;const name=cleanTagName(tag.name);if(name && !next.some(value=>tagKey(value)===tagKey(name)))next.push(name);}if(next.length!==draft.value.length)commit(next);}
const emit = defineEmits<{ selectTag: [tag: PageTag] }>();
const tags = ref<PageTag[]>([]), draft = ref<string[]>([]), suggestions = ref<PageTag[]>([]);
const loading = ref(false), saving = ref(false), editing = ref(false), loaded = ref(false), suggestPending = ref(false);
const error = ref(''), editError = ref(''), suggestError = ref(''), text = ref(''), index = ref(0);
const input = ref<ComponentPublicInstance>();
const listId = `page-tag-options-${useId()}`;
let request: AbortController | undefined, generation = 0;
let refreshAfterInterruptedSave = false;
/** The answers by typed text (the earlier ones show at once, a new one refines them) and the steps Ctrl+Z and Ctrl+Y walk. */
const answers = () => { let own = known.get(props.userId); if (!own) known.set(props.userId, own = new Map()); return own; };
let undone: string[][] = [], redone: string[][] = [];
const cleanInput = computed(() => cleanTagName(text.value));
/** The folded text `suggestions` answers: another text is on its way, and the rows shown meanwhile are the earlier answer's that still match. */
const answered = ref('');
// The rows of an earlier answer that still match what is typed stay while the next answer is on its way (no blank flash, P17).
const options = computed(() => {
  const typed = foldText(cleanInput.value), stale = answered.value !== typed;
  return suggestions.value.filter(tag => !draft.value.some(name => tagKey(name) === tagKey(tag.name)) && (!stale || !typed || foldText(tag.name).includes(typed)));
});
/** The row marked as the one Enter takes: with nothing typed none, until the arrows or the pointer chose one (searchKeys.lit). */
const highlighted = computed(() => (searchKeys.lit.value ? index.value : -1));
const createName = computed(() => cleanInput.value && !draft.value.some(name => tagKey(name) === tagKey(cleanInput.value)) && !options.value.some(tag => tagKey(tag.name) === tagKey(cleanInput.value)) ? cleanInput.value : '');
const optionNames = computed(() => [...options.value.map(tag => tag.name), ...(createName.value ? [createName.value] : [])]);
const changed = computed(() => {
  const original = tags.value.map(tag => tagKey(tag.name)).sort();
  return JSON.stringify(original) !== JSON.stringify(draft.value.map(tagKey).sort());
});
const root = ref<HTMLElement>();
/**
 * Tags arrive a moment after the page and may need another line of the metadata row that hosts them. The row eases
 * to its new height, so the article below is moved there instead of jumping.
 */
async function settleRow(apply: () => void) {
  const row = root.value?.parentElement, before = row?.offsetHeight ?? 0;
  apply();
  await nextTick();
  if (row?.isConnected && before && row.offsetHeight !== before) {
    animateAtlas(row, [{ height: `${before}px`, overflow: 'clip' }, { height: `${row.offsetHeight}px`, overflow: 'clip' }], { duration: motionMs(row, '--atlas-motion-base'), fill: 'none' });
  }
}
/**
 * The dialog is centred, so a list that arrives a moment after it opened, collapses to one row on a keystroke and grows
 * again would move the whole dialog every time. The list keeps the tallest height it reached while the dialog is open
 * (`optionsFloor`) and eases to a taller one, so the dialog only ever grows, once, and never bounces while typing.
 */
const optionsList = ref<HTMLElement>(), optionsFloor = ref(0);
let optionsMotion: Animation | null = null;
async function settleOptions() {
  const list = optionsList.value, before = list?.offsetHeight ?? 0;
  await nextTick();
  if (!list) return;
  const after = list.offsetHeight, floor = after;
  if (after <= optionsFloor.value) return;
  optionsMotion?.cancel();
  // The first rows also fade in, which is all that remains of the motion under reduced motion.
  const fade = before === 0;
  const motion = optionsMotion = after > before ? animateAtlas(list, [
    { height: `${before}px`, overflow: 'clip', ...(fade ? { opacity: 0 } : {}) },
    { height: `${after}px`, overflow: 'clip', ...(fade ? { opacity: 1 } : {}) },
  ], { duration: motionMs(list, '--atlas-motion-base'), fill: 'none' }) : null;
  const settle = () => { if (optionsMotion === motion) { optionsMotion = null; optionsFloor.value = Math.max(optionsFloor.value, floor); } };
  if (motion) void motion.finished.then(settle, settle); else settle();
}
watch(optionNames, settleOptions);
watch(editing, open => { if (!open) { optionsMotion?.cancel(); optionsMotion = null; optionsFloor.value = 0; } });
async function load() {
  request?.abort(); const controller = request = new AbortController(), current = ++generation;
  // The shown tags stay in place during a reload of the same page; a different page clears them first.
  loading.value = true; error.value = '';
  try {
    const result = await api<{ tags: PageTag[] }>(`/pages/${props.pageId}/tags`, { signal: controller.signal });
    if (current === generation && !controller.signal.aborted) await settleRow(() => { tags.value = result.tags; loaded.value = true; });
  } catch (failure) { if (current === generation && !controller.signal.aborted) { loaded.value = false; error.value = errorMessage(failure); } }
  finally { if (current === generation) loading.value = false; }
}
// The catalog's answer for a typed text, kept in `known`; a failed one says the suggestions are down (a new tag can still be typed).
const lookup = createDebouncedLookup<{ typed: string; items: PageTag[] }>({
  delayMs: 150, minLength: 0, empty: () => ({ typed: '', items: [] }),
  find: async (value, signal) => ({ typed: foldText(value), items: (await api<{ items: PageTag[] }>(`/tags?q=${encodeURIComponent(value)}`, { signal })).items ?? [] }),
  onResult: ({ typed, items }) => { answers().set(typed, items); suggestions.value = items; answered.value = typed; suggestPending.value = false; },
  onError: failure => { suggestError.value = errorMessage(failure); suggestPending.value = false; },
});
function stopSuggestions() { lookup.cancel(); suggestPending.value = false; }
/** Asks for the tags that match the typed text: at once for the opening and an empty field, after a short pause while typing. */
function findSuggestions(immediate = false) {
  stopSuggestions(); suggestError.value = ''; index.value = 0;
  if (!editing.value) return;
  const typed = foldText(cleanInput.value), cached = answers().get(typed);
  if (cached) { suggestions.value = cached; answered.value = typed; }
  // A text answered before shows that answer at once and nothing waits for the refresh; only a new text holds an Enter back.
  suggestPending.value = !cached;
  lookup.update(cleanInput.value, immediate || !typed ? 0 : 150);
}
async function startEditing() {
  if (!props.canEdit || !loaded.value) return;
  // A save still on its way carries the newest draft (the tags shown are older): reopening must not send them back.
  if (!persisting) draft.value = tags.value.map(tag => tag.name);
  opened = [...draft.value]; searchKeys.rest(); text.value = ''; editError.value = ''; undone = []; redone = []; editing.value = true;
  findSuggestions(true); await nextTick(); fieldElement(input.value)?.focus();
}
/** The tags the dialog opened with: removing some and closing offers „Zpět“ on the page, where Ctrl+Z of the dialog is gone (P6). */
let opened: string[] = [];
function close() {
  editing.value = false; stopSuggestions();
  const before = opened, gone = before.filter(name => !draft.value.some(kept => tagKey(kept) === tagKey(name)));
  if (gone.length) toaster.success(t.tags.removed(gone), { undo: () => commit([...before]) });
}
/** Every change of the tags goes through here: shown at once, one step for Ctrl+Z, sent while the keyboard carries on. */
function commit(next: string[]) {
  undone.push([...draft.value]); if (undone.length > 50) undone.shift();
  redone = [];
  draft.value = next; editError.value = '';
  void persist();
}
function step(from: string[][], to: string[][]) {
  const names = from.pop();
  if (!names) return;
  to.push([...draft.value]);
  draft.value = names; editError.value = '';
  void persist();
}
function add(name: string) {
  const value = cleanTagName(name);
  if (!value) return;
  if (value.length > 48 || /[\p{Cc}\p{Cf}]/u.test(name)) { editError.value = t.tags.tooLong; return; }
  if (draft.value.some(name => tagKey(name) === tagKey(value))) { text.value = ''; return; }
  if (draft.value.length >= 20) { editError.value = t.tags.tooMany; return; }
  text.value = '';
  commit([...draft.value, value]); fieldElement(input.value)?.focus();
}
function remove(position: number) { commit(draft.value.filter((_, at) => at !== position)); fieldElement(input.value)?.focus(); }
// Enter or Tab takes the highlighted row (the first, once something is typed; one typed before its answer waits for it); nothing typed takes
// nothing unless the arrows or the pointer chose a row; the first Esc empties the field, the next one closes the dialog (P3, P8).
const searchKeys = useSearchKeys({ text: () => text.value, clear: () => { text.value = ''; }, waiting: () => suggestPending.value, keepFocus: true,
  empty: () => true,
  take: () => { const name = optionNames.value[index.value]; if (!name) return false; add(name); } });
function keys(event: KeyboardEvent) {
  if (event.isComposing || event.ctrlKey || event.metaKey || event.altKey) return;
  if (searchKeys.onKeydown(event)) return;
  if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
    event.preventDefault();
    const last = optionNames.value.length - 1, first = !searchKeys.lit.value;
    index.value = Math.max(0, Math.min(last, first ? (event.key === 'ArrowDown' ? 0 : last) : index.value + (event.key === 'ArrowDown' ? 1 : -1)));
    searchKeys.move();
    document.getElementById(`${listId}-${index.value}`)?.scrollIntoView({ block: 'nearest' });
  }
}
/** Ctrl+Z and Ctrl+Y (also Ctrl+Shift+Z) walk back and forth through the changes of the tags, unless the field holds text of its own to undo (P6). */
function historyKeys(event: KeyboardEvent) {
  if (event.isComposing || !(event.ctrlKey || event.metaKey) || event.altKey || text.value) return;
  const key = event.key.toLowerCase();
  if (key !== 'z' && key !== 'y') return;
  event.preventDefault();
  if (key === 'y' || event.shiftKey) step(redone, undone); else step(undone, redone);
}
/** What the page shows (P17): the tags as saved, and while a save is on its way the names just chosen; a refused save puts the saved ones back. */
const shownTags = computed<PageTag[]>(() => saving.value ? draft.value.map(name => tags.value.find(tag => tagKey(tag.name) === tagKey(name)) ?? { id: `draft:${tagKey(name)}`, name }) : tags.value);
/** Sends the tags as they are shown, one request at a time; changes made meanwhile go in the next one. A refused save puts the saved tags back. */
let persisting = false;
async function persist() {
  if (persisting) return;
  persisting = true; saving.value = true;
  try {
    while (props.canEdit && changed.value) {
      const sent = [...draft.value], current = generation, id = props.pageId;
      const controller = request = new AbortController();
      try {
        const result = await api<{ tags: PageTag[] }>(`/pages/${id}/tags`, {
          method: 'PUT', signal: controller.signal,
          body: JSON.stringify({ names: sent, expectedTagIds: tags.value.map(tag => tag.id) }),
        });
        if (current !== generation || controller.signal.aborted) return;
        await settleRow(() => { tags.value = result.tags; });
        // The names as the server keeps them, unless the keyboard has already moved on.
        if (draft.value.length === sent.length && draft.value.every((name, at) => name === sent[at])) draft.value = result.tags.map(tag => tag.name);
      } catch (failure) {
        if (current !== generation || controller.signal.aborted) return;
        const message = errorMessage(failure);
        undone = []; redone = [];
        if (failure instanceof ApiError && failure.status === 409) await load();
        draft.value = tags.value.map(tag => tag.name);
        if (editing.value && mounted) editError.value = message; else toaster.error(message);
        return;
      }
    }
  } finally { persisting = false; saving.value = false; }
}
watch(text, () => findSuggestions());
let initialTags = props.initial;
watch([() => props.pageId, () => props.userId], () => {
  refreshAfterInterruptedSave = false; editing.value = false; saving.value = false; stopSuggestions(); suggestions.value = []; loaded.value = false; tags.value = [];
  if (initialTags) { tags.value = [...initialTags]; loaded.value = true; initialTags = undefined; } else void load();
}, { immediate: true });
watch(() => props.canEdit, value => {
  if (!value) {
    if (saving.value) { refreshAfterInterruptedSave = true; generation++; request?.abort(); saving.value = false; }
    editing.value = false; stopSuggestions();
  } else if (refreshAfterInterruptedSave || !loaded.value) {
    refreshAfterInterruptedSave = false; void load();
  }
});
// Leaving the page while a save is on its way lets it finish (and say so if it fails): aborting would drop the change without a word.
let mounted = true;
onBeforeUnmount(() => { mounted = false; stopSuggestions(); if (!persisting) { generation++; request?.abort(); } });
</script>
<template>
  <div ref="root" class="page-tags tags-ui" :aria-busy="loading" :aria-label="t.tags.pageTags">
    <LoadingNote v-if="loading && !loaded" inline class="tag-loading" :label="t.tags.loading" />
    <span v-else-if="error" class="tag-lookup-error" role="alert">{{ error }} <Button variant="outline" type="button" @click="load">{{ t.tags.retry }}</Button></span>
    <template v-else>
      <div v-if="shownTags.length" class="tag-chip-list">
        <button v-for="tag in shownTags" :key="tag.id" type="button" class="tag-chip page-tag-link" :data-tone="tagTone(tag.name)" :disabled="tag.id.startsWith('draft:')" :aria-label="t.tags.pagesWith(tag.name)" @click="emit('selectTag', tag)"><AtlasIcon name="hash" :size="14" />{{ tag.name }}</button>
      </div>
      <button v-if="loaded && (canEdit || (canWrite !== false && !shownTags.length))" type="button" class="edit-page-tags" :class="{ 'is-locked': !canEdit }" :inert="!canEdit || undefined" :aria-label="tags.length ? t.tags.editTags : t.tags.addTags" @click="startEditing"><AtlasIcon :name="tags.length ? 'pencil' : 'plus'" :size="14" /><span>{{ tags.length ? t.tags.editTags : t.tags.addTags }}</span></button>
    </template>
    <AppDialog :title="t.tags.pageTags" :open="editing" @close="close">
      <div class="tag-editor tags-ui" @keydown="historyKeys">
        <p class="tag-editor-intro">{{ t.tags.intro }}</p>
        <div class="tag-selection" :aria-label="t.tags.selected">
          <span v-for="(name, position) in draft" :key="tagKey(name)" class="tag-chip tag-draft" :data-tone="tagTone(name)"><AtlasIcon name="hash" :size="14" />{{ name }}<button type="button" class="tag-remove" :aria-label="t.tags.removeTag(name)" @click="remove(position)"><AtlasIcon name="close" :size="14" /></button></span>
          <span v-if="!draft.length" class="tag-selection-empty">{{ t.tags.noTags }}</span>
        </div>
        <Label class="tag-input-label" :for="listId + '-input'">{{ t.tags.findOrCreate }} <span>{{ draft.length }}/20</span></Label>
        <div class="tag-input-wrap"><AtlasIcon name="search" :size="18" /><Input :id="listId + '-input'" ref="input" v-model="text" class="h-auto border-0 bg-transparent p-0 shadow-none dark:bg-transparent focus-visible:border-0 focus-visible:[outline:none]! min-h-[42px] py-2.5 text-sm md:text-sm" role="combobox" aria-autocomplete="list" :aria-controls="listId" :aria-expanded="optionNames.length > 0" :aria-activedescendant="highlighted >= 0 && optionNames[index] ? listId + '-' + index : undefined" :disabled="draft.length >= 20" maxlength="48" :placeholder="t.tags.placeholder" autocomplete="off" @keydown="keys" /></div>
        <div :id="listId" ref="optionsList" class="tag-options" role="listbox" :aria-label="t.tags.available" :style="optionsFloor ? { minHeight: `${optionsFloor}px` } : undefined">
          <LoadingSkeleton v-if="suggestPending && !optionNames.length" :rows="3" row-height="52px" :label="t.tags.loading" />
          <button v-for="(name, optionIndex) in optionNames" :id="listId + '-' + optionIndex" :key="tagKey(name)" type="button" role="option" :aria-selected="highlighted === optionIndex" tabindex="-1" :class="{ active: highlighted === optionIndex }" @pointermove="index = optionIndex; searchKeys.move()" @mousedown.prevent @click="add(name)"><span class="tag-option-icon" :data-tone="tagTone(name)"><AtlasIcon :name="createName === name ? 'plus' : 'hash'" :size="16" /></span><span><strong>{{ name }}</strong><small>{{ createName === name ? t.tags.createNew : t.tags.addExisting }}</small></span><AtlasIcon name="plus" :size="16" /></button>
        </div>
        <p v-if="suggestError" class="tag-lookup-error" role="status">{{ t.tags.lookupError }} <Button variant="outline" type="button" @click="findSuggestions(true)">{{ t.tags.retry }}</Button></p>
        <p v-if="editError" class="notice error" role="alert">{{ editError }}</p>
        <TagSuggestions :request="capabilities.tagSuggestions && canEdit && pageVersion !== undefined ? aiTagRequest : undefined" :context-key="tagContextKey" @apply="applyAiTags" />
        <p class="tag-editor-help"><AtlasIcon name="info" :size="14" />{{ t.tags.help }}</p>
      </div>
    </AppDialog>
  </div>
</template>
