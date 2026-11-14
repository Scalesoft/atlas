<script setup lang="ts">
import { computed, onBeforeUnmount, ref, shallowRef, watch } from "vue";
import { afterPaint } from "../../idle";
import { gridKeys, rovingInit } from "../../keyboard/navigation";
import AtlasIcon from "../../components/AtlasIcon.vue";
import LoadingRegion from "../../components/LoadingRegion.vue";
import EmptyState from "../../components/settings/EmptyState.vue";
import ErrorState from "../../components/settings/ErrorState.vue";
import OptionPills from "../../components/settings/OptionPills.vue";
import t from "../../i18n/projector";
import type { ChoiceOption } from "../../components/settings/types";
import { Button } from "../../components/ui/button";
import { projectorApi } from "../projectorApi";
import { ensureRequirementIcons } from "../requirementIcons";
import { stateIcons } from "../requirementLook";
import { requirementStateLabel } from "../requirementSummary";
import { requirementStates } from "../requirementTypes";
import { overviewApi, type OverviewFilter } from "./overviewApi";
import { overviewMatrix, productMatrix, type MatrixRow } from "./overviewModel";
import { useGuardedLoad } from "./useGuardedLoad";
import { pragueNow } from "@atlas/markdown/core";
import "../../components/settings/settings.css";
import "../requirements.css";

/**
 * Přehled nasazení (spec §4 GetOverview): the space's deployment pages × requirement keys. One answer of the server (every keyed
 * block the account may read) serves all three filters: columns are the keys of the chosen one (Co chybí by default), grouped by the
 * catalog's bundle or the key's prefix, rows the pages with something left in it. A cell is the block's state, its date or the days
 * it waits, and a link to the block on its page; switching the filter fades the cells outside it. A space the account cannot read
 * is „Nemáte přístup“. Composed of OptionPills, EmptyState, ErrorState and LoadingRegion; the matrix itself is this view's own, its
 * cells one link each (icons drawn by CSS) and its rows drawn in chunks after each paint. „Produkty“ turns the same answer into deployments ×
 * products (catalog modules by their stable key, P1): what each deployment has, has in part and lacks.
 */
const props = withDefaults(defineProps<{
  spaceId: string;
  /** Where the answers come from: the server, or sample data in the gallery. Only the catalog's keys, titles and bundles are read. */
  api?: { overview: typeof overviewApi.overview; catalog: (spaceId: string) => Promise<{ definitions: readonly { key: string; title: string; bundle: string; moduleKey?: string }[] }> };
}>(), { api: () => ({ overview: overviewApi.overview, catalog: projectorApi.catalog }) });

const filters: (ChoiceOption & { value: OverviewFilter })[] = [
  { value: "missing", label: t.overview.filterMissing, icon: "list-checks" },
  { value: "inbound-missing", label: t.overview.filterInbound, icon: "arrow-down" },
  { value: "all", label: t.overview.filterAll, icon: "table" },
];
const views: ChoiceOption[] = [...filters, { value: "products", label: t.overview.products, icon: "layers" }];
const filter = ref<OverviewFilter | "products">("missing");
const bundles = shallowRef(new Map<string, string>()), titles = shallowRef(new Map<string, string>());
const definitions = shallowRef<readonly { key: string; bundle: string; moduleKey?: string }[]>([]);
const { value: overview, loading, denied, error, load } = useGuardedLoad(signal => props.api.overview(props.spaceId, "all", signal));

// The catalog only names the bundles and gives canonical titles; without it the columns group by the key's prefix.
async function loadCatalog(spaceId: string) {
  try {
    const { definitions: found } = await props.api.catalog(spaceId);
    if (spaceId !== props.spaceId) return;
    bundles.value = new Map(found.filter(item => item.bundle).map(item => [item.key, item.bundle]));
    titles.value = new Map(found.map(item => [item.key, item.title]));
    definitions.value = found;
  } catch { /* the overview stands without bundles */ }
}

// A few rows at once, the rest in chunks after each paint: the first rows (and the filter's new ones) show at once, the matrix fills in.
const FIRST_ROWS = 8, ROW_CHUNK = 8;
const rowLimit = ref(FIRST_ROWS);
function start() {
  overview.value = undefined;
  bundles.value = new Map();
  titles.value = new Map();
  definitions.value = [];
  void loadCatalog(props.spaceId);
  void load();
}
watch(() => props.spaceId, start, { immediate: true });
ensureRequirementIcons(document);

const matrix = computed(() => overview.value ? overviewMatrix(overview.value, filter.value === "products" ? "all" : filter.value, { today: pragueNow().date, bundles: bundles.value, titles: titles.value }) : null);
const products = computed(() => filter.value === "products" && overview.value ? productMatrix(overview.value, definitions.value) : null);
const productTone = (cell: { have: number; total: number; done: number }) => !cell.have ? "none" : cell.have < cell.total ? "partial" : cell.done === cell.have ? "done" : "have";
/** A page's next steps in the blocks' words: „lze nasadit 2“, „lze předat 1“, „blokuje 1“; nothing to say says nothing. */
const next = ({ readiness }: MatrixRow) => [
  readiness.deployable && { tone: "ready", label: t.overview.canDeploy(readiness.deployable) },
  readiness.handover && { tone: "handover", label: t.overview.canHandOver(readiness.handover) },
  readiness.blocking && { tone: "blocked", label: t.overview.blocking(readiness.blocking) },
].filter((chip): chip is { tone: string; label: string } => !!chip);
// shortcut: rows are drawn in chunks and columns all at once; a space with hundreds of keys would want the columns windowed too.
const rows = computed(() => matrix.value?.rows.slice(0, rowLimit.value) ?? []);
let drawing = 0;
watch(matrix, () => {
  const mine = ++drawing;
  rowLimit.value = FIRST_ROWS;
  const more = () => afterPaint(() => {
    if (mine !== drawing || rowLimit.value >= (matrix.value?.rows.length ?? 0)) return;
    rowLimit.value += ROW_CHUNK;
    more();
  });
  more();
}, { flush: "post" });
onBeforeUnmount(() => { drawing++; });
// The matrix is one tab stop; the arrows walk its links (keyboard pass), so Tab passes a hundred cells in one step.
const table = ref<HTMLTableElement>();
watch([table, rows], () => rovingInit(table.value, "a[href]"), { flush: "post" });
const productTable = ref<HTMLTableElement>();
watch([productTable, products], () => rovingInit(productTable.value, "a[href]"), { flush: "post" });
const nothingLeft = computed(() => filter.value === "inbound-missing"
  ? { title: t.overview.allDelivered, text: t.overview.allDeliveredText }
  : { title: t.overview.nothingMissing, text: t.overview.nothingMissingText });
// The pages a filter leaves out are said, so a finished deployment is not mistaken for a missing one.
const hiddenPages = computed(() => {
  const hidden = matrix.value?.hidden ?? 0;
  if (!hidden) return "";
  return filter.value === "inbound-missing" ? t.overview.hiddenNoInputs(hidden) : t.overview.hiddenDone(hidden);
});
</script>

<template>
  <section class="deployment-overview settings-ui" data-deployment-overview aria-labelledby="deployment-overview-heading">
    <h2 id="deployment-overview-heading" class="st-sr-only">{{ t.overview.title }}</h2>
    <EmptyState v-if="denied" icon="lock" :title="t.overview.noAccess" :text="t.overview.noAccessText" />
    <ErrorState v-else-if="error" :title="t.overview.loadFailed" :text="error" :busy="loading" @retry="start" />
    <LoadingRegion v-else :loading="loading" :ready="!!matrix" :rows="5" :label="t.overview.loading">
      <EmptyState v-if="matrix && !overview?.pages.length" icon="list-checks" :title="t.overview.noRequirements"
        :text="t.overview.noRequirementsText" />
      <template v-else-if="matrix">
        <div class="overview-toolbar">
          <OptionPills v-model="filter" :label="t.overview.show" hide-label :options="views" data-overview-view />
          <ul v-if="!products" class="overview-legend" :aria-label="t.overview.states">
            <li v-for="state in requirementStates" :key="state" :data-tone="state"><AtlasIcon :name="stateIcons[state]" :size="13" />{{ requirementStateLabel(state) }}</li>
          </ul>
        </div>
        <template v-if="products">
          <EmptyState v-if="!products.columns.length" compact icon="layers" :title="t.overview.noProducts"
            :text="t.overview.noProductsText" />
          <div v-else class="overview-scroll" data-overview-products>
            <table ref="productTable" class="overview-matrix" @keydown="gridKeys">
              <caption class="st-sr-only">{{ t.overview.productsCaption }}</caption>
              <thead>
                <tr>
                  <th scope="col" class="overview-rowhead">{{ t.overview.deployment }}</th>
                  <th v-for="column in products.columns" :key="column.key" scope="col" data-column :title="`${column.title} · ${t.counts.requirements(column.keys.length)}`">
                    <span class="overview-column-title">{{ column.title }}</span>
                  </th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="row in products.rows" :key="row.pageId">
                  <th scope="row" class="overview-rowhead"><a :href="row.href" :title="row.title" class="overview-page">{{ row.title }}</a></th>
                  <td v-for="(cell, index) in row.cells" :key="products.columns[index]!.key"><a :href="cell.href" class="overview-product" :data-product="productTone(cell)"
                    :aria-label="cell.label" :title="cell.label">{{ !cell.have ? "–" : cell.have < cell.total ? `${cell.have}/${cell.total}` : cell.done === cell.have ? "✓" : t.overview.has }}</a></td>
                </tr>
              </tbody>
            </table>
          </div>
        </template>
        <EmptyState v-else-if="!matrix.columns.length" compact icon="circle-check" :title="nothingLeft.title" :text="nothingLeft.text" />
        <template v-else>
          <div class="overview-scroll">
            <table ref="table" class="overview-matrix" data-fixed :data-filter="filter" :style="{ '--overview-columns': matrix.columns.length }" @keydown="gridKeys">
              <caption class="st-sr-only">{{ t.overview.matrixCaption }}</caption>
              <colgroup><col class="overview-rowhead-col"><col v-for="column in matrix.columns" :key="column.key" class="overview-column-col"></colgroup>
              <thead>
                <tr v-if="matrix.groups.length" class="overview-groups">
                  <td class="overview-rowhead" />
                  <th v-for="group in matrix.groups" :key="group.label" scope="colgroup" :colspan="group.span" data-group>{{ group.label }}</th>
                </tr>
                <tr>
                  <th scope="col" class="overview-rowhead">{{ t.overview.deployment }}</th>
                  <th v-for="column in matrix.columns" :key="column.key" scope="col" data-column :title="`${column.title} (${column.key})`">
                    <span class="overview-column-title">{{ column.title }}</span>
                    <span v-if="column.detail" class="overview-column-detail">{{ column.detail }}</span>
                  </th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="row in rows" :key="row.pageId">
                  <th scope="row" class="overview-rowhead">
                    <a :href="row.href" :title="row.title" class="overview-page">{{ row.title }}</a>
                    <span class="overview-percent" data-percent>{{ t.overview.percentDone(row.percent) }}</span>
                    <span v-if="next(row).length" class="overview-next" data-overview-next>
                      <span v-for="chip in next(row)" :key="chip.tone" class="overview-chip" :data-readiness="chip.tone">{{ chip.label }}</span>
                    </span>
                  </th>
                  <td v-for="(cell, index) in row.cells" :key="matrix.columns[index]!.key" :data-matches="cell ? String(cell.matches) : undefined"
                    :class="{ 'overview-none': !cell }" :title="cell ? undefined : t.overview.notOnPage"><a v-if="cell" :href="cell.href" class="overview-cell rq-i rq-dir"
                    :data-icon="stateIcons[cell.state]" :data-tone="cell.state" :data-direction="cell.direction" :aria-label="cell.label" :title="cell.label">{{ cell.text }}</a><template v-else>–</template></td>
                </tr>
              </tbody>
            </table>
          </div>
        </template>
        <p v-if="hiddenPages" class="overview-hidden" role="status" data-overview-hidden>
          {{ hiddenPages }} · <Button type="button" variant="link" size="sm" @click="filter = 'all'">{{ t.overview.showAll }}</Button>
        </p>
      </template>
    </LoadingRegion>
  </section>
</template>

<style scoped>
.deployment-overview { container: deployment-overview / inline-size; display: grid; gap: 14px; width: 100%; min-width: 0; }
.overview-toolbar { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 10px 16px; margin-bottom: 12px; }
.overview-legend { display: flex; flex-wrap: wrap; gap: 4px 12px; margin: 0; padding: 0; list-style: none; color: var(--muted); font-size: 12px; }
.overview-legend li { display: inline-flex; align-items: center; gap: 4px; }
/* The matrix scrolls sideways inside its own frame only; the page never gets a horizontal scrollbar. */
.overview-scroll { max-width: 100%; overflow-x: auto; border: 1px solid var(--line); border-radius: var(--st-radius); background: var(--surface); overscroll-behavior-x: contain; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.overview-matrix { border-collapse: separate; border-spacing: 0; min-width: 100%; font-size: 12.5px; }
/* Fixed widths (the page names' column and 84px per key) let the browser lay the rows out one by one: a matrix drawn in chunks costs per chunk, not per row so far. */
.overview-matrix[data-fixed] { table-layout: fixed; width: calc(var(--overview-head) + var(--overview-columns) * 84px); }
.overview-matrix { --overview-head: 220px; }
.overview-rowhead-col { width: var(--overview-head); }
.overview-column-col { width: 84px; }
.overview-matrix th, .overview-matrix td { padding: 6px 4px; border-bottom: 1px solid var(--st-line-soft); text-align: center; vertical-align: middle; }
.overview-matrix tbody tr:last-child > * { border-bottom: 0; }
.overview-matrix thead th { background: var(--st-subtle); font-weight: 600; vertical-align: bottom; }
.overview-groups th { padding-top: 8px; border-bottom: 0; color: var(--muted); font-size: 11px; letter-spacing: .04em; text-align: left; text-transform: uppercase; }
/* Titles read across (U4-9): up to three lines in a column a little wider than its cells, the rest in the header's title; a key
   below a title another column of the group shares. The matrix scrolls sideways in its frame, the page names stay. */
.overview-matrix thead th[data-column] { width: 84px; min-width: 84px; max-width: 84px; padding: 8px 4px 6px; vertical-align: bottom; }
.overview-column-title { display: -webkit-box; overflow: hidden; -webkit-box-orient: vertical; -webkit-line-clamp: 3; line-clamp: 3; font-size: 11.5px; font-weight: 550;
  line-height: 1.3; overflow-wrap: anywhere; hyphens: auto; }
.overview-column-detail { display: block; margin-top: 2px; overflow: hidden; color: var(--muted); font-size: 10.5px; font-weight: 400; text-overflow: ellipsis; white-space: nowrap; }
.overview-matrix .overview-rowhead { position: sticky; left: 0; z-index: 1; width: 220px; min-width: 220px; max-width: 220px; padding-inline: 12px 10px; background: var(--surface); text-align: left; box-shadow: 1px 0 0 var(--line); }
.overview-matrix thead .overview-rowhead { background: var(--st-subtle); }
/* Two lines before an ellipsis: deployments share a long beginning („Deployment in …“), so one line made them look alike (PR2-4). */
.overview-page { display: -webkit-box; overflow: hidden; -webkit-box-orient: vertical; -webkit-line-clamp: 2; line-clamp: 2; color: var(--text); font-weight: 600; line-height: 1.3; overflow-wrap: anywhere; text-decoration: none; }
.overview-percent { display: block; color: var(--muted); font-size: 11.5px; font-variant-numeric: tabular-nums; }
/* A page's next steps under its name, in the blocks' tones. */
.overview-next { display: flex; flex-wrap: wrap; gap: 2px 6px; margin-top: 2px; }
.overview-chip { font-size: 11px; font-weight: 600; white-space: nowrap; color: var(--muted); }
.overview-chip[data-readiness="ready"] { color: var(--rq-tone-done); }
.overview-chip[data-readiness="blocked"] { color: var(--rq-tone-blocked); }
/* A cell is one link: the state's icon (::before, requirementIcons.ts), its date or days below, the direction's arrow beside the icon. */
.overview-cell {
  position: relative; display: grid; grid-template-rows: 15px 1.3em; justify-items: center; align-items: center; gap: 1px; min-width: 40px; padding: 3px 2px;
  border-radius: var(--st-radius-sm); color: var(--muted); font-size: 10.5px; font-variant-numeric: tabular-nums; text-decoration: none; white-space: nowrap; --rq-size: 15px;
  transition: opacity var(--atlas-motion-base) var(--atlas-ease-out), color var(--atlas-motion-base) var(--atlas-ease-out), background-color var(--atlas-motion-fast) var(--atlas-ease-out);
}
.overview-cell::before { color: var(--tone, var(--muted)); }
.overview-cell:is([data-direction="inbound"], [data-direction="outbound"])::after {
  content: ""; position: absolute; top: 11px; left: calc(50% + 4px); width: 11px; height: 11px; background: var(--muted);
  -webkit-mask: var(--rq-dir) center / contain no-repeat; mask: var(--rq-dir) center / contain no-repeat;
}
[data-matches="false"] .overview-cell { opacity: .28; }
@media (hover: hover) and (pointer: fine) {
  .overview-cell:hover { background: var(--hover); opacity: 1; }
  .overview-page:hover { color: var(--accent); }
}
.overview-cell:focus-visible, .overview-page:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; opacity: 1; }
.overview-matrix .overview-none { color: var(--muted); }
/* A product cell: ✓ all of it finished, „má“ all of it there, 3/5 a part of it, – none of it; one link to its first block. */
.overview-product { display: inline-grid; place-items: center; min-width: 40px; padding: 4px 6px; border-radius: var(--st-radius-sm); color: var(--muted); font-weight: 600;
  font-variant-numeric: tabular-nums; text-decoration: none;
  transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.overview-product[data-product="done"] { color: var(--rq-tone-done); }
.overview-product[data-product="have"] { color: var(--rq-tone-in_progress); }
.overview-product[data-product="partial"] { color: var(--rq-tone-waiting_for_input); background: color-mix(in srgb, var(--rq-tone-waiting_for_input) 10%, transparent); }
.overview-product:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
@media (hover: hover) and (pointer: fine) { .overview-product:hover { background: var(--hover); } }
.overview-hidden { margin: 0; color: var(--muted); font-size: 12.5px; text-align: center; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.overview-hidden :deep(button) { height: auto; padding: 0; font-size: inherit; }
/* Tones: the one map of requirements.css (--tone by data-tone); an icon of what stands reads muted, not as the bar's faint fill. */
:is([data-tone="planned"], [data-tone="not_applicable"]) { --tone: var(--muted); }
.overview-legend [data-tone] > svg { color: var(--tone, var(--muted)); }
@container deployment-overview (max-width: 560px) {
  .overview-matrix { --overview-head: 140px; }
  .overview-matrix .overview-rowhead { width: 140px; min-width: 140px; max-width: 140px; padding-inline: 10px 6px; }
}
</style>
