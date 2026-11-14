<script setup lang="ts">
import componentsLibraryHome from "../../i18n/components-library-home";
import { formatNumber } from "../../i18n";
import { nextTick, onMounted, ref, useId, watch } from "vue";
import { unreadNotifications } from "../../notifications";
import { isPlainNavigationClick } from "../../pageLinks";
import type { LibraryTab } from "../../personalLibrary";
import AtlasIcon from "../AtlasIcon.vue";
import { Button } from "../ui/button";
import { useStageHeight } from "./stageHeight";
import { useTabIndicator } from "./tabIndicator";
import { tablistKeys } from "../../keyboard/navigation";
import { vScrollEdges } from "../../lib/scrollEdges";

/**
 * Knihovna's page (refresh 2.5, board 01): the title, Nový prostor (on Prostory only, the other parts have their own create actions), and the parts of the library as tabs –
 * the spaces, then the personal lists the server keeps. The tabs are links (`#/library/<part>`), the shell
 * routes a plain click; the underline slides to the new tab and the content only fades (board 16).
 * On a phone the tabs are a row of pills (board 14). The shell renders the part itself in the slot.
 */
export type LibraryPart = LibraryTab | "spaces" | "contacts" | "personal" | "shared" | "notifications";
const props = defineProps<{ view: LibraryPart; spaceCount: number; canCreate?: boolean }>();
const emit = defineEmits<{ navigate: [part: LibraryPart]; create: [] }>();

const parts: { id: LibraryPart; title: string; short?: string }[] = [
  { id: "spaces", title: componentsLibraryHome.page.parts.spaces.title },
  { id: "personal", title: componentsLibraryHome.page.parts.personal.title, short: componentsLibraryHome.page.parts.personal.short },
  { id: "shared", title: componentsLibraryHome.page.parts.shared.title, short: componentsLibraryHome.page.parts.shared.short },
  { id: "recent", title: componentsLibraryHome.page.parts.recent.title },
  { id: "favorites", title: componentsLibraryHome.page.parts.favorites.title },
  { id: "drafts", title: componentsLibraryHome.page.parts.drafts.title, short: componentsLibraryHome.page.parts.drafts.short },
  { id: "updates", title: componentsLibraryHome.page.parts.updates.title, short: componentsLibraryHome.page.parts.updates.short },
  { id: "contacts", title: componentsLibraryHome.page.parts.contacts.title },
  { id: "notifications", title: componentsLibraryHome.page.parts.notifications.title },
];
const headingId = useId();

function go(part: LibraryPart, event: MouseEvent) {
  if (!isPlainNavigationClick(event)) return;
  event.preventDefault();
  emit("navigate", part);
}

/* The underline is one element that slides under the current tab; it is placed without motion first
   (tabIndicator.ts, shared with the tabs of a space). */
const tabs = ref<HTMLElement>();
const indicator = useTabIndicator(tabs, '[aria-current="page"]', () => [props.view, props.spaceCount, unreadNotifications.value]);
/* On a phone the tabs are a scrolling row of pills wider than the screen: opening #/library/drafts showed the
   current pill („Koncepty“) cut by the screen's edge. The row brings the current tab fully into view (with its
   16 px gutter), at once on arrival and with a scroll when it changes; a row that fits does not move. */
function revealCurrentTab(smooth: boolean) {
  const row = tabs.value, tab = row?.querySelector<HTMLElement>('[aria-current="page"]');
  if (!row || !tab || row.scrollWidth <= row.clientWidth) return;
  const gutter = parseFloat(getComputedStyle(row).paddingLeft) || 0;
  const start = tab.offsetLeft - gutter, end = tab.offsetLeft + tab.offsetWidth + gutter - row.clientWidth;
  const left = row.scrollLeft > start ? start : row.scrollLeft < end ? end : row.scrollLeft;
  if (left === row.scrollLeft) return;
  const reduced = window.matchMedia?.("(prefers-reduced-motion: reduce)").matches || document.documentElement.hasAttribute("data-atlas-reduced-motion");
  row.scrollTo?.({ left, behavior: smooth && !reduced ? "smooth" : "auto" });
}
onMounted(() => revealCurrentTab(false));
watch(() => props.view, () => void nextTick(() => revealCurrentTab(true)));
// After the first switch the parts only fade in (board 16); the page itself rose in once.
const switched = ref(false);
watch(() => props.view, () => { switched.value = true; });
// The part that is fading out is a still picture: nothing in it may take focus or a click meanwhile; the stage
// eases from its height to the new part's (stageHeight.ts), so the page below never jumps.
const stage = ref<HTMLElement>();
const { onBeforeLeave, onEnter } = useStageHeight(stage);
function retire(element: Element) {
  onBeforeLeave();
  element.setAttribute("inert", "");
  element.setAttribute("aria-hidden", "true");
}
</script>

<template>
  <section class="overview library-overview" :aria-labelledby="headingId">
    <header class="library-page-header">
      <div class="library-page-title">
        <h1 :id="headingId">{{ componentsLibraryHome.page.title }}</h1>
        <p>{{ componentsLibraryHome.page.intro }}</p>
      </div>
      <Button v-if="canCreate && view === 'spaces'" type="button" class="library-create" :aria-label="componentsLibraryHome.page.create" data-space-create @click="emit('create')"><AtlasIcon name="plus" :size="15" /><span class="library-create-label">{{ componentsLibraryHome.page.create }}</span></Button>
    </header>
    <nav ref="tabs" v-scroll-edges:x class="library-tabs" :aria-label="componentsLibraryHome.page.tabs" @keydown="tablistKeys($event, 'a[href]')">
      <a
        v-for="part in parts"
        :key="part.id"
        :href="`#/library/${part.id}`"
        :aria-current="view === part.id ? 'page' : undefined"
        :data-library-part="part.id"
        @click="go(part.id, $event)"
      ><span :class="{ 'library-tab-long': part.short }">{{ part.title }}</span><span v-if="part.short" class="library-tab-short" aria-hidden="true">{{ part.short }}</span><span v-if="part.id === 'spaces'" class="library-tab-count">{{ formatNumber(spaceCount) }}</span><span
        v-else-if="part.id === 'notifications' && unreadNotifications" class="library-tab-count" data-library-unread :aria-label="componentsLibraryHome.page.unread(unreadNotifications)">{{ unreadNotifications > 99 ? "99+" : unreadNotifications }}</span></a>
      <span class="library-tabs-indicator" :class="{ 'is-ready': indicator.ready }" :style="{ width: `${indicator.width}px`, transform: `translateX(${indicator.left}px)` }" aria-hidden="true" />
    </nav>
    <!-- The parts cross-fade in one grid cell, so the old one never vanishes in a frame and the new one never waits for it. -->
    <div ref="stage" class="library-stage">
      <Transition name="library-part" @before-leave="retire" @enter="onEnter">
        <div :key="view" class="library-part" :data-switched="switched || undefined"><slot /></div>
      </Transition>
    </div>
  </section>
</template>
