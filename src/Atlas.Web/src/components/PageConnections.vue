<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, useId, watch } from "vue";
import type { MarkdownRichValue } from "@atlas/markdown/core";
import type { MarkdownPageSource } from "@atlas/markdown/plugins/pages";
import { createPageSource, pageIdFromUrl } from "../editorDirectories";
import { animateAtlas, motionMs, riseIn, RISE_IN_ROWS } from "../motion/aiMotion";
import { LINKS_HERE_MORE, pageConnectionsApi, placesOf, type LinkingPage, type PageConnectionsApi, type PagePlace } from "../pageConnections";
import { boardLink, pageLink } from "../pageLinks";
import { plainTitle } from "../objects/relationsApi";
import { Button } from "./ui/button";
import AtlasIcon from "./AtlasIcon.vue";
import LoadingSkeleton from "./LoadingSkeleton.vue";
import EmptyState from "./settings/EmptyState.vue";
import t from "../i18n/components-search";

/**
 * R2 "Odkazuje sem", what a published page being read is linked with: the pages that link here and the references in its text. It is
 * a plain rule (a projection of page references the reader may open), so it carries no AI glyph and never twinkles or shimmers.
 * Nothing is shown while it loads, on an error or a 404, so it never makes the page jump.
 *
 * R1 "Související" (phase 2) is pages similar in topic, embedding-based and not model output, so it has no AI glyph either (a type
 * icon only). The server decides whether it exists: while `related` is missing or empty it is left out and nothing is said. Only the
 * first render of a page animates (the related tags 90 ms after the first group, like the prototype); a refresh after a publication
 * swaps the data without moving anything.
 *
 * The host mounts it only while reading. Editing, the public share (/s/<token>) and the history preview are other
 * surfaces and never render it.
 * `layout="graph"` presents actual incoming/outgoing references around the current article, six at a time. Topic similarity
 * remains a separate list. A successful empty load has an explicit empty state; unreadable pages and failures stay hidden.
 * `layout="list"` is „Odtud dál“ of the page dock (refresh 2.5, board 03): the same references and similar pages as one
 * column of quiet cards, each naming how it relates (Odkaz z textu, Odkazuje sem, Vzájemné odkazy, Podobné tématem). The column
 * holds its place with a skeleton until the first answer, says EmptyState when nothing connects, and leaves only on a failure.
 */
const props = withDefaults(defineProps<{
  pageId: string;
  /** Graph for exploring actual article links; list is the dock column. */
  layout: "graph" | "list";
  pageTitle?: string;
  document?: MarkdownRichValue;
  pageSource?: Pick<MarkdownPageSource, "resolve">;
  /** The page's own space: its name is shown only for pages of other spaces. Without it every card names its space. */
  spaceId?: string;
  /** Changes whenever the host's data may have changed (a publication, a save); the list is read again quietly. */
  refreshKey?: number;
  /** The host hides related pages immediately while AI is unavailable and refreshes them on recovery. */
  relatedEnabled?: boolean;
  api?: PageConnectionsApi;
}>(), { api: () => pageConnectionsApi, spaceId: undefined, refreshKey: 0, relatedEnabled: true });

const headingId = `links-here-${useId()}`;
const items = ref<LinkingPage[]>([]);
const total = ref(0);
const related = ref<LinkingPage[]>([]);
const list = ref<HTMLElement>();
const relatedList = ref<HTMLElement>();
const loaded = ref(false);
/** The first answer is still on its way: the dock column holds the section's place with a skeleton (F2, nothing pushed down). */
const pending = ref(true);
/** The links in the text are still being checked: „nothing connects“ waits for them, so it never flips to a card a moment later. */
const resolving = ref(false);
/** The cards on screen are the previous page's until its own arrive: shown quietened, not as this page's. */
const stale = ref(false);
const outgoing = ref<{ id: string; title: string }[]>([]);
/** The boards that show the page, the contacts it mentions, its deployment board (GET /api/objects/{id}/relations). */
const places = ref<PagePlace[]>([]);
const graph = ref<HTMLElement>();
const graphCenter = ref<HTMLElement>();
const graphEdges = ref<{ id: string; d: string }[]>([]);
const verifiedPages = createPageSource(() => []);
let outgoingRequest: AbortController | undefined;
let graphObserver: ResizeObserver | undefined;
let request: AbortController | undefined;
let animated = false;
let relatedAnimated = false;

/** The second column starts later than the first (the prototype's 90 ms). */
const RELATED_DELAY = 90;

const hasRelated = computed(() => props.relatedEnabled && related.value.length > 0);
interface GraphNode { id: string; typeId?: string; title: string; spaceId: string; spaceName: string; relation: string; icon?: string }
const graphNodes = computed<GraphNode[]>(() => {
  const nodes = new Map<string, GraphNode>(items.value.map(item => [item.id.toLowerCase(), { ...item, relation: t.connections.linksHere }]));
  for (const item of outgoing.value) {
    const id = item.id.toLowerCase();
    const incoming = nodes.get(id);
    nodes.set(id, { ...item, spaceId: incoming?.spaceId ?? "", spaceName: incoming?.spaceName ?? "", relation: incoming ? t.connections.mutual : t.connections.inPage });
  }
  nodes.delete(props.pageId.toLowerCase());
  const placed = places.value.map(place => ({ id: place.id, typeId: place.typeId, title: plainTitle(place.title), spaceId: place.spaceId, spaceName: place.spaceName, relation: place.relation, icon: place.icon }));
  return [...nodes.values(), ...placed];
});
/** Pages and boards open; a contact has no page of its own, so it is named, not linked. */
const hrefOf = (item: { id: string; typeId?: string }) => (item.typeId === "atlas.board" ? boardLink(item.id) : !item.typeId || item.typeId === "atlas.page" ? pageLink(item.id) : undefined);
/** The dock column: references first (as the graph names them; the places beyond links come with their own words and icon), then similar pages; six until „Zobrazit další“. */
const LIST_KINDS: Record<string, { label: string; icon: string }> = {
  [t.connections.inPage]: { label: t.connections.fromText, icon: "arrow-right" },
  [t.connections.linksHere]: { label: t.connections.linksHere, icon: "undo-2" },
  [t.connections.mutual]: { label: t.connections.mutual, icon: "arrow-right-left" },
};
const listItems = computed(() => [
  ...graphNodes.value.map(item => ({ ...item, kind: LIST_KINDS[item.relation]?.label ?? item.relation, icon: item.icon ?? LIST_KINDS[item.relation]?.icon ?? "link" })),
  ...(hasRelated.value ? related.value.filter(item => !graphNodes.value.some(node => node.id.toLowerCase() === item.id.toLowerCase()))
    .map(item => ({ ...item, relation: t.connections.similar, kind: t.connections.similar, icon: "compass" })) : []),
]);
/** How a card relates to the page, and the space it lives in when that is another one. The column cuts it, so it is also its tooltip. */
const relationLine = (item: { kind: string; spaceId: string; spaceName: string }) =>
  item.spaceName && (!props.spaceId || item.spaceId !== props.spaceId) ? `${item.kind} · ${item.spaceName}` : item.kind;
const listExpanded = ref(false);
const shownList = computed(() => listExpanded.value ? listItems.value : listItems.value.slice(0, 6));
async function expandList() {
  const before = shownList.value.length;
  listExpanded.value = true;
  await nextTick();
  // The added rows rise with the section (the list watcher below); focus moves to the first of them, the pressed button is gone.
  ([...(list.value?.children ?? [])][before]?.querySelector("a") as HTMLElement | null | undefined)?.focus();
}

/** Resolve only actual references through the same permission check as rendered document links. */
async function loadOutgoing() {
  outgoingRequest?.abort();
  outgoing.value = [];
  resolving.value = false;
  if (!props.document) return;
  const current = outgoingRequest = new AbortController();
  const ids = new Set<string>();
  type ReferenceNode = { type?: string; attrs?: { id?: unknown; href?: unknown }; marks?: ReferenceNode[]; content?: ReferenceNode[] };
  function visit(node: ReferenceNode) {
    if (node.type === "pageReference" && typeof node.attrs?.id === "string") ids.add(node.attrs.id.toLowerCase());
    for (const mark of node.marks ?? []) {
      if (mark.type !== "link" || typeof mark.attrs?.href !== "string") continue;
      try {
        const id = pageIdFromUrl(new URL(mark.attrs.href, location.href).href);
        if (id) ids.add(id.toLowerCase());
      } catch { /* An invalid URL is not an Atlas page reference. */ }
    }
    node.content?.forEach(visit);
  }
  visit(props.document.document as ReferenceNode);
  ids.delete(props.pageId.toLowerCase());
  resolving.value = ids.size > 0;
  const resolved = await Promise.all([...ids].map(async id => {
    try { return await (props.pageSource ?? verifiedPages).resolve?.(id, { signal: current.signal }); }
    catch { return null; }
  }));
  if (!current.signal.aborted) { outgoing.value = resolved.filter((item): item is NonNullable<typeof item> => !!item); resolving.value = false; }
}

/** Measure real wrapped titles, including the single-column mobile layout. */
function drawGraph() {
  if (!graph.value || !graphCenter.value) return;
  const bounds = graph.value.getBoundingClientRect();
  const center = graphCenter.value.getBoundingClientRect();
  const narrow = bounds.width <= 480;
  graphEdges.value = [...graph.value.querySelectorAll<HTMLElement & { href?: string }>(".connections-graph-node")].map(node => {
    const box = node.getBoundingClientRect();
    const right = box.left > center.left;
    const x1 = (narrow ? center.left : right ? center.right : center.left) - bounds.left;
    const y1 = (narrow ? center.bottom : center.top + center.height / 2) - bounds.top;
    const x2 = (narrow ? box.left : right ? box.left : box.right) - bounds.left;
    const y2 = box.top + box.height / 2 - bounds.top;
    const bend = narrow ? 8 : (x1 + x2) / 2;
    return { id: node.dataset.node ?? node.href ?? "", d: `M ${x1} ${y1} C ${bend} ${y1}, ${bend} ${y2}, ${x2} ${y2}` };
  });
}

// Another page keeps the previous page's cards until its own arrive (load replaces them): clearing here made the dock's section
// vanish for a moment and the whole right column jump (MOT1-4).
function reset() {
  request?.abort();
  stale.value = props.layout === "list";
  listExpanded.value = false;
  animated = false;
  relatedAnimated = false;
}

/** `fresh`: a refresh, never an answer kept from the other view of this page (pageConnections.ts). */
async function load(fresh = false) {
  request?.abort();
  const current = request = new AbortController();
  try {
    // The places beyond links come with the same answer; a failure there leaves them out, never the links.
    const [data, relations] = await Promise.all([
      props.api.get(props.pageId, LINKS_HERE_MORE, current.signal, fresh),
      props.api.relations(props.pageId, current.signal, fresh).catch(() => null),
    ]);
    if (current.signal.aborted) return;
    places.value = placesOf(props.pageId, relations);
    const linksHere = data?.linksHere;
    items.value = linksHere?.items ?? [];
    total.value = linksHere?.total ?? 0;
    related.value = data?.related?.items ?? [];
    loaded.value = !!data;
    await nextTick();
    if (current.signal.aborted) return;
    if (props.layout === "list") {
      animated = true; // the column's rows arrive through the list watcher below, whichever answer brings them
    } else {
      if (!animated && graph.value) { animated = true; riseIn(graph.value.querySelectorAll(".connections-graph-node")); }
      drawGraph();
      if (hasRelated.value && !relatedAnimated) arriveRelated();
    }
  } catch {
    if (current.signal.aborted) return;
    // Any failure hides the section.
    items.value = []; total.value = 0; related.value = []; places.value = []; loaded.value = false;
  } finally { if (request === current) { stale.value = false; pending.value = false; } }
}

/** Prototype "Čtení stránky", moment 5: the related tags rise 90 ms after the first group starts. */
function arriveRelated() {
  relatedAnimated = true;
  riseIn(relatedList.value?.children ?? [], { delay: RELATED_DELAY });
}

/**
 * The dock column: the section unfolds when it first has something to say and grows with the rows that the links here
 * and the links in the article bring one after the other. What is below it in the dock is moved, never jumped.
 */
const listSection = ref<HTMLElement>();
/** What the reader has seen of the section so far: its settled height and its rows. */
let listHeight = 0, listRows = 0, listMotion: Animation | null = null;
watch([listSection, () => shownList.value.length, loaded, () => listItems.value.length > 6 && !listExpanded.value], async () => {
  if (props.layout !== "list") return;
  await nextTick();
  const section = listSection.value;
  if (!section) { listHeight = 0; listRows = 0; listMotion = null; return; }
  // The content's own height, also while the box is still unfolding to an earlier one.
  const height = section.scrollHeight, rows = list.value?.children.length ?? 0;
  // The section stands from the first frame (its skeleton): only a change of its height eases, nothing unfolds from nothing.
  if (listHeight && height !== listHeight) {
    // A change that arrives while the section is still moving continues from where it is.
    const from = section.offsetHeight;
    listMotion?.cancel();
    listMotion = animateAtlas(section, [{ height: `${from}px`, overflow: "clip" }, { height: `${height}px`, overflow: "clip" }], { duration: motionMs(section, "--atlas-motion-base"), fill: "none" });
  }
  if (rows > listRows) riseIn([...list.value!.children].slice(listRows), RISE_IN_ROWS);
  listHeight = height; listRows = rows;
});

watch(() => props.pageId, () => { reset(); void load(); }, { immediate: true });
watch(() => props.refreshKey, () => { void load(true); });
watch(() => [props.layout, props.pageId, props.document, props.refreshKey], () => { void loadOutgoing(); }, { immediate: true });
watch(graphNodes, async () => { await nextTick(); drawGraph(); });
watch(graph, element => {
  graphObserver?.disconnect();
  if (element && typeof ResizeObserver !== "undefined") { graphObserver = new ResizeObserver(drawGraph); graphObserver.observe(element); }
});
watch(() => props.relatedEnabled, (enabled) => {
  related.value = [];
  if (enabled) void load(true);
});
onBeforeUnmount(() => { request?.abort(); outgoingRequest?.abort(); graphObserver?.disconnect(); });
</script>

<template>
  <section v-if="layout === 'list' && (loaded || pending || listItems.length > 0)" ref="listSection" class="page-connections-list" :class="{ 'is-stale': stale }" :aria-busy="stale" :aria-labelledby="headingId" data-connections-list>
    <h2 :id="headingId" class="connections-list-heading">{{ t.connections.onwards }}</h2>
    <ul v-if="listItems.length" ref="list" class="connections-list">
      <li v-for="item in shownList" :key="`${item.kind}-${item.id}`">
        <component :is="hrefOf(item) ? 'a' : 'span'" class="connections-list-card" :href="hrefOf(item)">
          <AtlasIcon :name="item.icon" :size="15" aria-hidden="true" />
          <span class="connections-list-copy">
            <span class="connections-list-title" :title="item.title">{{ item.title }}</span>
            <small :title="relationLine(item)">{{ relationLine(item) }}</small>
          </span>
        </component>
      </li>
    </ul>
    <EmptyState v-else-if="loaded && !resolving" compact icon="link" :title="t.connections.emptyTitle" :text="t.connections.emptyText" data-connections-empty />
    <LoadingSkeleton v-else :rows="2" row-height="56px" :label="t.connections.loading" />
    <Button v-if="!listExpanded && listItems.length > 6" variant="ghost" size="sm" class="connections-list-more" @click="expandList">{{ t.connections.showMore(listItems.length - 6) }}</Button>
  </section>
  <section v-else-if="layout === 'graph' && loaded && (graphNodes.length > 0 || hasRelated)" class="page-connections page-connections-graph" :aria-label="t.connections.label">
    <div class="connections-bar">
      <ul ref="relatedList" class="connections-tags" :aria-label="t.connections.relatedPages">
        <li v-for="item in graphNodes.slice(0, 6)" :key="`${item.relation}-${item.id}`"><component :is="hrefOf(item) ? 'a' : 'span'" class="links-here-card" :href="hrefOf(item)" :title="item.relation"><AtlasIcon :name="item.icon ?? 'link'" :size="12" aria-hidden="true" /><span>{{ item.title }}</span></component></li>
        <li v-for="item in hasRelated ? related : []" :key="`related-${item.id}`"><a class="links-here-card" :href="pageLink(item.id)" :title="t.connections.similarIn(item.spaceName)"><AtlasIcon name="page" :size="12" aria-hidden="true" /><span>{{ item.title }}</span></a></li>
      </ul>
      <details v-if="graphNodes.length" :key="pageId" class="connections-map" @toggle="drawGraph()">
      <summary><AtlasIcon name="network" :size="14" aria-hidden="true" />{{ t.connections.map }}<span>{{ graphNodes.length }}</span></summary>
      <div v-if="graphNodes.length" ref="graph" class="connections-graph">
        <svg class="connections-graph-lines" aria-hidden="true"><path v-for="edge in graphEdges" :key="edge.id" :d="edge.d" /></svg>
        <ul v-for="side in [0, 1]" :key="side" class="connections-graph-side" :class="{ 'connections-graph-side-right': side === 1 }" :aria-label="side === 0 ? t.connections.left : t.connections.right">
          <li v-for="item in graphNodes.filter((_, index) => index % 2 === side)" :key="`${item.relation}-${item.id}`">
            <component :is="hrefOf(item) ? 'a' : 'div'" class="connections-graph-node" :href="hrefOf(item)" :data-node="`${item.relation}-${item.id}`">
              <small class="connections-graph-kind">{{ item.relation }}</small>
              <span>{{ item.title }}</span>
              <small v-if="item.spaceName && (!spaceId || item.spaceId !== spaceId)" class="links-here-space">{{ item.spaceName }}</small>
            </component>
          </li>
        </ul>
        <div ref="graphCenter" class="connections-graph-center"><small>{{ t.connections.reading }}</small>{{ pageTitle || t.connections.thisPage }}</div>
      </div>

      <p v-if="total > items.length" class="connections-graph-description">{{ t.connections.graphLimit(items.length, total) }}</p>
      </details>
    </div>
  </section>
</template>

<style scoped>
/* The prototype's .foot / .lcard. */
.page-connections { width: min(960px, 100%); margin: 30px auto 0; box-sizing: border-box; padding: 0 40px 36px; }
.links-here-card {
  display: inline-flex; max-width: 100%; gap: 5px; align-items: center; padding: 4px 8px; border: 0; border-radius: 6px; font-size: 12px; line-height: 1.5;
  /* A shade quieter than a full hover fill: the chips are a footnote of the article, not its controls. */
  background: color-mix(in srgb, var(--hover) 62%, transparent); color: var(--muted); text-decoration: none;
  transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), color var(--atlas-motion-fast) var(--atlas-ease-out);
}
@media (hover: hover) and (pointer: fine) { .links-here-card:hover { background: var(--accent-soft); color: var(--text); } }
.links-here-card:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.links-here-card :deep(svg) { flex: none; color: var(--muted); opacity: .8; }
.links-here-space { overflow-wrap: anywhere; font-size: 11.5px; color: var(--muted); }
@media (max-width: 680px) { .page-connections { padding-inline: 20px; } }
.page-connections-graph { container-type: inline-size; }
.connections-bar { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 12px; padding-top: 12px; border-top: 1px solid var(--line); }
.connections-tags { display: flex; flex-wrap: wrap; gap: 6px; list-style: none; padding: 0; margin: 0; }
.connections-tags li { min-width: 0; max-width: 100%; }
.connections-tags a span { overflow-wrap: anywhere; }
.connections-map { min-width: 0; }
.connections-map summary { display: inline-flex; align-items: center; gap: 6px; cursor: pointer; color: var(--muted); font-size: 12px; border-radius: 5px; padding: 3px 0; list-style: none; }
.connections-map summary::-webkit-details-marker { display: none; }
.connections-map summary:focus-visible { outline: 2px solid var(--accent); outline-offset: 3px; }
.connections-map[open] summary { color: var(--text); }
@media (hover: hover) and (pointer: fine) { .connections-map summary:hover { color: var(--text); } }
.connections-map summary span { font-size: 10px; }
/* Behind its button the map can take the footer's whole width and show every connection at once; it is never paged. */
.connections-map[open] { flex-basis: 100%; }
/* The map rises in when it opens; where the browser animates the content of <details> it also fades out on closing. */
.connections-map[open] > .connections-graph { animation: st-rise-in var(--atlas-motion-slow) var(--atlas-ease-out) both; }
.connections-map:not([open])::details-content { opacity: 0; transition: opacity var(--atlas-motion-base) var(--atlas-ease-out), content-visibility var(--atlas-motion-base) allow-discrete; }
.connections-graph-description { margin: 0; color: var(--muted); font-size: 12px; }
.connections-graph { position: relative; display: grid; grid-template-columns: minmax(0, 1fr) minmax(112px, .6fr) minmax(0, 1fr); gap: 16px; align-items: center; padding: 12px 0; }
.connections-graph-lines { position: absolute; inset: 0; width: 100%; height: 100%; pointer-events: none; overflow: visible; }
.connections-graph-lines path { fill: none; stroke: var(--line); stroke-width: 1.5; }
.connections-graph-side { position: relative; display: grid; gap: 12px; grid-column: 1; grid-row: 1; list-style: none; margin: 0; padding: 0; min-width: 0; }
.connections-graph-side-right { grid-column: 3; }
.connections-graph-node { position: relative; display: block; padding: 8px; border-radius: 6px; background: var(--surface); color: var(--text); text-decoration: none; overflow-wrap: anywhere; font-size: 12px; line-height: 1.5; transition: background var(--atlas-motion-fast) var(--atlas-ease-out); }
.connections-graph-node::before { content: ''; position: absolute; right: -3px; top: calc(50% - 3px); width: 6px; height: 6px; border-radius: 50%; background: var(--muted); }
.connections-graph-side-right .connections-graph-node::before { right: auto; left: -3px; }
@media (hover: hover) and (pointer: fine) { .connections-graph-node:hover { background: var(--hover); } }
.connections-graph-node:focus-visible { background: var(--hover); outline: 2px solid var(--accent); outline-offset: 2px; }
.connections-graph-kind, .connections-graph-center small { display: block; margin-bottom: 4px; color: var(--muted); font-size: 10px; }
.connections-graph-node .links-here-space { display: block; margin-top: 4px; }
.connections-graph-center { position: relative; grid-column: 2; grid-row: 1; padding: 8px; border-radius: 10px; background: var(--hover); text-align: center; font-size: 13px; overflow-wrap: anywhere; }
@container (max-width: 480px) {
  .connections-graph { grid-template-columns: minmax(0, 1fr); padding-left: 28px; gap: 18px; }
  .connections-graph-center { grid-column: 1; grid-row: 1; }
  .connections-graph-side { grid-column: 1; grid-row: 2; gap: 12px; }
  .connections-graph-side-right { grid-row: 3; }
  .connections-graph-node::before { left: -3px; right: auto; }
}
@media (prefers-reduced-motion: reduce) {
  .connections-graph-node { transition: none; }
}
/* The dock column (board 03): quiet white cards, the relation under the title. */
.page-connections-list { display: flex; flex-direction: column; gap: 6px; min-width: 0; transition: opacity var(--atlas-motion-base) var(--atlas-ease-out); }
.page-connections-list.is-stale { opacity: .5; pointer-events: none; }
.connections-list-heading { margin: 0; padding: 0 8px; color: var(--muted); font-size: 11px; font-weight: 700; letter-spacing: 0.06em; text-transform: uppercase; }
.connections-list { display: flex; flex-direction: column; gap: 6px; margin: 0; padding: 0; list-style: none; }
.connections-list li { min-width: 0; }
.connections-list-card {
  display: flex; align-items: center; gap: 10px; padding: 8px 10px; border: 1px solid color-mix(in srgb, var(--line) 80%, transparent); border-radius: 10px;
  background: var(--surface); color: var(--text); text-decoration: none;
  transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out), box-shadow var(--atlas-motion-fast) var(--atlas-ease-out);
}
.connections-list-card > svg { flex: none; color: var(--muted); }
/* Hover moves nothing (a lifted card left the pointer on its lower edge and flickered): border and shadow answer. */
@media (hover: hover) and (pointer: fine) {
  .connections-list-card:hover { border-color: var(--line); box-shadow: var(--refresh-card-shadow, none); }
}
.connections-list-card:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.connections-list-copy { display: flex; flex-direction: column; min-width: 0; }
.connections-list-title { overflow: hidden; font-weight: 500; text-overflow: ellipsis; white-space: nowrap; }
.connections-list-copy small { overflow: hidden; color: var(--muted); font-size: 12px; text-overflow: ellipsis; white-space: nowrap; }
.connections-list-more { align-self: flex-start; }
</style>
