<script setup lang="ts">
import componentsLibraryHome from "../../i18n/components-library-home";
import { computed } from "vue";
import type { PageSummary, Space } from "../../api";
import { formatAgo, formatDateTime } from "../../formatDate";
import { roleLabel } from "../../roles";
import { spaceCoverSrc } from "../../spaceCover";
import AtlasIcon from "../AtlasIcon.vue";
import ActionDisclosure from "../ActionDisclosure.vue";
import ActionDisclosureItem from "../ActionDisclosureItem.vue";
import { Button } from "../ui/button";
import SpaceCoverImage from "./SpaceCoverImage.vue";
import { leaveInPlace } from "./leaveInPlace";

/**
 * The head of a space's overview (refresh 2.5, board 02): the image of the space when it has one, icon, name,
 * description, a line of facts (pages, drafts, the account's access, the last change) and the space's two actions,
 * Nastavení prostoru for its administrators and Nová stránka for writers (with the module Boards, Nový offers Stránka and
 * Tabule). Presentational; the shell routes the actions.
 */
// `pagesLoaded` false: the page list is still on its way, so the head counts what the space list says (the count of the space)
// instead of claiming no pages; a head that is told nothing counts the pages it is given.
const props = withDefaults(defineProps<{ space: Space; pages: readonly PageSummary[]; pagesLoaded?: boolean; canManage?: boolean; canWrite?: boolean; busy?: boolean; boards?: boolean }>(), { pagesLoaded: true });
const emit = defineEmits<{ settings: []; newPage: []; newBoard: [] }>();
const pageTotal = computed(() => (props.pagesLoaded ? props.pages.length : props.space.pageCount ?? 0));
const drafts = computed(() => props.pages.filter((page) => !page.isPublished).length);
// A space without an image keeps the plain head: the content stays first, no band of pattern above the name.
const hasCover = computed(() => spaceCoverSrc(props.space.id, props.space.cover) !== undefined);
</script>

<template>
  <header class="space-hero" :class="{ 'has-cover': hasCover }">
    <SpaceCoverImage v-if="hasCover" class="space-hero-cover" :space="space" eager instant :mark-size="180" />
    <span class="large-space-icon space-icon" :data-color="space.color" aria-hidden="true"><AtlasIcon :name="space.icon" :size="24" /></span>
    <div class="space-hero-text">
      <h1>{{ space.name }}</h1>
      <p v-if="space.description" class="space-description space-hero-description">{{ space.description }}</p>
      <TransitionGroup tag="p" name="space-fact" class="space-hero-facts" @before-leave="leaveInPlace">
        <span key="pages" class="space-fact" data-space-fact="pages">{{ componentsLibraryHome.pages(pageTotal) }}</span>
        <span v-if="drafts" key="drafts" class="space-fact" data-tone="warning" data-space-fact="drafts">{{ componentsLibraryHome.drafts(drafts) }}</span>
        <span key="role" class="space-fact" data-tone="accent" :title="componentsLibraryHome.accessOf(roleLabel(space.role))" data-space-fact="role"><span class="sr-only">{{ componentsLibraryHome.access }}: </span>{{ roleLabel(space.role) }}</span>
        <span v-if="space.lastChangedAt" key="changed" class="space-fact-plain">{{ componentsLibraryHome.header.lastChange }} <time :datetime="space.lastChangedAt" :title="formatDateTime(space.lastChangedAt)">{{ formatAgo(space.lastChangedAt) }}</time></span>
      </TransitionGroup>
    </div>
    <div v-if="canManage || canWrite" class="space-hero-actions">
      <Button v-if="canManage" variant="outline" type="button" data-space-settings-open :disabled="busy" @click="emit('settings')"><AtlasIcon name="sliders-horizontal" :size="15" />{{ componentsLibraryHome.header.settings }}</Button>
      <ActionDisclosure v-if="canWrite && boards" :label="componentsLibraryHome.header.newContent" :trigger-title="componentsLibraryHome.header.newTitle" variant="default" size="default" trigger-class="space-hero-new" :disabled="busy">
        <template #trigger><AtlasIcon name="plus" :size="15" />{{ componentsLibraryHome.header.new }}</template>
        <ActionDisclosureItem data-space-new-page @click="emit('newPage')"><AtlasIcon name="file-plus" />{{ componentsLibraryHome.header.page }}</ActionDisclosureItem>
        <ActionDisclosureItem data-space-new-board @click="emit('newBoard')"><AtlasIcon name="layout-dashboard" />{{ componentsLibraryHome.header.board }}</ActionDisclosureItem>
      </ActionDisclosure>
      <Button v-else-if="canWrite" type="button" data-space-new-page :disabled="busy" @click="emit('newPage')"><AtlasIcon name="plus" :size="15" />{{ componentsLibraryHome.header.newPage }}</Button>
    </div>
  </header>
</template>

<style scoped>
/* A fact that arrives (the drafts come with the page list) or goes (the last draft is published) fades where it lands and
   the facts after it glide to their new place, instead of jumping by its width in one frame; the one that leaves is
   pinned where it stands (leaveInPlace.ts) and fades. Reduced motion: the fade, no travel. */
.space-hero-facts { position: relative; }
.space-fact-enter-active { animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out) both; }
.space-fact-leave-active { position: absolute; z-index: 0; pointer-events: none; animation: st-fade-out var(--atlas-motion-fast) var(--atlas-ease-in) both; }
.space-fact-move { transition: transform var(--atlas-motion-base) var(--atlas-ease-out); }
@media (prefers-reduced-motion: reduce) { .space-fact-move { transition: none; } }
:global([data-atlas-reduced-motion] .space-hero-facts .space-fact-move) { transition: none; }
</style>
