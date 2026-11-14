<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from "vue";
import { errorMessage } from "../../../api";
import { projectorApi, type ProjectorCatalog, type ProjectorPreset } from "../../../projector/projectorApi";
import { catalogModules } from "../../../projector/catalogSelection";
import { organizationApi } from "../../../organization";
import AtlasIcon from "../../AtlasIcon.vue";
import LoadingRegion from "../../LoadingRegion.vue";
import { Button } from "../../ui/button";
import EmptyState from "../EmptyState.vue";
import ErrorState from "../ErrorState.vue";
import SettingsList from "../SettingsList.vue";
import SettingsListRow from "../SettingsListRow.vue";
import SettingsPage from "../SettingsPage.vue";
import SettingsSection from "../SettingsSection.vue";
import StatusBadge from "../StatusBadge.vue";
import t from "../../../i18n/settings-space";
import { useSettingsContext } from "../context";

/**
 * Sady a katalog (plan 3.5 C, owner O1): where the space's catalog and its sets are and what they mean, read only. The catalog says
 * what we can deliver, a set (a template with requirement blocks, e.g. Standardizace) a typical job, its first paragraph what it is;
 * a deployment is one customer's. Each is an ordinary page, edited in its text: a row only opens it. A set whose blocks read otherwise
 * than the catalog says so (O3), as the catalog's own warnings (a key defined twice) are said.
 */
const props = defineProps<{ spaceId: string; api?: Pick<typeof projectorApi, "catalog" | "presets">; tree?: typeof organizationApi.tree }>();
const context = useSettingsContext();
const catalog = ref<ProjectorCatalog>({ definitions: [], warnings: [] });
const presets = ref<ProjectorPreset[]>([]);
// A catalog page's row is its page's title (the space's tree); „Katalog požadavků“ while it is not known.
const titles = ref(new Map<string, string>());
const loading = ref(true), loaded = ref(false);
const error = ref("");
let request: AbortController | undefined;

async function load() {
  request?.abort();
  const controller = (request = new AbortController());
  loading.value = true;
  error.value = "";
  try {
    const source = props.api ?? projectorApi;
    const [read, sets, tree] = await Promise.all([source.catalog(props.spaceId, controller.signal), source.presets(props.spaceId, controller.signal, true),
      (props.tree ?? organizationApi.tree)(props.spaceId, controller.signal).catch(() => undefined)]);
    if (controller.signal.aborted) return;
    catalog.value = read;
    presets.value = sets;
    titles.value = new Map(tree?.items.filter(item => item.title).map(item => [item.id, item.title]));
    loaded.value = true;
  } catch (failure) {
    if (!controller.signal.aborted) error.value = errorMessage(failure);
  } finally {
    if (request === controller) loading.value = false;
  }
}
void load();
onBeforeUnmount(() => request?.abort());

/** Each catalog page: how much it holds, and its products with how many modules each. */
const pages = computed(() => {
  const modules = catalogModules(catalog.value.definitions).filter(module => module.title);
  return [...new Set(catalog.value.definitions.map(definition => definition.pageId))].map(pageId => {
    const own = modules.filter(module => module.pageId === pageId), products = new Map<string, number>();
    for (const module of own) { const name = module.product || module.category; products.set(name, (products.get(name) ?? 0) + 1); }
    const size = catalog.value.definitions.filter(definition => definition.pageId === pageId).length;
    return { pageId, products: [...products],
      description: [t.sets.requirements(size), t.sets.products(products.size), t.sets.modules(own.length)].join(" · ") };
  });
});
const setMeta = (preset: ProjectorPreset) => [t.sets.modules(preset.modules ?? 0), ...(preset.products ?? [])].join(" · ");
const differs = (preset: ProjectorPreset) => preset.differs?.length
  ? t.sets.differs(preset.differs.length) : "";
</script>

<template>
  <div class="space-sets-page" :aria-busy="loading">
    <SettingsPage :title="t.sets.title" icon="book-open"
      :description="t.sets.description">
      <ErrorState v-if="error" :title="t.sets.loadFailed" :text="error" :busy="loading" @retry="load" />
      <template v-else>
        <SettingsSection :title="t.sets.catalog" icon="book-open" :description="t.sets.catalogDescription" :count="loaded ? pages.length : undefined">
          <LoadingRegion :loading="loading" :ready="loaded" :rows="1" row-height="72px" :label="t.sets.loadingCatalog" class="sets-rows">
            <EmptyState v-if="!pages.length" compact icon="book-open" :title="t.sets.noCatalog"
              :text="t.sets.noCatalogText" />
            <SettingsList v-else :label="t.sets.catalogPages">
              <SettingsListRow v-for="page in pages" :key="page.pageId" :title="titles.get(page.pageId) ?? (pages.length > 1 ? t.sets.catalogNumbered(pages.indexOf(page) + 1) : t.sets.catalog)" :description="page.description" icon="book-open" data-sets-catalog>
                <template #meta>
                  <StatusBadge v-for="[name, modules] in page.products" :key="name" size="sm" :label="`${name} (${t.sets.modules(modules)})`" />
                  <StatusBadge v-for="warning in catalog.warnings" :key="warning" size="sm" tone="warning" icon="triangle-alert" :label="warning" data-sets-catalog-warning />
                </template>
                <template #actions><Button type="button" variant="outline" size="sm" @click="context.openPage(page.pageId)">{{ t.sets.open }}<AtlasIcon name="arrow-right" :size="15" /></Button></template>
              </SettingsListRow>
            </SettingsList>
          </LoadingRegion>
        </SettingsSection>
        <SettingsSection :title="t.sets.sets" icon="layout-template" :description="t.sets.setsDescription" :count="loaded ? presets.length : undefined">
          <LoadingRegion :loading="loading" :ready="loaded" :rows="2" row-height="72px" :label="t.sets.loadingSets" class="sets-rows">
            <EmptyState v-if="!presets.length" compact icon="layout-template" :title="t.sets.noSets"
              :text="t.sets.noSetsText" />
            <SettingsList v-else :label="t.sets.spaceSets">
              <SettingsListRow v-for="preset in presets" :key="preset.pageId" :title="preset.title" :description="preset.summary || undefined" icon="layout-template" :data-sets-preset="preset.pageId">
                <template #meta>
                  <StatusBadge size="sm" :label="`${t.sets.requirements(preset.requirements)} · ${setMeta(preset)}`" />
                  <StatusBadge v-if="differs(preset)" size="sm" tone="warning" icon="triangle-alert" :label="differs(preset)" :title="preset.differs!.join(', ')" data-sets-differs />
                </template>
                <template #actions><Button type="button" variant="outline" size="sm" @click="context.openPage(preset.pageId)">{{ t.sets.open }}<AtlasIcon name="arrow-right" :size="15" /></Button></template>
              </SettingsListRow>
            </SettingsList>
          </LoadingRegion>
        </SettingsSection>
      </template>
    </SettingsPage>
  </div>
</template>

<style scoped>
.space-sets-page { width: 100%; min-width: 0; }
/* As Šablony stránek: the list reaches the card edges, the empty state is frameless inside the card. */
.sets-rows :deep(.loading-region-content > .settings-list) { margin: calc(var(--st-body-pad-y, 16px) * -1) calc(var(--st-pad-x, 18px) * -1); --st-row-pad-x: var(--st-pad-x, 18px); }
.sets-rows :deep(.loading-region-content > .st-empty) { border-style: none; padding-block: 20px; }
</style>
