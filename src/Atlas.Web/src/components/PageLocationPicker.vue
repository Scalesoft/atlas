<script setup lang="ts">
import t from "../i18n/components-b";
import { computed, nextTick, ref, useId, watch } from 'vue';
import { focusFirst } from '../lib/pickEnter';
import { useSearchKeys } from '../lib/searchKeys';
import AtlasIcon from './AtlasIcon.vue';
import { Input } from './ui/input';
import { Button } from './ui/button';

type Location = { id: string; parentId: string | null; title: string; canEdit: boolean; sortOrder?: number };
const props = defineProps<{ modelValue: string; items: Location[]; label: string; rootLabel?: string; excluded?: Set<string>; disabled?: boolean }>();
const emit = defineEmits<{ 'update:modelValue': [id: string] }>();
const search = ref(''), expanded = ref(new Set<string>()), limit = ref(80), focused = ref('');
const id = useId(), treeElement = ref<HTMLElement>();
const normalize = (value: string) => value.normalize('NFD').replace(/\p{M}/gu, '').toLocaleLowerCase();
const byId = computed(() => new Map(props.items.map(item => [item.id, item])));
const children = computed(() => {
  const map = new Map<string, Location[]>();
  for (const item of props.items) {
    if (props.excluded?.has(item.id)) continue;
    const parent = item.parentId && byId.value.has(item.parentId) ? item.parentId : '';
    const group = map.get(parent) ?? []; group.push(item); map.set(parent, group);
  }
  for (const group of map.values()) group.sort((a, b) => (a.sortOrder ?? 0) - (b.sortOrder ?? 0) || a.title.localeCompare(b.title, 'cs'));
  return map;
});
function path(item: Location) {
  const names = [item.title], seen = new Set([item.id]);
  let parent = item.parentId ? byId.value.get(item.parentId) : undefined;
  while (parent && !seen.has(parent.id)) {
    names.unshift(parent.title); seen.add(parent.id); parent = parent.parentId ? byId.value.get(parent.parentId) : undefined;
  }
  return names.join(' / ');
}
const selectedPath = computed(() => byId.value.has(props.modelValue) ? path(byId.value.get(props.modelValue)!) : props.rootLabel ?? t.loc.rootLevel);
const rows = computed(() => {
  const query = normalize(search.value.trim());
  if (query) return props.items.filter(item => !props.excluded?.has(item.id) && normalize(item.title).includes(query)).map(item => ({ ...item, depth: 0 }));
  const result: (Location & { depth: number })[] = [], seen = new Set<string>();
  const pending = (children.value.get('') ?? []).map(item => ({ ...item, depth: 0 })).reverse();
  while (pending.length) {
    const item = pending.pop()!;
    if (seen.has(item.id)) continue;
    seen.add(item.id); result.push(item);
    if (expanded.value.has(item.id)) pending.push(...(children.value.get(item.id) ?? []).map(child => ({ ...child, depth: item.depth + 1 })).reverse());
  }
  return result;
});
// The match Enter in the search takes: the first one the reader may place pages under.
const first = computed(() => search.value.trim() ? rows.value.find(item => item.canEdit) : undefined);
// Enter or Tab places the first match, the first Esc empties the search (P3, P8); Enter in the search never submits the surrounding move form.
const searchKeys = useSearchKeys({ text: () => search.value, clear: () => { search.value = ''; }, empty: () => true,
  take: () => { if (!first.value) return false; select(first.value.id); } });
const enterTree = () => focusFirst(treeElement.value, '[role=treeitem]');
function toggle(value: string) {
  const next = new Set(expanded.value); if (next.has(value)) next.delete(value); else next.add(value); expanded.value = next;
  if (focused.value && !rows.value.some(item => item.id === focused.value)) focused.value = value;
}
function select(value: string) {
  if (props.disabled || props.excluded?.has(value) || value && !byId.value.get(value)?.canEdit) return;
  emit('update:modelValue', value);
}
async function keys(event: KeyboardEvent, value: string) {
  if (event.isComposing || props.disabled) return;
  const nodes = [...treeElement.value!.querySelectorAll<HTMLElement>('[role=treeitem]')];
  const index = nodes.findIndex(node => node.dataset.pageId === value);
  let target: HTMLElement | undefined;
  switch (event.key) {
    case 'Enter': case ' ': select(value); break;
    case 'ArrowDown': target = nodes[Math.min(index + 1, nodes.length - 1)]; break;
    case 'ArrowUp': target = nodes[Math.max(index - 1, 0)]; break;
    case 'Home': target = nodes[0]; break;
    case 'End': target = nodes.at(-1); break;
    case 'ArrowRight': if (children.value.has(value) && !expanded.value.has(value)) toggle(value); else target = nodes[index + 1]; break;
    case 'ArrowLeft':
      if (expanded.value.has(value)) toggle(value);
      else target = nodes.find(node => node.dataset.pageId === (byId.value.get(value)?.parentId ?? ''));
      break;
    default: return;
  }
  event.preventDefault(); event.stopPropagation();
  await nextTick(); target?.focus();
}
watch(search, () => { limit.value = 80; focused.value = ''; });
watch([() => props.modelValue, byId], () => {
  const next = new Set(expanded.value), seen = new Set<string>();
  let parent = byId.value.get(props.modelValue)?.parentId;
  while (parent && !seen.has(parent)) { next.add(parent); seen.add(parent); parent = byId.value.get(parent)?.parentId; }
  expanded.value = next;
  const index = rows.value.findIndex(item => item.id === props.modelValue);
  if (index >= limit.value) limit.value = index + 1;
}, { immediate: true });
</script>

<template>
  <div class="location-picker" :class="{ disabled }">
    <label :for="id" class="location-search"><AtlasIcon name="search" /><Input :id="id" v-model="search" :disabled="disabled" :aria-label="t.loc.searchIn(label)" :placeholder="t.loc.findPlaceholder" :aria-activedescendant="first ? `${id}-row-${first.id}` : undefined" @keydown="searchKeys.onKeydown" @keydown.down.prevent="enterTree" /></label>
    <div ref="treeElement" class="location-tree" role="tree" :aria-label="label" :aria-disabled="disabled">
      <div role="treeitem" data-page-id="" :aria-selected="!modelValue" :tabindex="!disabled && focused === '' ? 0 : -1" class="location-row location-root" @focus="focused = ''" @keydown="keys($event, '')" @click="select('')">
        <AtlasIcon name="book" /><span>{{ rootLabel ?? t.loc.rootLevel }}</span><AtlasIcon v-if="!modelValue" name="check" />
      </div>
      <div v-for="item in rows.slice(0, limit)" :id="`${id}-row-${item.id}`" :key="item.id" role="treeitem" :data-page-id="item.id" :aria-level="item.depth + 1"
        :aria-selected="modelValue === item.id" :aria-disabled="!item.canEdit || disabled" :title="item.canEdit ? undefined : t.loc.readOnlyReason" :aria-description="item.canEdit ? undefined : t.loc.readOnlyReason" :aria-expanded="!search && children.has(item.id) ? expanded.has(item.id) : undefined"
        :tabindex="!disabled && focused === item.id ? 0 : -1" :data-first="item.id === first?.id || undefined" class="location-row" :style="{ paddingLeft: `${10 + Math.min(item.depth, 12) * 18}px` }"
        @focus="focused = item.id" @keydown="keys($event, item.id)" @click="select(item.id)">
        <button v-if="!search && children.has(item.id)" type="button" tabindex="-1" class="location-expand" :data-open="expanded.has(item.id) || undefined" :disabled="disabled" :aria-label="`${expanded.has(item.id) ? t.loc.collapse : t.loc.expand} ${item.title}`" @click.stop="toggle(item.id)"><AtlasIcon name="right" :size="14" /></button>
        <AtlasIcon v-else name="page" :size="15" class="location-leaf" />
        <span>{{ item.title }}<small v-if="search">{{ path(item) }}</small></span><AtlasIcon v-if="modelValue === item.id" name="check" :size="16" />
      </div>
      <p v-if="search && !rows.length" class="location-empty" role="status">{{ t.loc.noMatch }}</p>
      <Button v-if="rows.length > limit" type="button" variant="ghost" class="location-more" :disabled="disabled" @click="limit += 80">{{ t.loc.showMore(rows.length - limit) }}</Button>
    </div>
    <p class="location-selection"><AtlasIcon name="organize" :size="14" /><span><small>{{ t.loc.selected }}</small><span data-location-value>{{ selectedPath }}</span></span></p>
  </div>
</template>

<style scoped>
.location-picker { border: 1px solid var(--line); border-radius: 12px; overflow: hidden; background: var(--surface); }
.location-search { display: flex !important; align-items: center; gap: 10px; margin: 0 !important; padding: 10px 12px; border-bottom: 1px solid var(--line); color: var(--muted); }
.location-search input { border: 0; box-shadow: none; background: transparent; min-width: 0; }
.location-tree { max-height: 280px; overflow: auto; padding: 6px; overscroll-behavior: contain; }
.location-row {
  display: flex; align-items: center; gap: 9px; min-height: 38px; padding: 8px 10px; border-radius: 7px; font-size: 13px; cursor: pointer; outline-offset: -2px;
  /* Rows that join (a branch opened, more rows loaded) fade in; the others are reused by their key and stay still. */
  animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out) both;
  transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), color var(--atlas-motion-fast) var(--atlas-ease-out);
}
@media (hover: hover) and (pointer: fine) { .location-row:hover:not([aria-disabled=true], [aria-selected=true]) { background: var(--hover); } }
/* The tree clips (overflow: auto), so the shared focus ring sits inside the row. */
.location-picker .location-tree > .location-row[role="treeitem"]:focus-visible { outline-offset: -2px; }
.location-row[aria-selected=true] { background: var(--accent-soft); color: var(--accent); font-weight: 600; }
/* The match Enter in the search takes, while the search has the focus. */
.location-picker:has(.location-search input:focus) .location-row[data-first]:not([aria-selected=true]) { background: var(--state-active-row-bg); box-shadow: var(--state-active-row-mark); }
.location-row[aria-disabled=true] { color: var(--muted); cursor: default; }
.location-row > span { flex: 1; min-width: 0; overflow-wrap: anywhere; }
.location-row svg { flex-shrink: 0; }
.location-row small { display: block; color: var(--muted); font-size: 11px; font-weight: 400; }
.location-expand { border: 0; padding: 0; width: 18px; height: 22px; background: transparent; color: inherit; display: grid; place-items: center; }
/* The page icon of a row without children takes the room of the expander, so the names of all rows start on one line. */
.location-leaf { margin-inline: 1.5px; }
/* One chevron that turns (as the sidebar tree's), instead of a right and a down icon that swap. */
.location-expand > svg { transition: rotate var(--atlas-motion-fast) var(--atlas-motion-ease); }
.location-expand[data-open] > svg { rotate: 90deg; }
[data-atlas-reduced-motion] .location-expand > svg { transition: none; }
@media (prefers-reduced-motion: reduce) { .location-expand > svg { transition: none; } }
.location-root { border-bottom: 1px solid var(--line); margin-bottom: 4px; }
.location-selection { margin: 0; padding: 12px 14px; background: var(--canvas); display: flex; gap: 10px; align-items: center; font-size: 12px; overflow-wrap: anywhere; border-top: 1px solid var(--line); }
.location-selection small { display: block; color: var(--muted); font-size: 11px; margin-bottom: 3px; }
.location-empty { padding: 16px; color: var(--muted); font-size: 13px; }
.location-more { width: 100%; }
.disabled { opacity: .6; }
</style>
