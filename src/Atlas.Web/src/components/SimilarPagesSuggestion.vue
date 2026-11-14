<script setup lang="ts">
import { nextTick, onMounted, ref, watch } from "vue";
import { animateAtlas, literalMs, meterFill, PROTOTYPE_MS, prefersReducedMotion } from "../motion/aiMotion";
import { pageLink } from "../pageLinks";
import { matchPercent, type SimilarPage } from "../similarPages";
import PageStatusBadge from "./PageStatusBadge.vue";
import SuggestionChip from "./settings/SuggestionChip.vue";
import { Button } from "./ui/button";
import t from "../i18n/components-creation";

/**
 * P1 "Podobné stránky už existují:", under the title of a page being created: SuggestionChip as an informational panel
 * (no apply) with up to three rows, each a plain link to the existing page (or to the matching section). It is a plain
 * similarity rule, so its icon is the type icon (`info`), never the AI glyph. It never takes focus and is not announced;
 * the parent decides when it is shown and what dismissing means, and returns focus to the title afterwards.
 *
 * Motion (prototype "Založení a publikace", moments 2 and 3): the panel unfolds (SuggestionChip), and once it has
 * unfolded the match meters fill one after another (meterFill: 400 ms, 80 ms apart). A row that arrives later (the title
 * changed) fills its own meter only. Under reduced motion the meters fade in instead of growing.
 *
 * Keyboard (P15): ↓ in the title (the parent calls `focusFirst`) goes to the first page's link, ↑/↓ move between the links and
 * „Přesto založit“, Enter opens the page instead of creating a duplicate (`open`: the parent drops its draft, nothing to confirm), Esc goes
 * back to the title; „Přesto založit“ creates.
 */
const props = defineProps<{ items: SimilarPage[] }>();
const emit = defineEmits<{ dismiss: []; create: []; back: []; open: [] }>();

const chip = ref<{ $el: HTMLElement }>();
const filled = new Set<string>();

function sectionOf(item: SimilarPage) {
  return item.matchedBy === "heading" && item.headingId && item.headingTitle ? { id: item.headingId, title: item.headingTitle } : null;
}
function meta(item: SimilarPage) {
  const section = sectionOf(item);
  if (section) return t.similar.section(item.spaceName, section.title);
  if (item.matchedBy === "meaning") return t.similar.byMeaning(item.spaceName);
  return item.matchedBy === "alias" ? t.similar.byAlias(item.spaceName) : item.spaceName;
}
function openLabel(item: SimilarPage) {
  const section = sectionOf(item);
  return section ? t.similar.openSectionOf(section.title, item.title) : t.similar.openPageOf(item.title);
}

/** The meters of rows that have not filled yet. */
function newBars() {
  const rows = chip.value?.$el.querySelectorAll<HTMLElement>("[data-similar-page]") ?? [];
  return [...rows].flatMap(row => {
    const id = row.dataset.similarPage!;
    if (filled.has(id)) return [];
    filled.add(id);
    const bar = row.querySelector<HTMLElement>(".similar-meter > i");
    return bar ? [bar] : [];
  });
}

/**
 * The bars wait at zero until the panel has unfolded (the prototype fills them after the reveal), so a half-open
 * panel never shows them full; then they fill. The DOM is final the whole time: if nothing animates they are simply there.
 */
async function play(bars: HTMLElement[], unfolding: Promise<unknown> = Promise.resolve()) {
  if (!bars.length) return;
  const reduced = prefersReducedMotion(bars[0]);
  for (const bar of bars) { if (reduced) bar.style.opacity = "0"; else bar.style.transform = "scaleX(0)"; }
  await unfolding;
  if (bars[0]!.isConnected) {
    if (reduced) bars.forEach((bar, index) => animateAtlas(bar, [{ opacity: 0 }, { opacity: 1 }], { duration: literalMs(bar, PROTOTYPE_MS.meterFill), delay: literalMs(bar, index * PROTOTYPE_MS.meterStagger) }));
    else meterFill(bars);
  }
  // The animations hold the first frame through their delay (fill backwards) and end on the bar's own styles.
  for (const bar of bars) { bar.style.opacity = ""; bar.style.transform = ""; }
}

const stops = () => [...chip.value?.$el.querySelectorAll<HTMLElement>("[data-similar-stop]") ?? []];
function focusFirst() { stops()[0]?.focus(); }
function onKeydown(event: KeyboardEvent) {
  const all = stops(), at = all.indexOf(event.target as HTMLElement);
  if (at < 0) return;
  if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); emit("back"); return; }
  if (event.key !== "ArrowDown" && event.key !== "ArrowUp") return;
  event.preventDefault();
  // ↑ from the first link is back in the title.
  if (event.key === "ArrowUp" && at === 0) emit("back");
  else all[Math.min(all.length - 1, at + (event.key === "ArrowDown" ? 1 : -1))]?.focus();
}
defineExpose({ focusFirst });

onMounted(() => {
  const unfolding = Promise.allSettled((chip.value?.$el.getAnimations?.() ?? []).map(animation => animation.finished));
  void play(newBars(), unfolding);
});
watch(() => props.items.map(item => item.id).join(), async () => {
  await nextTick();
  void play(newBars());
}, { flush: "post" });
</script>

<template>
  <!-- The keys of its links and „Přesto založit“ (focus moves only by ↓ from the title). -->
  <div class="similar-pages-host" @keydown="onKeydown">
  <SuggestionChip ref="chip" layout="panel" icon="info" :label="t.similar.label" :apply-label="null" :dismiss-label="t.similar.dismiss" data-similar-pages @dismiss="emit('dismiss')">
    <ul class="similar-pages">
      <li v-for="item in items" :key="item.id" class="similar-row" :data-similar-page="item.id">
        <span class="similar-copy">
          <span class="similar-title"><strong>{{ item.title }}</strong><PageStatusBadge v-if="item.isDraft" :is-published="false" :version="1" :published-version="null" /></span>
          <small>{{ meta(item) }}</small>
          <span v-if="matchPercent(item.score) !== null" class="similar-meter" role="img" :aria-label="t.similar.match(matchPercent(item.score)!)"><i :style="{ width: `${matchPercent(item.score)}%` }" /></span>
        </span>
        <Button as-child size="xs" variant="outline" class="similar-open">
          <a :href="pageLink(item.id, sectionOf(item)?.id)" :aria-label="openLabel(item)" data-similar-stop @click.exact="emit('open')">{{ sectionOf(item) ? t.similar.openSection : t.similar.open }}</a>
        </Button>
      </li>
    </ul>
    <p class="similar-note">{{ t.similar.note }}</p>
    <template #actions>
      <Button type="button" size="xs" variant="outline" data-similar-stop data-similar-create @click="emit('create')">{{ t.similar.createAnyway }}</Button>
    </template>
  </SuggestionChip>
  </div>
</template>

<style scoped>
/* The prototype's .dup-r rows and .meter. */
.similar-pages { list-style: none; margin: 0; padding: 0; }
.similar-row { display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: 2px 10px; align-items: center; padding: 7px 0; border-bottom: 1px solid var(--line); }
.similar-row:last-child { border-bottom: 0; }
.similar-copy { display: grid; gap: 1px; min-width: 0; justify-items: start; }
.similar-title { display: flex; flex-wrap: wrap; align-items: center; gap: 2px 6px; min-width: 0; }
.similar-title strong { font-size: 13px; overflow-wrap: anywhere; }
.similar-title :deep([data-status]) { margin-left: 0; }
.similar-copy small { color: var(--muted); font-size: 12px; overflow-wrap: anywhere; }
.similar-meter { display: block; width: 120px; max-width: 100%; height: 4px; margin-top: 3px; overflow: hidden; border-radius: 99px; background: var(--hover); }
.similar-meter > i { display: block; height: 100%; border-radius: inherit; background: var(--accent); transform-origin: 0 50%; }
/* A link dressed as a button keeps the button's text colour, not the link colour. */
.similar-open.similar-open { color: var(--text); text-decoration: none; }
.similar-note { margin: 4px 0 0; color: var(--muted); font-size: 12px; }
</style>
