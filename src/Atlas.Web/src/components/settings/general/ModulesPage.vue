<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from "vue";
import { useDirtyGuard } from "../dirtyGuard";
import { provideModuleHeader } from "./moduleHeader";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import { errorMessage } from "../../../api";
import t from "../../../i18n/settings-modules";
import EmptyState from "../EmptyState.vue";
import ErrorState from "../ErrorState.vue";
import LoadingNote from "../LoadingNote.vue";
import SettingsPage from "../SettingsPage.vue";
import StatusBadge from "../StatusBadge.vue";
import TileCard from "../TileCard.vue";
import { useSettingsContext } from "../context";
import { useSettingsRoute } from "../useSettingsRoute";
import { loadModules, moduleEntry, type ModuleDescriptor, type ModuleState } from "./moduleRegistry";

/**
 * Moduly a integrace (`general/modules[/<id>]`): without `sub` the modules as cards with their
 * state; with `sub` (a module id: `oidc`, `youtrack`, `wiki-js-import`) that module's own page with
 * its compiled form, which saves on its own. Wiki administrator only.
 */
const props = defineProps<{ sub?: string; part?: string }>();
const context = useSettingsContext();
const router = useSettingsRoute();

const modules = ref<ModuleDescriptor[]>([]);
// Authentication and AI have their own settings navigation; legacy deep links below still open.
const integrationModules = computed(() => modules.value.filter(item => !["oidc", "hub", "ai"].includes(item.settingsUi)));
const loading = ref(true);
const error = ref("");
const states = ref<Record<string, ModuleState>>({});
let disposed = false;

const current = computed(() => (props.sub ? modules.value.find((item) => item.id === props.sub) : undefined));
const entry = computed(() => moduleEntry(current.value?.settingsUi));
// Refresh 2.5 module template (board 10): the header carries the module's mark, its state and the form's own switch.
const headerControl = ref<HTMLElement>();
provideModuleHeader({ target: headerControl });
const currentState = ref<ModuleState>();
const guard = useDirtyGuard();
function readCurrentState() {
  const read = entry.value?.state;
  const id = current.value?.id;
  if (!read || !id || id === "ai") return;
  read().then((state) => { if (!disposed && current.value?.id === id) currentState.value = state; }).catch(() => undefined);
}
watch(() => current.value?.id, () => { currentState.value = undefined; readCurrentState(); });
// A saved (or discarded) draft may change what the module does: read its state again.
watch(() => guard?.dirty.value, (dirty, before) => { if (before && !dirty) readCurrentState(); });
const descriptions: Record<string, string> = {
  ai: t.modules.aiDescription,
};
const back = computed(() => {
  if (!props.sub || props.sub === "ai") return undefined;
  if (props.sub === "oidc" || props.sub === "hub") return { href: router.href("general/authentication"), label: t.modules.backAuthentication };
  return { href: router.href("general/modules"), label: t.modules.backAll };
});

async function load() {
  if (!context.isWikiAdmin) {
    loading.value = false;
    return;
  }
  loading.value = true;
  error.value = "";
  try {
    const result = await loadModules();
    if (disposed) return;
    modules.value = result;
    if (!props.sub) loadStates();
  } catch (failure) {
    if (!disposed) error.value = errorMessage(failure);
  } finally {
    if (!disposed) loading.value = false;
  }
}
// Card states arrive one by one after the cards render; a state that cannot be read is left out.
function loadStates() {
  for (const item of integrationModules.value) {
    const read = moduleEntry(item.settingsUi)?.state;
    if (!read) continue;
    read()
      .then((state) => {
        if (!disposed) states.value = { ...states.value, [item.id]: state };
      })
      .catch(() => undefined);
  }
}
watch(
  () => props.sub,
  (sub) => {
    if (!sub && modules.value.length) loadStates();
  },
);
onMounted(load);
onBeforeUnmount(() => (disposed = true));
</script>

<template>
  <SettingsPage
    :title="current?.title ?? t.modules.title"
    :icon="current ? (entry?.icon ?? 'blocks') : undefined"
    :icon-tone="current && current.settingsUi !== 'ai' ? 'neutral' : undefined"
    :description="(current && descriptions[current.settingsUi]) ?? current?.description ?? t.modules.description"
    :back="back"
    :data-module-header="current?.id"
  >
    <template v-if="sub && current && entry" #actions>
      <Transition name="st-fade"><StatusBadge v-if="currentState" :tone="currentState.tone" :icon="currentState.icon" :label="currentState.label" :data-module-header-state="current.id" /></Transition>
      <div ref="headerControl" class="module-header-control" />
    </template>
    <EmptyState v-if="!context.isWikiAdmin" icon="lock" :title="t.modules.adminOnlyTitle" :text="t.modules.adminOnlyText" />
    <ErrorState v-else-if="error" :title="t.modules.loadFailed" :text="error" :busy="loading" @retry="load" />
    <LoadingNote v-else-if="loading" :label="t.modules.loading" />

    <template v-else-if="sub">
      <EmptyState v-if="!current" icon="compass" :title="t.modules.notFoundTitle" :text="t.modules.notFoundText">
        <Button as-child variant="outline"><a :href="router.href('general/modules')"><AtlasIcon name="back" :size="16" />{{ t.modules.backAll }}</a></Button>
      </EmptyState>
      <EmptyState v-else-if="!entry" icon="triangle-alert" :title="t.modules.noFormTitle" :text="t.modules.noFormText" />
      <template v-else>
        <p v-if="sub === 'oidc' || sub === 'hub'" class="st-note" data-auth-legacy><span>{{ t.modules.legacyBefore }}<a class="st-link" :href="router.href(`general/authentication/${sub === 'oidc' ? 'login' : 'hub'}`)">{{ t.modules.legacyLink(sub === "oidc") }}</a>.</span></p>
        <component :is="entry.component" :key="current.id" :data-module-page="current.id" v-bind="entry.parts ? { part } : {}" @imported="context.imported" />
      </template>
    </template>

    <template v-else>
      <section v-if="integrationModules.length" class="modules-grid" :aria-label="t.modules.gridLabel">
        <TileCard
          v-for="(item, index) in integrationModules"
          :key="item.id"
          :title="item.title"
          :description="item.description"
          :icon="moduleEntry(item.settingsUi)?.icon ?? 'blocks'"
          :href="router.href(`general/modules/${item.id}`)"
          :index="index"
          :data-module="item.id"
        >
          <template #badge>
            <StatusBadge v-if="!moduleEntry(item.settingsUi)" tone="warning" icon="triangle-alert" :label="t.modules.unavailable" size="sm" />
            <Transition v-else name="st-fade"><StatusBadge v-if="states[item.id]" :tone="states[item.id]!.tone" :icon="states[item.id]!.icon" :label="states[item.id]!.label" size="sm" :data-module-state="item.id" /></Transition>
          </template>
          <template #footer>{{ t.modules.openSettings }}</template>
        </TileCard>
      </section>
      <EmptyState v-else icon="blocks" :title="t.modules.noneTitle" :text="t.modules.noneText" />
    </template>
  </SettingsPage>
</template>

<style scoped>
.modules-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 240px), 1fr)); gap: 12px; }
.module-header-control { display: contents; }
</style>
