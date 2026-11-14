<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from "vue";
import { errorMessage } from "../../../api";
import t from "../../../i18n/settings-backups";
import EmptyState from "../EmptyState.vue";
import ErrorState from "../ErrorState.vue";
import LoadingNote from "../LoadingNote.vue";
import SettingsPage from "../SettingsPage.vue";
import SettingsTabs from "../SettingsTabs.vue";
import { useSettingsContext } from "../context";
import { useSettingsRoute } from "../useSettingsRoute";
import type { SettingsTab } from "../types";
import { loadModules, moduleEntry, type ModuleDescriptor } from "./moduleRegistry";

/**
 * Authentication settings use one active form at a time. OIDC login and Hub membership keep
 * their own saves and dirty guards; switching tabs goes through the settings route guard.
 * Server module descriptors decide which sections this installation actually offers.
 */
const props = defineProps<{ part?: string }>();
const context = useSettingsContext();
const router = useSettingsRoute();
const modules = ref<ModuleDescriptor[]>([]);
const loading = ref(true), error = ref("");
let disposed = false;

const choices = () => [
  { id: "login", settingsUi: "oidc", label: t.authentication.login, icon: "identity" },
  { id: "hub", settingsUi: "hub", label: t.authentication.hub, icon: "users" },
] as const;
const available = computed(() => choices().flatMap((choice) => {
  const descriptor = modules.value.find((item) => item.settingsUi === choice.settingsUi);
  const entry = descriptor && moduleEntry(descriptor.settingsUi);
  return descriptor && entry ? [{ ...choice, descriptor, entry }] : [];
}));
const activePart = computed(() => props.part || available.value[0]?.id);
const current = computed(() => available.value.find((item) => item.id === activePart.value));
const tabs = computed<SettingsTab[]>(() => available.value.map((item) => ({
  id: item.id, label: item.label, icon: item.icon, href: router.href(`general/authentication/${item.id}`),
})));

async function load() {
  if (!context.isWikiAdmin) { loading.value = false; return; }
  loading.value = true;
  error.value = "";
  try {
    const result = await loadModules();
    if (!disposed) modules.value = result;
  } catch (failure) {
    if (!disposed) error.value = errorMessage(failure);
  } finally {
    if (!disposed) loading.value = false;
  }
}
onMounted(() => { void load(); });
onBeforeUnmount(() => { disposed = true; });
</script>

<template>
  <SettingsPage :title="t.authentication.title" :description="t.authentication.description">
    <EmptyState v-if="!context.isWikiAdmin" icon="lock" :title="t.authentication.adminOnlyTitle" :text="t.authentication.adminOnlyText" />
    <ErrorState v-else-if="error" :title="t.authentication.loadError" :text="error" :busy="loading" @retry="load" />
    <LoadingNote v-else-if="loading" :label="t.authentication.loading" />
    <EmptyState v-else-if="!available.length" icon="identity" :title="t.authentication.unavailableTitle" :text="t.authentication.unavailableText" />
    <template v-else>
      <SettingsTabs :options="tabs" :model-value="activePart || ''" :label="t.authentication.tabsLabel" />
      <EmptyState v-if="!current" icon="compass" :title="t.authentication.notFoundTitle" :text="t.authentication.notFoundText" />
      <Transition v-else name="st-step" mode="out-in">
        <div :key="current.id" :data-auth-panel="current.id">
          <component :is="current.entry.component" />
        </div>
      </Transition>
    </template>
  </SettingsPage>
</template>