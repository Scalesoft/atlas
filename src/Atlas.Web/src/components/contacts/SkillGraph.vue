<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from "vue";
import t from "../../i18n/contacts-ui";
import { errorMessage } from "../../api";
import { foldText } from "../../lib/foldText";
import { prefersReducedMotion } from "../../motion/aiMotion";
import { topicUrl } from "../../contacts/contactLinking";
import { skillApi as defaultApi, type SkillApi, type SkillGraph, type SkillNode } from "../../contacts/skillApi";
import AtlasIcon from "../AtlasIcon.vue";
import LoadingSkeleton from "../LoadingSkeleton.vue";
import EmptyState from "../settings/EmptyState.vue";
import ErrorState from "../settings/ErrorState.vue";
import FilterMenu from "../settings/FilterMenu.vue";
import OptionPills from "../settings/OptionPills.vue";
import SearchField from "../settings/SearchField.vue";
import ContactCard from "./ContactCard.vue";
import ContactSkillLine from "./ContactSkillLine.vue";
import "../../page-tags.css";

/**
 * Mapa znalostí as a graph (skills spec §7), the third view of Knihovna → Kontakty beside Adresář and Celá wiki: people and the topics they
 * know (the line's strength the level the reader may see), topics linked as part of or related. Filters: organization (of the people), space
 * (the topics of its pages, asked of the server) and a topic's text (it, its linked topics and their people). A topic opens its dashboard
 * (`#/library/contacts?q=`), a person opens the person's card beneath (ContactCard with the skill bars). Drawn with ECharts as
 * HistoryActivityChart does (loaded with the view; the editor's chart renderer takes a table only). The list is the same graph for the
 * keyboard and narrow rooms: under 680 px of room only the list shows. `api` and `data` are the gallery's.
 */
const props = defineProps<{ spaces?: readonly { id: string; name: string }[]; api?: Pick<SkillApi, "graph" | "contact" | "rate" | "topics">; data?: SkillGraph }>();
const api = computed(() => props.api ?? defaultApi);
const graph = ref<SkillGraph | null>(props.data ?? null);
const loading = ref(!props.data), error = ref("");
const organization = ref(""), space = ref(""), topicText = ref(""), view = ref<"graph" | "list">("graph");
const selected = ref<SkillNode | null>(null);
const root = ref<HTMLElement>(), host = ref<HTMLElement>(), probe = ref<HTMLElement>();
const narrow = ref(false);
let chart: import("echarts/core").EChartsType | undefined, observer: ResizeObserver | undefined, disposed = false;

async function load() {
  if (props.data) return;
  loading.value = true; error.value = "";
  try { graph.value = await api.value.graph(space.value || null); }
  catch (failure) { error.value = errorMessage(failure); }
  finally { loading.value = false; }
}
watch(space, () => void load());

const organizations = computed(() => [...new Set((graph.value?.nodes ?? []).filter(node => node.kind === "person" && node.organization).map(node => node.organization!))]
  .sort((a, b) => a.localeCompare(b)).map(name => ({ value: name, label: name })));
const all = computed(() => ({ value: "", label: t.skills.everyone }));
const organizationOptions = computed(() => [all.value, ...organizations.value]);
const spaceOptions = computed(() => (props.spaces ?? []).length ? [all.value, ...(props.spaces ?? []).map(item => ({ value: item.id, label: item.name }))] : []);
/** The graph the filters leave: people of the organization; a topic's text keeps the topics it names, the topics linked to them and their people. */
const shown = computed(() => {
  const source = graph.value;
  if (!source) return { nodes: [] as SkillNode[], links: [] as SkillGraph["links"] };
  let people = source.nodes.filter(node => node.kind === "person" && (!organization.value || node.organization === organization.value));
  let topics = source.nodes.filter(node => node.kind === "topic");
  const text = foldText(topicText.value.trim());
  if (text) {
    const named = new Set(topics.filter(node => foldText(node.name).includes(text)).map(node => node.id));
    const near = new Set(named);
    for (const link of source.links) if (link.kind !== "skill" && (named.has(link.source) || named.has(link.target))) { near.add(link.source); near.add(link.target); }
    topics = topics.filter(node => near.has(node.id));
  }
  const topicIds = new Set(topics.map(node => node.id)), personIds = new Set(people.map(node => node.id));
  const links = source.links.filter(link => link.kind === "skill" ? personIds.has(link.source) && topicIds.has(link.target) : topicIds.has(link.source) && topicIds.has(link.target));
  const linked = new Set(links.filter(link => link.kind === "skill").flatMap(link => [link.source, link.target]));
  people = people.filter(node => linked.has(node.id));
  topics = topics.filter(node => linked.has(node.id) || links.some(link => link.kind !== "skill" && (link.source === node.id || link.target === node.id)));
  return { nodes: [...topics, ...people], links };
});
const counts = computed(() => ({ people: shown.value.nodes.filter(node => node.kind === "person").length, topics: shown.value.nodes.filter(node => node.kind === "topic").length }));
/** The list: topics, most people first, each with its people (the best first). */
const rows = computed(() => {
  const byId = new Map(shown.value.nodes.map(node => [node.id, node]));
  return shown.value.nodes.filter(node => node.kind === "topic").map(topic => ({
    topic,
    people: shown.value.links.filter(link => link.kind === "skill" && link.target === topic.id).sort((a, b) => (b.level ?? 0) - (a.level ?? 0))
      .map(link => ({ node: byId.get(link.source)!, level: link.level ?? null })).filter(entry => entry.node),
  })).filter(row => row.people.length).sort((a, b) => b.people.length - a.people.length || a.topic.name.localeCompare(b.topic.name));
});
const showGraph = computed(() => !narrow.value && view.value === "graph");

function open(node: SkillNode) {
  if (node.kind === "topic") location.hash = topicUrl(node.name);
  else selected.value = node;
}

const css = (name: string, fallback: string) => (root.value ? getComputedStyle(root.value).getPropertyValue(name).trim() : "") || fallback;
function option() {
  const accent = css("--accent", "#006f84"), text = css("--text", "#142f3b"), muted = css("--muted", "#4e6874"), line = css("--line", "#c5dade"), surface = css("--surface", "#fff");
  // A topic is a page tag: its ink is the tags' (page-tags.css, the theme's light or dark value), read from a probe of the violet tone.
  const crowded = shown.value.nodes.length > 80;
  const topicInk = (probe.value ? getComputedStyle(probe.value).getPropertyValue("--tag-ink").trim() : "") || "#7859a7";
  return {
    animation: !prefersReducedMotion(root.value), animationDurationUpdate: 240,
    tooltip: { formatter: (params: { dataType: string; data: { name: string; detail?: string } }) => params.dataType === "node" ? [params.data.name, params.data.detail].filter(Boolean).join("<br/>") : "" },
    series: [{
      type: "graph", layout: "force", roam: true, draggable: true, label: { show: true, position: "right", color: text, fontSize: 11 },
      // Hundreds of nodes (§7a): a tighter layout, and people's names on hover only, so the topics stay readable.
      force: crowded ? { repulsion: 70, edgeLength: [25, 70], gravity: 0.25 } : { repulsion: 320, edgeLength: [80, 170], gravity: 0.05 },
      emphasis: { focus: "adjacency", lineStyle: { width: 3 }, label: { show: true } },
      data: shown.value.nodes.map(node => ({ id: node.id, name: node.name, detail: node.kind === "topic" ? t.skills.knownBy(node.people) : [node.title, node.organization].filter(Boolean).join(" · "),
        symbol: node.kind === "topic" ? "roundRect" : "circle", symbolSize: node.kind === "topic" ? (crowded ? 8 + Math.min(10, node.people) : 14 + Math.min(18, node.people * 3)) : crowded ? 7 : 12,
        itemStyle: node.kind === "topic" ? { color: topicInk } : { color: surface, borderColor: accent, borderWidth: 2 },
        label: { show: !crowded || node.kind === "topic" }, node })),
      links: shown.value.links.map(link => ({ source: link.source, target: link.target,
        lineStyle: link.kind === "skill" ? { color: accent, opacity: 0.25 + 0.15 * (link.level ?? 1), width: 1 + (link.level ?? 1) / 2 }
          : { color: link.kind === "related" ? muted : topicInk, type: link.kind === "related" ? "dashed" : "solid", width: 1.5, opacity: 0.8 } })),
      lineStyle: { color: line, curveness: 0.1 },
    }],
  };
}
async function draw() {
  if (!showGraph.value || !host.value || disposed) return;
  if (!chart) {
    const [core, charts, components, renderers] = await Promise.all([import("echarts/core"), import("echarts/charts"), import("echarts/components"), import("echarts/renderers")]);
    if (disposed || !host.value) return;
    core.use([charts.GraphChart, components.TooltipComponent, renderers.SVGRenderer]);
    chart = core.init(host.value, undefined, { renderer: "svg" });
    chart.on("click", event => { const node = (event.data as { node?: SkillNode } | null)?.node; if (event.dataType === "node" && node) open(node); });
  }
  chart.setOption(option(), true);
}
watch([shown, showGraph], () => { if (!showGraph.value) { chart?.dispose(); chart = undefined; } else void Promise.resolve().then(draw); }, { flush: "post" });
onMounted(async () => {
  observer = new ResizeObserver(entries => { narrow.value = (entries[0]?.contentRect.width ?? 1000) < 680; chart?.resize(); });
  if (root.value) observer.observe(root.value);
  await load();
  void draw();
});
onBeforeUnmount(() => { disposed = true; observer?.disconnect(); chart?.dispose(); });
const views = computed(() => [{ value: "graph", label: t.skills.viewGraph, icon: "network" }, { value: "list", label: t.skills.viewList, icon: "list" }]);
</script>

<template>
  <section ref="root" class="skill-graph tags-ui" :aria-label="t.skills.graphTitle" :aria-busy="loading" data-skill-graph>
    <span ref="probe" data-tone="violet" hidden />
    <div class="skill-graph-toolbar">
      <SearchField v-model="topicText" :label="t.skills.filterTopic" :placeholder="t.skills.filterTopicPlaceholder" />
      <FilterMenu v-model="organization" :label="t.skills.filterOrganization" :options="organizationOptions" icon="users" />
      <FilterMenu v-if="spaceOptions.length" v-model="space" :label="t.skills.filterSpace" :options="spaceOptions" icon="book" />
      <OptionPills v-if="!narrow" v-model="view" :options="views" :label="t.skills.view" hide-label />
    </div>
    <ErrorState v-if="error && !graph" :title="t.skills.graphTitle" :text="error" :retry-label="t.overviewView.reload" :busy="loading" @retry="load" />
    <LoadingSkeleton v-else-if="loading && !graph" :rows="6" :label="t.skills.graphLoading" />
    <EmptyState v-else-if="graph && !rows.length" icon="users" :title="t.skills.graphEmpty" />
    <template v-else-if="graph">
      <div v-show="showGraph" ref="host" class="skill-graph-canvas" role="img" :aria-label="t.skills.graphLabel(counts.people, counts.topics)" />
      <ul v-if="!showGraph" class="skill-graph-list atlas-stagger" :aria-label="t.skills.graphTitle">
        <li v-for="row in rows" :key="row.topic.id">
          <a class="tag-chip" :href="topicUrl(row.topic.name)"><AtlasIcon name="hash" :size="13" />{{ row.topic.name }}</a>
          <span class="skill-graph-people">
            <button v-for="entry in row.people.slice(0, 8)" :key="entry.node.id" type="button" class="skill-graph-person" :aria-pressed="selected?.id === entry.node.id" @click="open(entry.node)">
              {{ entry.node.name }}<span v-if="entry.level" class="skill-graph-dots" aria-hidden="true"><i v-for="n in 5" :key="n" :class="{ on: n <= Math.round(entry.level) }" /></span>
            </button>
            <span v-if="row.people.length > 8" class="skill-graph-more">{{ t.skills.morePeople(row.people.length - 8) }}</span>
          </span>
        </li>
      </ul>
      <p v-if="graph.truncated" class="skill-graph-note">{{ t.skills.graphTruncated }}</p>
      <div v-if="selected" :key="selected.id" class="skill-graph-person-card" data-skill-graph-person>
        <ContactCard :contact="{ name: selected.name, title: selected.title ?? undefined, organization: selected.organization ?? undefined }" />
        <ContactSkillLine :contact-id="selected.id" bars :name="selected.name" :api="api" />
      </div>
    </template>
  </section>
</template>

<style scoped>
.skill-graph { display: grid; gap: 12px; min-width: 0; }
.skill-graph-toolbar { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.skill-graph-toolbar :deep(.search-field) { flex: 1 1 220px; }
.skill-graph-canvas { height: min(560px, 70vh); border: 1px solid var(--line); border-radius: 14px; background: var(--surface); animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.skill-graph-list { display: grid; margin: 0; padding: 0; list-style: none; border: 1px solid var(--line); border-radius: 14px; background: var(--surface); overflow: hidden; }
.skill-graph-list > li { display: grid; gap: 6px; padding: 10px 14px; content-visibility: auto; contain-intrinsic-size: auto 64px; }
.skill-graph-more { align-self: center; color: var(--muted); font-size: 12.5px; }
.skill-graph-list > li + li { border-top: 1px solid color-mix(in srgb, var(--line) 50%, var(--surface)); }
.skill-graph-list .tag-chip { justify-self: start; text-decoration: none; }
.skill-graph-people { display: flex; flex-wrap: wrap; gap: 4px 10px; min-width: 0; }
.skill-graph-person { display: inline-flex; align-items: center; gap: 6px; max-width: 100%; padding: 2px 6px; border: 0; border-radius: 6px; background: transparent; color: var(--text);
  font: inherit; font-size: 13px; cursor: pointer; transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.skill-graph-person[aria-pressed="true"] { background: var(--accent-soft); }
@media (hover: hover) and (pointer: fine) { .skill-graph-person:hover { background: var(--hover); } }
.skill-graph-person:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.skill-graph-dots { display: inline-flex; gap: 2px; }
.skill-graph-dots i { width: 5px; height: 5px; border-radius: 999px; background: color-mix(in srgb, var(--accent) 22%, transparent); }
.skill-graph-dots i.on { background: var(--accent); }
.skill-graph-note { margin: 0; color: var(--muted); font-size: 12.5px; }
.skill-graph-person-card { display: grid; gap: 10px; max-width: 560px; padding: 14px 16px; border: 1px solid var(--line); border-radius: 14px; background: var(--surface);
  box-shadow: var(--refresh-card-shadow); animation: st-rise-in var(--atlas-motion-slow) var(--atlas-ease-out) both; }
</style>
