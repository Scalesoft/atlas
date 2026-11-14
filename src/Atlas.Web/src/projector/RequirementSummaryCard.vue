<script setup lang="ts">
import { readStored, removeStored, writeStored } from "../lib/browserStorage";
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, useId, watch } from "vue";
import AtlasIcon from "../components/AtlasIcon.vue";
import { animateAtlas, collapseHeight, motionMs, popStatus, prefersReducedMotion, revealHeight } from "../motion/aiMotion";
import { keyed, missingPrerequisites, requirementName, summarize, type PageRequirement } from "./requirementModel";
import { blockedItems, blockKinds, byParty, cardControls, daysSince, emptyOutline, notAskedByParty, overviewSections, stateEntries, stateSegments, waitingByParty, type PageOutline, type StateEntry } from "./requirementOverview";
import { readinessTarget, sectionReadiness, type ReadinessOf } from "./requirementReadiness";
import { softGlow } from "../motion/aiMotion";
import t from "../i18n/projector";
import { directionLabel, partyName, partyPrefix, requirementStateLabel, stateText, unknownParty } from "./requirementSummary";
import RequirementViewBar from "./RequirementViewBar.vue";
import { compareRequirements, partyShown, viewPrefs } from "./requirementViewMode";
import OverviewActions from "../overview/OverviewActions.vue";
import type { OverviewCardAction } from "../overview/overviewKinds";
import { pragueNow, historyStampText } from "@atlas/markdown/core";

/**
 * The deployment overview of one page (spec §1.2, §2.2): computed from the page's requirement headings and outline, never stored. A dashboard in
 * one card: the percent (counting up) and a bar split by state with its legend; progress and readiness by section (the nearest
 * heading); what blocks, with its first note; whom we wait on (waiting for input only), by party, each with its days of waiting; the
 * inputs nobody was asked for yet (neřešeno), by party (plan 3.4 §3); and the inputs not delivered as a count that opens their list. The summary block's view (RequirementSummaryView.vue) adds its menu; the same card stands at the top
 * of a page without a summary block (`auto`, requirementAutoOverview.ts), with a pin that makes it a block. The read view of a
 * published page, history and print show it without anything that writes.
 */
const props = defineProps<{
  items: readonly PageRequirement[];
  /** On a board: the order and the state filter act only on a page's requirements, so they are not offered. */
  board?: boolean;
  /** The page's headings and the notes of its blocked requirements; without it the card shows no sections and no notes. */
  outline?: PageOutline;
  /** The page is a requirement catalog: the summary says so and counts its definitions instead of progress. */
  catalog?: boolean;
  /** Brings a requirement heading of the page into view (its section unfolded). */
  show: (item: PageRequirement) => void;
  /** The summary's menu, offered to someone who may change the page. */
  menu?: (anchor: HTMLElement) => void;
  /** The menu's place is kept while there is no menu yet (the page can be written, its editor is not editable yet): the actions
   *  beside it do not move when the menu arrives. */
  menuPlace?: boolean;
  /** The automatic overview (no summary block on the page). */
  auto?: boolean;
  /** „Připnout souhrn do stránky“: inserts a summary block in the automatic overview's place. */
  pin?: () => void;
  /** The page whose view (detail level, order, state filter) the strip under the summary sets; none (history, the public view, print), no strip. */
  viewPage?: string;
  /** The overview's bulk actions („Poskládat tabuli“, „Doplnit prerekvizity“), in the card's head (OverviewActions.vue). */
  actions?: readonly OverviewCardAction[];
  /** Brings a requirement into view for its missing prerequisites; a heading has no detail of its own, so this is `show`. */
  detail?: (item: PageRequirement) => void;
  /** Tab from the last control, Shift+Tab from the first, Esc: out into the page's text (spec §4.2); false lets the browser go on. */
  leave?: (card: HTMLElement, way: "next" | "back" | "caret") => boolean;
  /** The catalog's titles by key, asked once a prerequisite is missing; without it the key stands for its name. */
  catalogNames?: () => Promise<ReadonlyMap<string, string>>;
}>();
const root = ref<HTMLElement>();
const badge = ref<HTMLElement>();
const outline = computed(() => props.outline ?? emptyOutline);
// „Kdo“ (the view's parties): the card counts only their requirements (its numbers, bar, headline and parts); the page's list of parties
// and what the page lacks stay whole.
const scoped = computed(() => view.value?.parties.length ? props.items.filter(item => partyShown(view.value!, item.attrs.party)) : props.items);
const summary = computed(() => summarize(scoped.value));
const complete = computed(() => summary.value.total > 0 && summary.value.percent === 100);
// The bar and the legend: the states, a block's own word for its state (stateLabel) an item of its own in that state's tone.
const entries = computed(() => stateEntries(scoped.value));
const barEntries = computed(() => entries.value.filter(entry => entry.share > 0));
const byKey = computed(() => keyed(props.items));
// On a board a block of another page is not shown from here: its rows, chips and legend are plain text, not buttons that do nothing.
const press = computed(() => props.board ? "span" : "button");
const pressType = computed(() => props.board ? undefined : "button");
// The level switch: wherever the page has a view (not a catalog, not a page read without one), however many blocks.
const viewing = computed(() => !!props.viewPage && !props.catalog);
// The order and the state filter: not for one requirement, not on a board. The filter acts in the document, the order only here, as the
// sorted list below (owner 2026-11-04): on every level, NONE too, where the list is the way to the hidden requirements.
const menus = computed(() => viewing.value && !props.board && props.items.length > 1);
const view = computed(() => menus.value ? viewPrefs(props.viewPage!).value : undefined);
/** The requirements in the chosen order (a stable sort, so equal ones keep the page's), only those the filter keeps; none unsorted. */
const sorted = computed(() => {
  const { sort = "", state = "" } = view.value ?? {};
  return !sort ? [] : scoped.value.filter(item => !state || item.attrs.state === state).sort((a, b) => compareRequirements(sort, a.attrs, b.attrs));
});
// The sorted list is one Tab stop (as long as the page): ↑ ↓ PageUp PageDown Home End move in it, the row last focused is where Tab comes back to.
const sortedAt = ref(0);
watch(sorted, list => { if (sortedAt.value >= list.length) sortedAt.value = 0; });
function walkSorted(event: KeyboardEvent) {
  const rows = [...(event.currentTarget as HTMLElement).children] as HTMLElement[], at = rows.indexOf(event.target as HTMLElement);
  const to = { ArrowDown: at + 1, ArrowUp: at - 1, PageDown: at + 10, PageUp: at - 10, Home: 0, End: rows.length - 1 }[event.key];
  if (at < 0 || to === undefined) return;
  event.preventDefault();
  rows[Math.max(0, Math.min(rows.length - 1, to))]?.focus();
}
/** What a row of the sorted list is sorted by, beside its name. */
function sortedBy(item: PageRequirement) {
  const sort = view.value?.sort;
  if (sort === "date") return item.attrs.date ? historyStampText({ date: item.attrs.date }) : t.waited.undated;
  if (sort === "party") return item.attrs.party.trim() || partyName(unknownParty);
  if (sort === "key") return item.attrs.key;
  return stateText(item.attrs);
}
/**
 * Brings a requirement into view (its detail with `detail`). On any level and under any filter: the caret or the focus lands in it,
 * which pins it in view (requirementViewMode.ts), so the reader's level and filter stay as chosen (model §3).
 */
function go(item: PageRequirement, detail = false) {
  if (props.board) return;
  const from = document.activeElement instanceof HTMLElement && root.value?.contains(document.activeElement) ? document.activeElement : null;
  (detail && props.detail ? props.detail : props.show)(item);
  if (from) escapeBack(from);
}
let stopEscape: (() => void) | undefined;
/** Esc right after a jump (before any other key) brings the focus back to the control the jump came from (spec §4.2). */
function escapeBack(from: HTMLElement) {
  stopEscape?.();
  const page = root.value?.closest<HTMLElement>(".ProseMirror");
  if (!page) return;
  // Before the editor's own Esc (it selects the block around the caret); a popover's Esc never reaches the page.
  // It lasts while the reader looks around (arrows, Home, a reader's Enter or Space), not once the writer types or anyone tabs away.
  const writes = page.isContentEditable;
  const key = (event: KeyboardEvent) => {
    if (event.key !== "Escape") {
      const typing = event.key === "Tab" || (writes && !event.ctrlKey && !event.metaKey && (event.key.length === 1 || ["Enter", "Backspace", "Delete"].includes(event.key)));
      if (typing) stopEscape?.();
      return;
    }
    stopEscape?.();
    if (!from.isConnected) return;
    event.preventDefault();
    event.stopPropagation();
    from.focus();
  };
  page.addEventListener("keydown", key, true);
  page.addEventListener("pointerdown", () => stopEscape?.(), { once: true });
  stopEscape = () => { page.removeEventListener("keydown", key, true); stopEscape = undefined; };
}
function tabOut(event: KeyboardEvent) {
  if (event.altKey || event.ctrlKey || event.metaKey || event.defaultPrevented || !root.value || !props.leave) return;
  const controls = cardControls(root.value);
  const way = event.key === "Escape" ? "caret" : event.key !== "Tab" ? null
    : event.shiftKey ? (controls[0] === event.target ? "back" : null) : (controls.at(-1) === event.target ? "next" : null);
  if (way && props.leave(root.value, way)) event.preventDefault();
}
// One section says no more than the bar: the breakdown shows from two on. A board has no headings: no „Bez nadpisu“.
const sections = computed(() => {
  const all = overviewSections(scoped.value, outline.value.entries).filter(section => !props.board || section.title);
  return all.length < 2 ? [] : all.map(section => ({ ...section, segments: stateSegments(section.counts), readiness: sectionReadiness(section.items, byKey.value) }));
});
const blocked = computed(() => blockedItems(scoped.value, outline.value.notes));
const waiting = computed(() => waitingByParty(scoped.value));
const notAsked = computed(() => notAskedByParty(scoped.value));
// „čeká 103 dní“: the day of the page's render is enough, a summary is not left open for days.
const today = pragueNow().date;
const waited = (item: PageRequirement) => daysSince(item.attrs.date, today);
const missing = computed(() => summary.value.missing);
// What unfinished blocks require and the page lacks (plan 3.5 D); „Doplnit prerekvizity“ in the foot takes them from the catalog.
const lacking = computed(() => missingPrerequisites(props.items));
// „Doplnit prerekvizity“ only while one is missing.
const footActions = computed(() => props.actions?.filter(action => action.id !== "fill-prerequisites" || lacking.value.length > 0));
const catalogNames = shallowRef<ReadonlyMap<string, string>>();
watch(() => lacking.value.length > 0, any => {
  if (any && !catalogNames.value) props.catalogNames?.().then(names => { catalogNames.value = names; }, () => {});
}, { immediate: true });
// The card stays short (owner 2. 11.: a laptop screen): under the bar one row of parts with their counts, one of them open at a time;
// the breakdown by section is one of them, closed on every level.
type SummaryPart = "sections" | "blocked" | "waiting" | "notAsked" | "missing" | "lacking";
/** A part, or the list of one item of the bar and the legend (`state:` and its key): one open at a time. */
type Open = SummaryPart | `state:${string}`;
const open = ref<Open | "">("");
// „Zobrazit vše“: every part at once, as the card was before; the reader's choice for the page, kept in this browser.
const allKey = () => `atlas.summaryParts.${props.viewPage ?? ""}`;
const all = ref(!!props.viewPage && readStored(allKey()) === "all");
function showAll(value: boolean) {
  all.value = value;
  open.value = "";
  if (!props.viewPage) return;
  if (value) writeStored(allKey(), "all"); else removeStored(allKey());
}
const toggle = (part: Open) => {
  if (all.value) { showAll(false); open.value = part; return; }
  open.value = open.value === part ? "" : part;
};
// On paper nothing opens: the print layout (print/PrintDocument.vue) shows every part under its title, as „Zobrazit vše“ does.
const printing = ref(false);
const shows = (part: SummaryPart) => printing.value || all.value || open.value === part;
// „Pozornost“ under the states, what blocks first: the inputs not delivered say from whom the most, those not asked for yet hang on them
// as their part („z toho nežádáno“, a subset of them), what we wait on says for how long at most.
const mostMissing = computed(() => {
  const counts = new Map<string, number>();
  for (const item of missing.value) { const party = item.attrs.party.trim(); if (party) counts.set(party, (counts.get(party) ?? 0) + 1); }
  const [party, count] = [...counts].reduce<[string, number]>((most, next) => next[1] > most[1] ? next : most, ["", 0]);
  return party ? t.card.mostFrom(party, count) : undefined;
});
const longest = computed(() => {
  const days = waiting.value.flatMap(group => group.items.map(waited)).filter(value => value !== undefined);
  return days.length ? t.card.longest(t.waited.days(Math.max(...days))) : undefined;
});
const parts = computed(() => [
  { id: "blocked" as const, label: t.card.blocking, icon: "octagon-x", tone: "blocked", count: blocked.value.length },
  { id: "missing" as const, label: t.card.missing, icon: "arrow-down", tone: "waiting_for_input", count: missing.value.length, note: mostMissing.value },
  { id: "notAsked" as const, label: missing.value.length ? t.card.notAskedOf : t.card.notAsked, icon: "megaphone", tone: "planned",
    count: notAsked.value.reduce((sum, group) => sum + group.items.length, 0), sub: missing.value.length > 0 },
  { id: "waiting" as const, label: t.card.waiting, icon: "hourglass", tone: "waiting_for_input", count: waiting.value.reduce((sum, group) => sum + group.items.length, 0), note: longest.value },
  { id: "lacking" as const, label: t.card.lacking, icon: "link", tone: "waiting_for_input", count: lacking.value.length },
  { id: "sections" as const, label: t.card.bySection, icon: "list", count: sections.value.length },
].filter(part => part.count > 0));
// A segment of the bar, or its item in the legend, opens its blocks under them (by party, each a jump), as the parts below open theirs.
const stateKey = (entry: StateEntry): Open => `state:${entry.key}`;
const openEntry = computed(() => entries.value.find(entry => stateKey(entry) === open.value));
const openGroups = computed(() => {
  const entry = openEntry.value;
  return entry ? byParty(scoped.value.filter(item => item.attrs.state === entry.state && item.attrs.stateLabel.trim() === entry.label)) : [];
});
// „Kdo“'s choices: the parties of the page's requirements with their counts, the most first, „Bez určení“ last; a chosen one gone from
// the page stays, so it can be unticked.
const partyOptions = computed(() => {
  const counts = new Map<string, number>();
  for (const item of props.items) { const party = item.attrs.party.trim(); counts.set(party, (counts.get(party) ?? 0) + 1); }
  for (const party of view.value?.parties ?? []) if (!counts.has(party)) counts.set(party, 0);
  return [...counts].sort(([a, x], [b, y]) => Number(!a) - Number(!b) || y - x || a.localeCompare(b))
    .map(([party, count]) => ({ value: party, label: party || partyName(unknownParty), badge: String(count) }));
});
const entryName = (entry: StateEntry) => entry.label || requirementStateLabel(entry.state);
/** The headline: what blocks first, then done of all, what is being worked on (in progress and waiting for input), what has not started. */
const headline = computed(() => {
  const { counts, done, total, percent } = summary.value;
  return { blocked: counts.blocked ? t.card.blockedLead(counts.blocked) : "",
    done: complete.value ? t.card.allDone(done, total) : `${t.card.someDone(done, total)} ${t.card.percentOf(percent)}`,
    rest: [counts.in_progress + counts.waiting_for_input && t.card.inProgress(counts.in_progress + counts.waiting_for_input), counts.planned && t.card.notStarted(counts.planned),
      view.value?.parties.length && t.card.onlyParties(view.value.parties.map(party => party || partyName(unknownParty)).join(", "))].filter(Boolean) };
});
const nameOf = requirementName;
/** „23 vstupů · 4 moduly“ beside the headline's count. */
const kinds = computed(() => blockKinds(scoped.value));
/** What a readiness stands on, said on hover: the block it is blocked by and through what, or what it waits for. */
function readinessTitle(readiness: ReadinessOf<PageRequirement>) {
  if (readiness.state === "blocked") {
    const through = readiness.path.slice(1, -1);
    return t.card.blockedBy(nameOf(readiness.blocking!), through.map(nameOf).join(" › "));
  }
  return readiness.state === "waiting" ? t.card.waitsFor(readiness.prerequisites.map(nameOf).join(", ")) : undefined;
}
// One section's prerequisites open at a time, under its row.
const uid = useId();
const openSection = ref("");
const toggleSection = (key: string) => { openSection.value = openSection.value === key ? "" : key; };
const lists = (readiness: ReadinessOf<PageRequirement>) => readiness.state === "waiting" && readiness.prerequisites.length > 0;
/** On whom a prerequisite waits: „od IT KBN“, „komu Jana“, our own worker, or its direction when nobody is named. */
const whom = (item: PageRequirement) => item.attrs.party.trim()
  ? (item.attrs.direction === "internal" ? item.attrs.party.trim() : `${partyPrefix(item.attrs.direction)} ${item.attrs.party.trim()}`)
  : directionLabel(item.attrs.direction);
/** A catalog page holds definitions, not progress: the keyed blocks are what other pages can take. */
const definitions = computed(() => props.items.filter(item => item.attrs.key).length);

// The shown number counts towards the real one; the bar's segments start empty and grow in their CSS transition.
const shown = ref(0);
const grown = ref(false);
let frame = 0;
function countTo(target: number) {
  cancelAnimationFrame(frame);
  const el = root.value;
  if (!el || prefersReducedMotion(el)) { shown.value = target; return; }
  const from = shown.value, start = performance.now();
  let duration: number | undefined;
  const step = (now: number) => {
    // Read in the first frame, not as the card mounts: with the page's editor being built, the read forced its whole layout (3.6-G).
    duration ??= motionMs(el, "--atlas-ai-reveal");
    // The frame's timestamp can precede `start`: a negative progress would ease below `from` (a moment of "-8 %").
    const progress = duration ? Math.min(1, Math.max(0, (now - start) / duration)) : 1;
    shown.value = Math.round(from + (target - from) * (1 - (1 - progress) ** 3));
    if (progress < 1) frame = requestAnimationFrame(step);
  };
  frame = requestAnimationFrame(step);
}
watch(() => summary.value.percent, countTo);
watch(complete, (now, before) => { if (now && !before && root.value) softGlow(root.value); });
watch(() => props.catalog, (now, before) => { if (now && before === false && badge.value) popStatus(badge.value); }, { flush: "post" });
// A section whose count or readiness changed pops its numbers once.
const sectionCounts = new Map<string, Element>();
const keepCount = (key: string) => (element: unknown) => { if (element instanceof Element) sectionCounts.set(key, element); else sectionCounts.delete(key); };
watch(() => sections.value.map(section => `${section.key}:${section.done}/${section.total}:${section.readiness.state}`), (now, before) => {
  const was = new Set(before);
  now.forEach(line => { const element = was.has(line) ? undefined : sectionCounts.get(line.slice(0, line.indexOf(":"))); if (element) popStatus(element); });
}, { flush: "post" });
onMounted(() => {
  printing.value = !!root.value?.closest(".print-content");
  // Built hidden under the page it replaces (DocumentEditor's `.editor-hydrating`): it arrives as it stands, or it would take that
  // page's place halfway through counting and fading in.
  if (root.value?.closest(".editor-hydrating")) { shown.value = summary.value.percent; grown.value = true; return; }
  countTo(summary.value.percent);
  // The card arrives: it rises a little and fades in (a fade only under reduced motion). Started in the first frame, before it paints:
  // as the card mounts, reading the motion tokens forced a layout of the whole page whose editor is being built (3.6-G).
  requestAnimationFrame(() => {
    grown.value = true;
    animateAtlas(root.value, [{ opacity: 0, transform: "translateY(6px)" }, { opacity: 1, transform: "none" }], { duration: root.value ? motionMs(root.value, "--atlas-motion-slow") : 0 });
  });
});
onBeforeUnmount(() => { cancelAnimationFrame(frame); stopEscape?.(); });

// A list that appears or goes unfolds or folds, so the blocks below the summary never jump.
const settle = (animation: Animation | null, done: () => void) => { if (animation) animation.finished.then(done, done); else done(); };
const unfold = (el: Element, done: () => void) => settle(revealHeight(el as HTMLElement), done);
const fold = (el: Element, done: () => void) => settle(collapseHeight(el as HTMLElement), done);
const share = (value: number) => `${value * 100}%`;
</script>

<template>
  <div ref="root" class="summary atlas-requirement-summary-view" :class="{ 'is-complete': complete && !catalog, 'is-auto': auto, 'is-board': board, 'is-grown': grown }" contenteditable="false"
    data-requirement-summary-view @keydown="tabOut">
    <header class="summary-head">
      <template v-if="catalog">
        <AtlasIcon name="book-open" :size="16" />
        <span ref="badge" class="summary-label" data-summary-catalog>{{ t.card.catalog }} · {{ t.card.definitions(definitions) }}</span>
      </template>
      <span v-else class="summary-label"><AtlasIcon :name="complete ? 'circle-check' : 'activity'" :size="15" />{{ t.card.deploymentState }}</span>
      <span class="summary-actions">
        <OverviewActions v-if="footActions?.length && !printing" :actions="footActions" />
        <button v-if="pin" type="button" class="summary-action" data-summary-pin :title="t.card.pinTitle" @click="pin()">
          <AtlasIcon name="pin" :size="14" /><span>{{ t.card.pin }}</span></button>
        <button v-if="menu" type="button" class="summary-menu" :aria-label="t.summaryMenu.options" :title="t.summaryMenu.options" aria-haspopup="menu" data-summary-menu
          @click="menu($event.currentTarget as HTMLElement)">
          <AtlasIcon name="more" :size="16" />
        </button>
        <span v-else-if="menuPlace" class="summary-menu" aria-hidden="true" data-summary-menu-place />
      </span>
    </header>
    <!-- A catalog page holds definitions, not a deployment's progress: no bar, counts or waiting there. -->
    <Transition :css="false" @enter="unfold" @leave="fold">
      <div v-if="!catalog" class="summary-progress">
        <!-- The takeaway in one line, from the state counts: what blocks, done of all, what is being worked on, what has not started. -->
        <p v-if="items.length" class="summary-headline" data-summary-headline>
          <span v-if="headline.blocked" class="summary-headline-blocked">{{ headline.blocked }}</span> <span class="summary-headline-done">{{ headline.done }}</span><template
            v-for="part in headline.rest" :key="part"> · <span>{{ part }}</span></template>
        </p>
        <!-- The bar opens the breakdown by section, as its part below does (that button is the accessible one). -->
        <!-- A segment opens its blocks, as its item in the legend does (that button is the accessible one). -->
        <div class="summary-bar" aria-hidden="true" data-summary-bar>
          <span v-for="entry in barEntries" :key="entry.key" class="summary-fill summary-segment" :data-tone="entry.state" :data-custom="entry.label ? '' : undefined"
            :data-open="open === stateKey(entry) || undefined" :style="{ width: share(entry.share) }" @click="!board && toggle(stateKey(entry))"><b>{{ entry.count }}</b></span>
        </div>
        <div class="summary-figure">
          <div v-if="items.length" class="summary-counts" data-summary-counts>
            <component :is="press" v-for="entry in entries" :key="entry.key" :type="pressType" data-go :data-tone="entry.state" :data-custom="entry.label ? '' : undefined"
              :aria-expanded="board ? undefined : open === stateKey(entry)" :aria-controls="open === stateKey(entry) ? `${uid}-state` : undefined" @click="!board && toggle(stateKey(entry))">
              <span class="summary-count-name"><i aria-hidden="true" />{{ entryName(entry) }}</span> <b>{{ entry.count }}</b><small v-if="entry.share" class="summary-count-share"> {{ Math.round(entry.share * 100) }} %</small>
              <small v-if="entry.label" class="summary-count-note">{{ t.card.countsAs(requirementStateLabel(entry.state)) }}</small>
            </component>
          </div>
          <div class="summary-total">
            <strong class="summary-percent" role="progressbar" :aria-label="t.card.done" aria-valuemin="0" aria-valuemax="100" :aria-valuenow="summary.percent"
              data-summary-percent>{{ shown }}<small> %</small></strong>
            <span v-if="kinds" class="summary-kinds" data-summary-kinds>{{ kinds }}</span>
          </div>
        </div>
        <Transition :css="false" @enter="unfold" @leave="fold">
          <section v-if="openEntry" :id="`${uid}-state`" class="summary-part summary-list summary-state-list" :data-tone="openEntry.state" data-summary-state-list>
            <div v-for="{ party, items: rows } in openGroups" :key="party" class="summary-group" data-summary-state-party>
              <span class="summary-party">{{ partyName(party) }}</span>
              <span class="summary-chips"><component :is="press" v-for="item in rows" :key="item.id" :type="pressType" data-go @click="go(item)">{{ nameOf(item) }}</component></span>
            </div>
          </section>
        </Transition>
        <div v-if="parts.length && !printing" class="summary-parts" data-summary-parts>
          <span class="summary-parts-title">{{ t.card.attention }}</span>
          <button v-for="part in parts" :key="part.id" type="button" class="summary-disclosure" :class="{ 'is-sub': part.sub }" :data-tone="part.tone" :aria-expanded="shows(part.id)"
            :data-summary-part="part.id" @click="toggle(part.id)">
            <AtlasIcon :name="part.icon" :size="13" />{{ part.label }} <b>{{ part.count }}</b><small v-if="part.note" class="summary-part-note">· {{ part.note }}</small><AtlasIcon class="summary-chevron" name="chevron-down" :size="14" />
          </button>
          <button v-if="parts.length > 1" type="button" class="summary-action summary-all" :aria-pressed="all" data-summary-all @click="showAll(!all)">
            <AtlasIcon :name="all ? 'chevrons-down-up' : 'chevrons-up-down'" :size="14" /><span>{{ all ? t.card.showLess : t.card.showAll }}</span></button>
        </div>
        <Transition :css="false" @enter="unfold" @leave="fold">
          <section v-if="blocked.length && shows('blocked')" class="summary-part" data-summary-blocked>
            <div v-if="printing || all" class="summary-list-title" data-tone="blocked"><AtlasIcon name="octagon-x" :size="13" />{{ t.card.blocking }}</div>
            <div class="summary-rows">
              <component :is="press" v-for="{ item, note } in blocked" :key="item.id" :type="pressType" class="summary-row" data-go @click="go(item)">
                <span class="summary-row-title">{{ nameOf(item) }}</span><span v-if="note" class="summary-row-note">{{ note }}</span>
              </component>
            </div>
          </section>
        </Transition>
        <Transition :css="false" @enter="unfold" @leave="fold">
          <section v-if="waiting.length && shows('waiting')" class="summary-part summary-list" data-summary-waiting>
            <div v-if="printing || all" class="summary-list-title" data-tone="waiting_for_input"><AtlasIcon name="hourglass" :size="13" />{{ t.card.waiting }}</div>
            <div v-for="{ party, items: entries } in waiting" :key="party" class="summary-group" data-summary-party>
              <span class="summary-party">{{ partyName(party) }}</span>
              <span class="summary-chips">
                <component :is="press" v-for="item in entries" :key="item.id" :type="pressType" data-go :title="item.attrs.date ? t.card.waitingSince(historyStampText({ date: item.attrs.date })) : undefined"
                  @click="go(item)">{{ nameOf(item) }}<span v-if="waited(item) !== undefined" class="summary-days" data-summary-days> · {{ t.waited.days(waited(item)!) }}</span></component>
              </span>
            </div>
          </section>
        </Transition>
        <Transition :css="false" @enter="unfold" @leave="fold">
          <section v-if="notAsked.length && shows('notAsked')" class="summary-part summary-list summary-not-asked" data-summary-not-asked>
            <div v-if="printing || all" class="summary-list-title"><AtlasIcon name="megaphone" :size="13" />{{ t.card.notAsked }}</div>
            <div v-for="{ party, items: entries } in notAsked" :key="party" class="summary-group" data-summary-not-asked-party>
              <span class="summary-party">{{ partyName(party) }}</span>
              <span class="summary-chips">
                <component :is="press" v-for="item in entries" :key="item.id" :type="pressType" data-go @click="go(item)">{{ nameOf(item) }}</component>
              </span>
            </div>
          </section>
        </Transition>
        <Transition :css="false" @enter="unfold" @leave="fold">
          <div v-if="missing.length && shows('missing')" class="summary-part summary-missing summary-chips summary-list" data-summary-missing-list>
            <div v-if="printing || all" class="summary-list-title"><AtlasIcon name="arrow-down" :size="13" />{{ t.card.missing }}</div>
            <component :is="press" v-for="item in missing" :key="item.id" :type="pressType" data-go :title="item.attrs.party ? t.card.fromParty(item.attrs.party) : undefined" @click="go(item)">{{ nameOf(item) }}</component>
          </div>
        </Transition>
        <Transition :css="false" @enter="unfold" @leave="fold">
          <div v-if="lacking.length && shows('lacking')" class="summary-part summary-missing summary-chips summary-list" data-summary-lacking-list>
            <div v-if="printing || all" class="summary-list-title"><AtlasIcon name="link" :size="13" />{{ t.card.lacking }}</div>
            <component :is="press" v-for="entry in lacking" :key="entry.key" :type="pressType" data-go :title="t.card.lackingTitle(entry.key, entry.neededBy.map(nameOf).join(', '))" @click="go(entry.neededBy[0]!, true)">{{ catalogNames?.get(entry.key) ?? entry.key }} · {{ entry.neededBy.length }}×</component>
          </div>
        </Transition>
        <Transition :css="false" @enter="unfold" @leave="fold">
          <section v-if="sections.length && shows('sections')" class="summary-part" data-summary-sections>
            <div v-if="printing || all" class="summary-list-title">{{ t.card.bySection }}</div>
            <div class="summary-sections">
              <div v-for="section in sections" :key="section.key" class="summary-section" data-summary-section>
                <!-- The whole row goes to the section; its readiness to what it counts. -->
                <component :is="press" :type="pressType" class="summary-section-name" data-summary-section-show
                  :aria-label="board ? undefined : t.card.sectionLabel(`${section.context ? section.context + ' › ' : ''}${section.title || t.card.untitledSection}`, section.done, section.total)"
                  @click="go(section.items[0]!)"><span v-if="section.context" class="summary-section-context">{{ section.context }} › </span>{{ section.title || t.card.untitledSection }}</component>
                <span class="summary-minibar" aria-hidden="true">
                  <span v-for="segment in section.segments" :key="segment.state" class="summary-fill" :data-tone="segment.state" :style="{ width: share(segment.share) }" />
                </span>
                <span :ref="keepCount(section.key)" class="summary-section-count" :aria-hidden="board ? undefined : 'true'">{{ section.done }}/{{ section.total }}</span>
                <!-- What a section waits for opens as a list (which ones, their state, on whom); anything else goes to what it counts. -->
                <button v-if="lists(section.readiness)" type="button" class="summary-readiness" :data-readiness="section.readiness.state"
                  :aria-expanded="openSection === section.key || printing" :aria-controls="`${uid}-${section.key}`" @click="toggleSection(section.key)">{{ section.readiness.label }}<AtlasIcon class="summary-chevron" name="chevron-down" :size="13" /></button>
                <component :is="press" v-else :type="pressType" class="summary-readiness" :data-readiness="section.readiness.state" :title="readinessTitle(section.readiness)"
                  @click="go(readinessTarget(section.items, section.readiness))">{{ section.readiness.label }}</component>
                <Transition :css="false" @enter="unfold" @leave="fold">
                  <div v-if="(openSection === section.key || printing) && lists(section.readiness)" :id="`${uid}-${section.key}`" class="summary-prerequisites" data-summary-prerequisites>
                    <component :is="press" v-for="item in section.readiness.prerequisites" :key="item.id" :type="pressType" data-go :data-tone="item.attrs.state"
                      :aria-label="board ? undefined : `${nameOf(item)}: ${stateText(item.attrs)} · ${whom(item)}`" @click="go(item)">
                      <span class="summary-row-title">{{ nameOf(item) }}</span>
                      <span class="summary-prerequisite-state">{{ stateText(item.attrs) }} · {{ whom(item) }}</span>
                    </component>
                  </div>
                </Transition>
              </div>
            </div>
          </section>
        </Transition>
      </div>
    </Transition>
    <!-- What acts on the requirements under the card (their level, the state filter) and on its sorted list (the order), at their boundary. -->
    <footer v-if="viewing && !printing" class="summary-foot">
      <span v-if="!board" class="summary-foot-title"><AtlasIcon name="arrow-down" :size="13" />{{ t.card.listControls }}</span>
      <RequirementViewBar :page-id="viewPage!" part="level" />
      <RequirementViewBar v-if="menus" :page-id="viewPage!" part="menus" :parties="partyOptions" />
    </footer>
    <!-- The chosen order as a list of links (the document keeps its own), under the menus that set it: Tab reaches the filter first. -->
    <Transition :css="false" @enter="unfold" @leave="fold">
      <section v-if="sorted.length && !printing" class="summary-part summary-sorted" data-summary-sorted>
        <div class="summary-rows" role="group" :aria-label="t.card.sortedRequirements" @keydown="walkSorted">
          <button v-for="(item, index) in sorted" :key="item.id" type="button" class="summary-row" data-go :data-tone="item.attrs.state"
            :tabindex="index === sortedAt ? 0 : -1" @focus="sortedAt = index" @click="go(item)">
            <i aria-hidden="true" /><span class="summary-row-title">{{ nameOf(item) }}</span><span class="summary-row-note">{{ sortedBy(item) }}</span>
          </button>
        </div>
      </section>
    </Transition>
    <p v-if="!items.length" class="summary-empty">{{ t.card.empty }}<template v-if="menu">{{ " " + t.card.emptyHint }}</template></p>
  </div>
</template>

<style scoped>
/* One card: the headline, the states as a bar and a legend, the parts under a hairline, the view's strip at the foot. Tones by state: the one map of requirements.css (--rq-tone-*, --tone). */
.summary {
  container-type: inline-size; display: grid; gap: .75rem; margin: .75rem 0 1.25rem; padding: 1rem 1.125rem 1.125rem; border: 1px solid var(--line);
  border-radius: var(--atlas-radius-lg, 12px); background: var(--paper, var(--surface)); box-shadow: 0 1px 2px color-mix(in srgb, var(--text) 5%, transparent);
  font-size: .875rem; line-height: 1.45; user-select: none;
  transition: border-color var(--atlas-motion-slow) var(--atlas-ease-out), background-color var(--atlas-motion-slow) var(--atlas-ease-out);
}
.summary.is-complete { border-color: color-mix(in srgb, var(--rq-tone-done) 45%, var(--line)); background: color-mix(in srgb, var(--rq-tone-done) 6%, var(--paper, var(--surface))); }
.summary-head { display: flex; flex-wrap: wrap; align-items: center; gap: .25rem .625rem; min-height: 1.75rem; color: var(--muted, var(--text-dim)); }
.summary-label { display: inline-flex; align-items: center; gap: .375rem; font-weight: 600; color: var(--muted, var(--text-dim)); letter-spacing: .01em; }
.is-complete .summary-label { color: var(--rq-tone-done); }
.summary-actions { display: inline-flex; flex-wrap: wrap; align-items: center; gap: .25rem .75rem; margin-left: auto; }
/* The strip at the boundary with the requirements it acts on: quiet, the card's full width, the order's menus at its end. */
.summary-foot { display: flex; flex-wrap: wrap; align-items: center; gap: .25rem .75rem; margin: 0 -1.125rem; padding: .375rem 1.125rem;
  border-top: 1px solid color-mix(in srgb, var(--line) 70%, transparent); background: color-mix(in srgb, var(--text) 2.5%, transparent); }
.summary-foot:last-child { margin-bottom: -1.125rem; border-radius: 0 0 var(--atlas-radius-lg, 12px) var(--atlas-radius-lg, 12px); }
.summary-foot-title { display: inline-flex; align-items: center; gap: .375rem; font-size: .8125rem; font-weight: 600; color: var(--muted, var(--text-dim)); white-space: nowrap; }
.summary-foot :deep(.requirement-view-menus) { margin-left: auto; }
.summary-action { display: inline-flex; align-items: center; gap: .375rem; height: 1.75rem; padding: 0 .625rem; border: 1px solid var(--line); border-radius: 999px;
  background: transparent; color: var(--text); font: inherit; font-size: .8125rem; cursor: pointer;
  transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), border-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.summary-menu { display: inline-grid; place-items: center; width: 1.75rem; height: 1.75rem; padding: 0; border: 0; border-radius: 6px; background: transparent; color: inherit; cursor: pointer; }
.summary-progress { display: grid; gap: .75rem; }
.summary-headline { margin: 0; font-size: 1.1875rem; font-weight: 600; line-height: 1.35; color: var(--text); font-variant-numeric: tabular-nums; text-wrap: balance; }
.summary-headline-done { color: var(--rq-tone-done); }
.summary-headline-blocked { color: var(--rq-tone-blocked); }
/* The legend under the bar, the percent and the kinds at its end. */
/* The legend wraps within its own column, so the percent stays at its end instead of falling to a row of its own. */
.summary-figure { display: flex; align-items: center; gap: .5rem 1rem; }
.summary-figure > .summary-counts { flex: 1 1 0; min-width: 0; }
.summary-total { display: grid; justify-items: end; gap: .25rem; margin-left: auto; text-align: right; }
.summary-kinds { color: var(--muted, var(--text-dim)); font-size: .8125rem; font-variant-numeric: tabular-nums; }
.summary-percent { font-size: 2.25rem; font-weight: 700; line-height: 1; letter-spacing: -.02em; color: var(--text); font-variant-numeric: tabular-nums; }
.summary-percent small { font-size: .5em; font-weight: 600; letter-spacing: 0; color: var(--muted, var(--text-dim)); }
/* The bar: segments by state on a track that reads as a bar at 0 % too; the card's own is tall, each segment with its count. */
.summary-bar, .summary-minibar { display: flex; gap: 2px; overflow: hidden; border-radius: 999px; background: color-mix(in srgb, var(--text) 7%, transparent); }
.summary-bar { gap: 3px; height: 2.75rem; border-radius: .75rem; }
.summary-segment { container-type: inline-size; display: flex; align-items: center; padding: 0 .625rem; border-radius: .25rem; overflow: hidden; }
.summary-segment b { color: var(--paper, var(--surface)); font-size: .9375rem; font-weight: 700; font-variant-numeric: tabular-nums; }
.summary-segment[data-tone="planned"] b { color: var(--muted, var(--text-dim)); }
/* A block's own word for its state: its state's tone, hatched, its count on a chip of the paper. */
.summary-segment[data-custom], .summary-counts [data-custom] i {
  background: repeating-linear-gradient(-45deg, var(--tone) 0 3px, color-mix(in srgb, var(--tone) 42%, transparent) 3px 6px); }
.summary-segment[data-custom] { padding: 0 .375rem; }
.summary-segment[data-custom] b { padding: .0625rem .375rem; border-radius: 999px; background: var(--paper, var(--surface)); color: var(--text); font-size: .8125rem; }
/* A segment too narrow for its count shows the colour alone; the legend names it. */
@container (max-width: 1rem) { .summary-segment b { visibility: hidden; } }
/* Each segment fills from its start as the card arrives (scaleX, as meterFill: no layout per frame, F16); a later change of a share
   takes its new length at once. */
.summary-fill { flex: 0 1 auto; min-width: 0; height: 100%; background: var(--tone); transform-origin: left;
  transition: transform var(--atlas-motion-slow) var(--atlas-ease-travel), background-color var(--atlas-motion-slow) var(--atlas-ease-out), opacity var(--atlas-motion-fast) var(--atlas-ease-out); }
.summary:not(.is-grown) .summary-fill { transform: scaleX(0); }
/* Reduced motion: the segments stand at their length at once, only their colour fades. */
@media (prefers-reduced-motion: reduce) { .summary-fill { transition-property: background-color; } }
:global([data-atlas-reduced-motion]) .summary-fill { transition-property: background-color; }
/* Each item: its name, its count big in its tone and its share; a state's own word says what it counts as; a click opens its blocks. */
.summary-counts { display: flex; flex-wrap: wrap; align-items: flex-start; gap: .25rem .5rem; font-size: .8125rem; color: var(--muted, var(--text-dim)); }
.summary-counts > [data-go] { display: grid; grid-template-columns: auto 1fr; align-items: baseline; column-gap: .375rem; padding: .375rem .625rem .4375rem; border: 0;
  border-radius: var(--atlas-radius-md, 8px); background: transparent; color: inherit; font: inherit; text-align: left; cursor: pointer;
  transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.summary-count-name { grid-column: 1 / -1; display: inline-flex; align-items: center; gap: .4375rem; color: var(--text); }
.summary-counts i { flex: none; width: .5625rem; height: .5625rem; border-radius: 50%; background: var(--tone); }
.summary-counts [data-custom] i { box-shadow: inset 0 0 0 1.5px var(--tone); }
.summary-counts b { color: var(--tone); font-size: 1.5rem; font-weight: 700; line-height: 1.15; font-variant-numeric: tabular-nums; }
.summary-counts [data-tone="planned"] b, .summary-counts [data-tone="not_applicable"] b { color: var(--text); }
.summary-count-share { font-size: .8125rem; font-variant-numeric: tabular-nums; }
.summary-count-note { grid-column: 1 / -1; font-size: .75rem; line-height: 1.25; }
.summary-counts > [aria-expanded="true"] { background: var(--hover, color-mix(in srgb, var(--text) 6%, transparent)); }
/* The open segment stands out, the others step back; the list hangs under the legend, its chips in the state's tone. */
.summary:not(.is-board) .summary-segment { cursor: pointer; }
.summary-segment[data-open] { box-shadow: inset 0 0 0 2px color-mix(in srgb, var(--text) 70%, transparent); }
.summary-bar:has([data-open]) .summary-segment:not([data-open]) { opacity: .45; }
.summary-part.summary-state-list { padding-top: .25rem; border-top: 0; }
.summary-state-list [data-go] { background: color-mix(in srgb, var(--tone) 16%, transparent); }
.summary-empty { margin: 0; color: var(--muted, var(--text-dim)); }
/* The parts under the figure, each under a hairline. A label, not a heading of the page: the editor's heading sizes do not reach it. */
.summary-part { display: grid; gap: .375rem; padding-top: .75rem; border-top: 1px solid color-mix(in srgb, var(--line) 70%, transparent); }
.summary-list-title { display: inline-flex; align-items: center; gap: .375rem; font-size: .75rem; font-weight: 600; letter-spacing: .04em; text-transform: uppercase;
  color: var(--muted, var(--text-dim)); }
.summary-list-title[data-tone] > :first-child { color: var(--tone); }
/* A section a row: its name, a small bar, done of all and its readiness, in columns; two lines in a narrow card. */
.summary-sections { display: grid; margin: 0 -.5rem; }
.summary-section { position: relative; display: grid; grid-template-columns: minmax(0, 1fr) minmax(3.5rem, 7rem) 2.75rem minmax(8.5rem, auto); align-items: center;
  gap: .75rem; padding: .3125rem .5rem; border-radius: 8px; transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.summary-section-name { min-width: 0; padding: 0; border: 0; background: none; color: var(--text); font: inherit; font-weight: 500; text-align: left; cursor: pointer;
  overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
/* The name's click covers the row; the readiness stays its own button above it. */
.summary-section-name::after { content: ""; position: absolute; inset: 0; border-radius: inherit; }
.summary-section-context { color: var(--muted, var(--text-dim)); font-weight: 400; }
.summary-minibar { height: .3125rem; }
.summary-section-count { color: var(--muted, var(--text-dim)); font-variant-numeric: tabular-nums; text-align: right; }
.summary-readiness[aria-expanded="true"] .summary-chevron { rotate: 0deg; }
/* What a section waits for, under its row: one line each, its state in its tone. Above the row's own click (the name's ::after). */
.summary-prerequisites { position: relative; z-index: 1; grid-column: 1 / -1; display: grid; gap: 1px; margin: .25rem 0 .125rem; padding-left: .75rem;
  border-left: 2px solid color-mix(in srgb, var(--rq-tone-waiting_for_input) 45%, transparent); }
.summary-prerequisites [data-go] { display: flex; align-items: baseline; gap: .5rem; min-width: 0; padding: .1875rem .5rem; border: 0; border-radius: 6px; background: none;
  color: var(--text); font: inherit; font-size: .8125rem; text-align: left; cursor: pointer; transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.summary-prerequisites .summary-row-title { flex: 0 1 auto; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.summary-prerequisite-state { flex: none; color: var(--tone, var(--muted)); font-size: .75rem; }
.summary-prerequisites [data-go][data-tone="planned"] .summary-prerequisite-state { color: var(--muted, var(--text-dim)); }
.summary-readiness { position: relative; display: inline-flex; align-items: center; gap: .375rem; justify-self: start; margin: 0 -.25rem; padding: .0625rem .25rem; border: 0;
  border-radius: 6px; background: transparent; font: inherit; font-size: .8125rem; color: var(--muted, var(--text-dim)); white-space: nowrap; cursor: pointer; }
.summary-readiness::before { content: ""; flex: none; width: .4375rem; height: .4375rem; border-radius: 50%; background: var(--ready); }
.summary-readiness[data-readiness="done"] { --ready: var(--rq-tone-done); color: var(--rq-tone-done); }
.summary-readiness[data-readiness="ready"] { --ready: var(--rq-tone-in_progress); color: var(--text); }
.summary-readiness[data-readiness="waiting"] { --ready: var(--rq-tone-waiting_for_input); }
.summary-readiness[data-readiness="blocked"] { --ready: var(--rq-tone-blocked); color: var(--rq-tone-blocked); font-weight: 600; }
@container (max-width: 34rem) {
  .summary-section { grid-template-columns: minmax(0, 1fr) auto; grid-template-areas: "name count" "bar ready"; gap: .25rem .75rem; padding-block: .4375rem; }
  /* The headings above go on a line of their own, so a narrow row never cuts the section's own name. */
  .summary-section-name { grid-area: name; white-space: normal; }
  .summary-section-context { display: block; overflow: hidden; font-size: .75rem; text-overflow: ellipsis; white-space: nowrap; }
  .summary-section-count { grid-area: count; }
  .summary-minibar { grid-area: bar; }
  .summary-readiness { grid-area: ready; justify-self: end; }
}
/* What blocks: its title and its note on one line, the note muted. */
.summary-rows { display: grid; margin: 0 -.5rem; }
.summary-row { display: flex; align-items: baseline; gap: .625rem; min-width: 0; padding: .3125rem .5rem; border: 0; border-radius: 8px; background: transparent; color: var(--text);
  font: inherit; text-align: left; cursor: pointer; transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.summary-row-title { flex: none; max-width: 60%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-weight: 600; }
/* The sorted list: as long as the page, so it scrolls in the card, which stays short. */
.summary-sorted .summary-rows { max-height: min(20rem, 50vh); overflow-y: auto; overscroll-behavior: contain; }
/* One line on every width (the narrow card stacks other rows): the dot, the name, what it is sorted by. */
.summary-sorted .summary-row { flex-direction: row; align-items: center; gap: .625rem; }
.summary-sorted .summary-row-title { max-width: 60%; }
.summary-sorted .summary-row > i { flex: none; width: .5rem; height: .5rem; border-radius: 50%; background: var(--tone); }
.summary-row-note { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--muted, var(--text-dim)); }
/* Whom we wait on: a party a line, its blocks as chips. */
.summary-group { display: flex; flex-wrap: wrap; align-items: baseline; gap: .25rem .625rem; }
.summary-party { flex: none; min-width: 7rem; font-weight: 600; color: var(--text); }
.summary-chips { display: flex; flex-wrap: wrap; gap: .25rem; min-width: 0; }
.summary-list [data-go] { max-width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; padding: .0625rem .5rem; border: 0;
  border-radius: 999px; background: color-mix(in srgb, var(--rq-tone-waiting_for_input) 14%, transparent); color: var(--text); font: inherit; font-size: .8125rem; cursor: pointer;
  transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.summary-missing [data-go], .summary-not-asked .summary-chips [data-go] { background: color-mix(in srgb, var(--text) 7%, transparent); }
.summary-days { color: var(--muted, var(--text-dim)); font-variant-numeric: tabular-nums; }
.summary-disclosure { display: inline-flex; align-items: center; gap: .375rem; justify-self: start; padding: 0; border: 0; background: none; color: var(--muted, var(--text-dim));
  font: inherit; font-size: .8125rem; cursor: pointer; }
.summary-disclosure b { color: var(--text); font-variant-numeric: tabular-nums; }
.summary-chevron { rotate: -90deg; transition: rotate var(--atlas-motion-base) var(--atlas-ease-out); }
.summary-disclosure[aria-expanded="true"] .summary-chevron { rotate: 0deg; }
.summary-parts { display: flex; flex-wrap: wrap; align-items: center; gap: .375rem .5rem; padding-top: .75rem; border-top: 1px solid color-mix(in srgb, var(--line) 70%, transparent); }
.summary-parts-title { margin-right: .25rem; font-size: .75rem; font-weight: 600; letter-spacing: .05em; text-transform: uppercase; color: var(--muted, var(--text-dim)); }
.summary-part-note { color: var(--muted, var(--text-dim)); font-size: .78125rem; }
/* Those not asked for yet are a part of the inputs not delivered: they hang on them, smaller. */
.summary-parts .summary-disclosure { white-space: nowrap; }
.summary-parts .summary-disclosure.is-sub { margin-left: -.375rem; font-size: .78125rem; }
/* The parts as soft pills in the tones of the legend above (the breakdown by section in the accent), the open one stronger. */
.summary-parts .summary-disclosure { --pill: var(--tone, var(--accent)); padding: .125rem .5rem .125rem .4375rem; border-radius: 999px;
  background: color-mix(in srgb, var(--pill) 12%, transparent); transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), color var(--atlas-motion-fast) var(--atlas-ease-out); }
.summary-parts .summary-disclosure > :first-child { color: var(--pill); }
.summary-parts .summary-disclosure[data-tone="planned"] > :first-child { color: var(--muted, var(--text-dim)); }
.summary-parts .summary-disclosure[aria-expanded="true"] { background: color-mix(in srgb, var(--pill) 24%, transparent); color: var(--text); }
.summary-all { margin-left: auto; }
/* The open part hangs from the row that opened it. */
.summary-parts ~ .summary-part { padding-top: .25rem; border-top: 0; }
.summary-missing > .summary-list-title { flex-basis: 100%; }
:is(.summary button):focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
@media (hover: hover) and (pointer: fine) {
  .summary-menu:hover, .summary-action:hover, .summary-section:hover, button.summary-row:hover, .summary-prerequisites button:hover, .summary-counts > button:hover { background: var(--hover, color-mix(in srgb, var(--text) 6%, transparent)); }
  button.summary-readiness:hover { text-decoration: underline; }
  .summary-list button:not(.summary-disclosure):hover { background: color-mix(in srgb, var(--rq-tone-waiting_for_input) 24%, transparent); }
  .summary-state-list button:hover { background: color-mix(in srgb, var(--tone) 28%, transparent); }
  .summary-missing button:hover, .summary-not-asked .summary-chips button:hover { background: color-mix(in srgb, var(--text) 12%, transparent); }
  .summary-disclosure:hover { color: var(--text); }
  .summary-parts .summary-disclosure:hover { background: color-mix(in srgb, var(--pill) 20%, transparent); }
}
button.summary-row:active, .summary-action:active { scale: var(--atlas-press, .98); }
/* A touch screen: every button of the card takes the touch target as an invisible area centred on it (D5, states.css: 44 px), its look
   stays (measured 21–28 px). A section's name keeps its own ::after, the whole row. */
@media (pointer: coarse) {
  /* Wrapped rows of the legend and the parts stand 40 px apart, or a row's areas take the half of the one above. */
  .summary-counts, .summary-parts { row-gap: 1.25rem; }
  .summary button:not(.summary-section-name) { position: relative; }
  .summary button:not(.summary-section-name)::after {
    content: ""; position: absolute; top: 50%; left: 50%; width: max(100%, var(--touch-target, 44px)); height: max(100%, var(--touch-target, 44px)); translate: -50% -50%;
  }
}
/* On a board a block of another page is not shown from here: its rows, chips and legend are plain text (spans): no hover, no pointer, no Tab stop. */
.is-board :is([data-go], span.summary-section-name, span.summary-readiness) { cursor: default; }
.is-board .summary-section:hover { background: none; }
@container (max-width: 30rem) {
  .summary-action span { position: absolute; width: 1px; height: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }
  .summary-action { width: 1.75rem; padding: 0; justify-content: center; }
  .summary-row { flex-direction: column; gap: 0; }
  .summary-row-title { max-width: 100%; }
  .summary-row-note { max-width: 100%; }
  .summary-party { min-width: 0; }
  .summary-headline { font-size: 1.0625rem; }
  .summary-part-note { display: none; }
  .summary-bar { height: 2.125rem; }
  /* The percent above the legend, which takes two items a row. */
  .summary-figure { flex-wrap: wrap; }
  .summary-total { order: -1; justify-items: start; margin-left: 0; text-align: left; }
  .summary-figure > .summary-counts { display: grid; grid-template-columns: 1fr 1fr; flex-basis: 100%; }
}
</style>
