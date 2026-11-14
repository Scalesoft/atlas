<script setup lang="ts">
import t from "../../i18n/components-library";
import componentsLibraryHome from "../../i18n/components-library-home";
import { formatNumber } from "../../i18n";
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from "vue";
import type { Space } from "../../api";
import { formatAgo, formatDateTime } from "../../formatDate";
import { roleLabel } from "../../roles";
import AtlasIcon from "../AtlasIcon.vue";
import SpaceCoverImage from "./SpaceCoverImage.vue";

/**
 * The card of one space in Knihovna (refresh 2.5, board 01): the space's cover on top, its icon over the
 * cover's edge, name, description and one line of facts. The cover is the image of the space
 * (SpaceCoverImage) under a soft scrim: its drawing or upload in the space's two colours, or a quiet
 * pattern with its icon drawn large, so every card looks intentional. Only the title is a link,
 * stretched over the card, so `#corner` can hold the space's own controls (the pin toggle) beside it.
 *
 * `preview` is the smaller card Nastavení prostoru → Obecné shows next to the cover controls: the same
 * drawing with the draft name, icon and colour, without a link. Choose TileCard for any other card in a
 * grid; this one exists for the cover, which TileCard has no place for.
 */
type CardSpace = Pick<Space, "id" | "name" | "icon" | "color" | "role" | "pageCount" | "lastChangedAt"> & Partial<Pick<Space, "description" | "cover" | "itemAccessOnly">>;
// `coverSrc` shows another image than the stored one: the file being uploaded, before the server has it.
const props = withDefaults(defineProps<{ space: CardSpace; href?: string; index?: number; preview?: boolean; coverSrc?: string }>(), { href: undefined, index: 0, preview: false, coverSrc: undefined });
const emit = defineEmits<{ navigate: [event: MouseEvent] }>();

// The card rises in once; a card that later moves (pinned to the top) must not rise again.
const entered = ref(false);
const description = computed(() => (props.space.itemAccessOnly ? t.card.itemAccessOnly : props.space.description?.trim() ?? ""));
// An older server may leave the count out.
const pageCount = computed(() => props.space.pageCount ?? 0);
const pageWord = computed(() => t.pagesWord(pageCount.value));

/* The facts stay on one line, and the time of the last change must not be cut mid-word ("před 20 minut…" on a card of
   262 px with a three-digit count): when it would be, the role, the least important of the three, gives way to its icon
   (its words stay for assistive technology and in the title). Looked at again whenever the line changes its width; set on
   the element itself, so the card needs no re-render for it. */
const facts = ref<HTMLElement>();
const roleIcons = { Admin: "shield-check", Writer: "pencil", Reader: "eye" } as const;
const roleIcon = computed(() => roleIcons[props.space.role as keyof typeof roleIcons] ?? "eye");
function fit() {
  const row = facts.value;
  if (!row) return;
  const time = row.querySelector<HTMLElement>(".space-card-changed");
  row.removeAttribute("data-tight");
  if (time && time.scrollWidth > time.clientWidth + 1) row.setAttribute("data-tight", "");
}
let observer: ResizeObserver | undefined;
onMounted(() => {
  fit();
  if (typeof ResizeObserver === "function" && facts.value) { observer = new ResizeObserver(fit); observer.observe(facts.value); }
});
onBeforeUnmount(() => observer?.disconnect());
watch(() => [props.space.pageCount, props.space.lastChangedAt, props.space.role], () => void nextTick(fit));
</script>

<template>
  <article class="space-card" :class="{ 'is-preview': preview, 'has-link': !!href, 'is-entered': entered }" :data-color="space.color" :style="{ '--i': Math.min(index, 8) }" @animationend.self="entered = true">
    <!-- The covers of the first cards are what the page shows first: they are asked for at once and ahead of other
         requests; the rest wait until they come near the viewport. An image that arrives while the card still rises
         in is part of that entrance (SpaceCoverImage, `instant`). -->
    <SpaceCoverImage class="space-card-cover" :space="space" :src="coverSrc" :eager="index < 8" :priority="index < 4" :instant="entered || preview" :mark-size="preview ? 120 : 150" />
    <span class="space-card-scrim" aria-hidden="true" />
    <span class="space-card-icon space-icon" :data-color="space.color" aria-hidden="true"><AtlasIcon :name="space.icon" :size="preview ? 19 : 22" /></span>
    <div class="space-card-body">
      <strong class="space-card-title" :title="space.name"><a v-if="href" class="space-card-link" :href="href" @click="emit('navigate', $event)">{{ space.name }}</a><template v-else>{{ space.name }}</template></strong>
      <p v-if="!preview" class="space-card-description" :class="{ 'is-empty': !description }" :title="description || undefined">{{ description || t.card.noDescription }}</p>
      <div ref="facts" class="space-card-facts">
        <span data-space-pages><b>{{ formatNumber(pageCount) }}</b> {{ pageWord }}</span>
        <template v-if="!preview">
          <span class="space-card-role" :title="componentsLibraryHome.accessOf(roleLabel(space.role))" data-space-access-level><AtlasIcon class="space-card-role-icon" :name="roleIcon" :size="13" aria-hidden="true" /><span class="space-card-role-text">{{ roleLabel(space.role) }}</span></span>
          <time v-if="space.lastChangedAt" class="space-card-changed" :datetime="space.lastChangedAt" :title="t.card.changed(formatDateTime(space.lastChangedAt))">{{ formatAgo(space.lastChangedAt) }}</time>
        </template>
      </div>
    </div>
    <div v-if="$slots.corner" class="space-card-corner"><slot name="corner" /></div>
  </article>
</template>

<style scoped>
.space-card {
  --space-cover-h: 120px;
  --space-icon-size: 46px;
  position: relative;
  display: flex;
  flex-direction: column;
  min-width: 0;
  overflow: hidden;
  border: 1px solid var(--line);
  border-radius: 16px;
  background: var(--surface);
  color: var(--text);
  animation: st-rise-in var(--atlas-motion-slow) var(--atlas-ease-out) backwards;
  /* Cards enter one after another (board 16), at most the first eight; --st-stagger is the settings design system's step. */
  animation-delay: calc(var(--i, 0) * var(--st-stagger));
  transition:
    transform var(--atlas-motion-fast) var(--atlas-ease-out),
    box-shadow var(--atlas-motion-base) var(--atlas-ease-out),
    border-color var(--atlas-motion-base) var(--atlas-ease-out);
}
.space-card.is-entered { animation: none; }
.space-card.has-link:focus-within {
  border-color: color-mix(in srgb, var(--accent) 32%, var(--line));
  box-shadow: var(--refresh-card-shadow-hover);
}
/* Preserve the hit area at the card edges and underneath its independent pin control. */
@media (hover: hover) and (pointer: fine) {
  .space-card.has-link:hover { border-color: color-mix(in srgb, var(--accent) 32%, var(--line)); box-shadow: var(--refresh-card-shadow-hover); }
}
.space-card.has-link:has(.space-card-link:focus-visible) { outline: 2px solid var(--accent); outline-offset: 2px; }
/* The card shows the ring; outranks the shell's ring for every link, which would draw a second one around the title. */
.space-card.has-link .space-card-link:focus-visible:not(.md-editor):not(.md-editor *) { outline: none; }

.space-card-cover { flex: none; height: var(--space-cover-h); }
/* Over the cover, on the card: the image layer itself (SpaceCoverImage) has no scrim. */
.space-card-scrim { position: absolute; inset: 0 0 auto; height: var(--space-cover-h); background: linear-gradient(180deg, transparent 50%, var(--surface) 100%); pointer-events: none; }

/* Beats the shell's 27px .atlas .space-icon. */
.space-card .space-card-icon.space-icon {
  position: absolute;
  top: calc(var(--space-cover-h) - 24px);
  left: 16px;
  z-index: 1;
  width: var(--space-icon-size);
  height: var(--space-icon-size);
  border-radius: 13px;
  box-shadow: 0 0 0 3px var(--surface), 0 4px 12px color-mix(in srgb, var(--text) 12%, transparent);
}
.space-card-body { display: flex; flex: 1; flex-direction: column; gap: 6px; min-width: 0; padding: 32px 16px 14px; }
.space-card-title { display: -webkit-box; overflow: hidden; font-size: 16px; font-weight: 650; line-height: 1.3; overflow-wrap: anywhere; -webkit-box-orient: vertical; -webkit-line-clamp: 2; line-clamp: 2; }
.space-card-link { color: inherit; text-decoration: none; outline: none; }
.space-card-link::after { content: ""; position: absolute; inset: 0; z-index: 2; }
.space-card-description { flex: 1; display: -webkit-box; overflow: hidden; margin: 0; color: var(--muted); font-size: 13px; line-height: 1.45; overflow-wrap: anywhere; -webkit-box-orient: vertical; -webkit-line-clamp: 2; line-clamp: 2; }
.space-card-description.is-empty { font-style: italic; opacity: .8; }
.space-card-facts { display: flex; align-items: center; gap: 8px; min-width: 0; margin-top: 4px; padding-top: 10px; border-top: 1px solid color-mix(in srgb, var(--line) 50%, var(--surface)); color: var(--muted); font-size: 12px; white-space: nowrap; }
.space-card-facts b { color: var(--text); font-weight: 650; font-variant-numeric: tabular-nums; }
.space-card-role { padding: 1px 7px; border-radius: 6px; background: color-mix(in srgb, var(--line) 50%, var(--surface)); color: color-mix(in srgb, var(--text) 82%, var(--muted)); font-weight: 600; }
.space-card-changed { margin-left: auto; overflow: hidden; text-overflow: ellipsis; }
.space-card-role { display: inline-flex; align-items: center; }
.space-card-role-icon { display: none; }
/* When the time would be cut the role is its icon (SpaceCard.vue `fit`). Its words stay for assistive technology, as a
   transparent box without width that keeps the line of the chip: the line of facts has the height it has everywhere, so
   the dividers of neighbouring cards stay level. */
.space-card-facts[data-tight] .space-card-role { padding: 1px 5px; }
.space-card-facts[data-tight] .space-card-role-icon { display: block; }
.space-card-facts[data-tight] .space-card-role-text { width: 0; overflow: hidden; opacity: 0; white-space: nowrap; }
.space-card-corner { position: absolute; top: 10px; right: 10px; z-index: 3; display: flex; gap: 6px; }

.space-card.is-preview { --space-cover-h: 96px; --space-icon-size: 40px; border-radius: 14px; animation: none; }
.space-card.is-preview .space-card-icon.space-icon { left: 14px; border-radius: 11px; box-shadow: 0 0 0 3px var(--surface); }
.space-card.is-preview .space-card-body { gap: 2px; padding: 26px 14px 12px; }
.space-card.is-preview .space-card-title { display: block; font-size: 14px; white-space: nowrap; text-overflow: ellipsis; }
.space-card.is-preview .space-card-facts { margin: 0; padding: 0; border: 0; }
.space-card.is-preview .space-card-facts b { color: inherit; font-weight: inherit; }

</style>
