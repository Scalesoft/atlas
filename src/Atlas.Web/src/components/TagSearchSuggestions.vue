<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import { Button } from './ui/button';
import { api } from '../api';
import { tagTone, type PageTag } from '../pageTags';
import AtlasIcon from './AtlasIcon.vue';
import InlineReveal from './settings/InlineReveal.vue';
import '../page-tags.css';
import t from "../i18n/components-c";
const props = defineProps<{ query: string; userId: string }>();
const emit = defineEmits<{ selectTag: [tag: PageTag] }>();
const tags = ref<PageTag[]>([]);
const failed = ref(false);
// The block unfolds and folds (InlineReveal) instead of popping in and out and shoving the results by its height;
// while it folds it keeps showing what it showed.
const shown = ref<{ tags: PageTag[]; failed: boolean }>({ tags: [], failed: false });
watch([tags, failed], () => { if (tags.value.length || failed.value) shown.value = { tags: tags.value, failed: failed.value && !tags.value.length }; }, { flush: 'sync' });
const open = computed(() => tags.value.length > 0 || failed.value);
let timer: ReturnType<typeof setTimeout> | undefined, controller: AbortController | undefined, generation = 0;
function load() {
  clearTimeout(timer); controller?.abort(); const version = ++generation;
  // Shown tags stay until the next result replaces them, so typing does not make the block jump.
  failed.value = false;
  const query = props.query.trim().replace(/^#/, '').trim();
  if (!query) { tags.value = []; return; }
  timer = setTimeout(async () => {
    const abort = controller = new AbortController();
    try {
      const result = await api<{ items: PageTag[] }>(`/tags?q=${encodeURIComponent(query)}`, { signal: abort.signal });
      if (version === generation && !abort.signal.aborted) tags.value = result.items;
    } catch { if (version === generation && !abort.signal.aborted) { tags.value = []; failed.value = true; } }
  }, 180);
}
watch([() => props.query, () => props.userId], load, { immediate: true });
onBeforeUnmount(() => { generation++; clearTimeout(timer); controller?.abort(); });
</script>
<template>
  <span class="tag-search-status" role="status" aria-live="polite" aria-atomic="true">{{ failed ? t.tagSearch.failed : tags.length ? t.tagSearch.found(tags.length) : '' }}</span>
  <InlineReveal :open="open">
    <section class="tag-search-suggestions tags-ui" :aria-label="t.tagSearch.region">
      <template v-if="!shown.failed">
        <span class="tag-suggestion-label">{{ t.tagSearch.tags }}</span>
        <div class="tag-chip-list"><button v-for="tag in shown.tags" :key="tag.id" type="button" class="tag-chip" :data-tone="tagTone(tag.name)" :aria-label="t.tagSearch.pagesWith(tag.name)" @click="emit('selectTag', tag)"><AtlasIcon name="hash" :size="14" />{{ tag.name }}</button></div>
      </template>
      <p v-else class="tag-lookup-error">{{ t.tagSearch.failed }} <Button variant="outline" type="button" @click="load">{{ t.tagSearch.retry }}</Button></p>
    </section>
  </InlineReveal>
</template>
