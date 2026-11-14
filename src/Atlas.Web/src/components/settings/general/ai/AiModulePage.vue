<script setup lang="ts">
import { computed } from "vue";
import AiHealthStatus from "./AiHealthStatus.vue";
import AiSetupSection from "./AiSetupSection.vue";
import SettingsTabs from "../../SettingsTabs.vue";
import StatusBadge from "../../StatusBadge.vue";
import type { SettingsTab } from "../../types";
import { useSettingsRoute } from "../../useSettingsRoute";
import { useModuleHeader } from "../moduleHeader";
import AiAccessSection from "./AiAccessSection.vue";
import AiEvaluationSection from "./AiEvaluationSection.vue";
import AiReportsSection from "./AiReportsSection.vue";
import AiFeaturesSection from "./AiFeaturesSection.vue";
import AiIndexSection from "./AiIndexSection.vue";
import AiModelsCards from "./AiModelsCards.vue";
import AiOverviewSection from "./AiOverviewSection.vue";
import AiProfileSection from "./AiProfileSection.vue";
import { useAiAdminHealth } from "./useAiAdminHealth";
import t from "../../../../i18n/settings-ai";

// AI has a dedicated settings entry. Each page retains its own explicit save and draft guard.
// Refresh 2.5 (boards 07, 08): every part is one tab away: Přehled (the guided setup with its progress), Modely (the
// three models, one opened, the others summarised beside it), Funkce a limity, Přístup a limity, Index znalostí, Stav a
// vypínač, Volání a náklady, Kvalita odpovědí. The page header says whether AI is on and leads to the switch.
const props = defineProps<{ part?: string }>();
const router = useSettingsRoute();
const header = useModuleHeader();
const health = useAiAdminHealth();

const parts = ["setup", "overview", "embeddings", "rerank", "quick", "chat", "features", "access", "index", "usage", "quality"] as const;
type Part = (typeof parts)[number];
const tab = computed<Part>(() => (parts as readonly string[]).includes(props.part ?? "") ? (props.part as Part) : "setup");
const modelParts: readonly Part[] = ["chat", "quick", "embeddings", "rerank"];
const mainTab = computed(() => (modelParts.includes(tab.value) ? "models" : tab.value));
const mainTabs = computed<SettingsTab[]>(() => [
  { id: "setup", label: t.module.tabs.setup, icon: "list-checks", href: router.href("general/modules/ai") },
  { id: "models", label: t.module.tabs.models, icon: "database", href: router.href(`general/modules/ai/${modelParts.includes(tab.value) ? tab.value : "chat"}`) },
  { id: "features", label: t.module.tabs.features, icon: "sliders-horizontal", href: router.href("general/modules/ai/features") },
  { id: "access", label: t.module.tabs.access, icon: "users", href: router.href("general/modules/ai/access") },
  { id: "index", label: t.module.tabs.index, icon: "box", href: router.href("general/modules/ai/index") },
  { id: "overview", label: t.module.tabs.overview, icon: "activity", href: router.href("general/modules/ai/overview") },
  { id: "usage", label: t.module.tabs.usage, icon: "line-chart", href: router.href("general/modules/ai/usage") },
  { id: "quality", label: t.module.tabs.quality, icon: "circle-check", href: router.href("general/modules/ai/quality") },
]);
const modelTabs = computed<SettingsTab[]>(() => [
  { id: "chat", label: t.module.modelTabs.chat, icon: "quote", href: router.href("general/modules/ai/chat") },
  { id: "quick", label: t.module.modelTabs.quick, icon: "pencil", href: router.href("general/modules/ai/quick") },
  { id: "embeddings", label: t.module.modelTabs.embeddings, icon: "search", href: router.href("general/modules/ai/embeddings") },
  { id: "rerank", label: t.module.modelTabs.rerank, icon: "list-ordered", href: router.href("general/modules/ai/rerank") },
]);
/** The header chip: whether AI is on for the wiki, and how many of its models are set; it leads to the switch. */
const power = computed(() => {
  const overview = health.overview.value;
  if (!overview) return undefined;
  const configured = overview.profiles.filter((profile) => profile.state !== "NotConfigured" && profile.kind !== "rerank").length;
  const attention = health.failures.value.length || health.warnings.value.length;
  return {
    tone: overview.enabled ? (attention ? "warning" : "success") : "neutral",
    icon: overview.enabled ? (attention ? "triangle-alert" : "circle-check") : "ban",
    label: t.module.power(overview.enabled, configured),
  } as const;
});
</script>

<template>
  <div class="ai-module" data-ai-module>
    <Teleport v-if="header?.target.value && power" :to="header.target.value">
      <a class="ai-power-link" :href="router.href('general/modules/ai/overview')" :aria-label="t.module.powerOpen(power.label)" data-ai-power-link>
        <StatusBadge :tone="power.tone" :icon="power.icon" :label="power.label" />
        <span class="ai-power-action">{{ t.module.switch }}</span>
      </a>
    </Teleport>
    <SettingsTabs :options="mainTabs" :model-value="mainTab" :label="t.module.settingsLabel" />
    <AiHealthStatus />
    <SettingsTabs v-if="mainTab === 'models'" variant="segmented" :options="modelTabs" :model-value="tab" :label="t.module.modelsLabel" />
    <Transition name="st-step" mode="out-in">
    <div :key="tab" class="ai-module-content">
    <AiSetupSection v-if="tab === 'setup'" />
    <AiOverviewSection v-else-if="tab === 'overview'" key="overview" />
    <div v-else-if="mainTab === 'models'" class="ai-models-layout">
      <AiProfileSection :key="tab" :kind="tab as 'chat' | 'quick' | 'embeddings' | 'rerank'" />
      <AiModelsCards :current="tab as 'chat' | 'quick' | 'embeddings' | 'rerank'" :profiles="health.overview.value?.profiles" />
    </div>
    <AiFeaturesSection v-else-if="tab === 'features'" key="features" />
    <AiAccessSection v-else-if="tab === 'access'" key="access" />
    <AiIndexSection v-else-if="tab === 'index'" key="index" />
    <template v-else-if="tab === 'usage' || tab === 'quality'"><AiReportsSection :key="tab" :kind="tab" /><AiEvaluationSection v-if="tab === 'quality'" /></template>
    </div>
    </Transition>
  </div>
</template>

<style scoped>
.ai-module, .ai-module-content { display: grid; gap: 18px; min-width: 0; }
/* The hover fill reaches as far past „Vypínač“ as it does before the badge's own pill, and the negative end margin keeps
   the word on the page's right edge, where the cards end, instead of 6 px short of it with a lopsided fill on hover. */
.ai-power-link { display: inline-flex; align-items: center; gap: 10px; min-height: var(--st-control-h, 36px); margin-inline-end: -10px; padding: 0 10px 0 6px; border-radius: var(--st-radius-sm); color: var(--text); text-decoration: none; transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) {
  .ai-power-link:hover { background: var(--hover); }
}
.ai-power-link:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.ai-power-action { color: var(--accent); font-size: 13px; font-weight: 600; }
/* Modely (board 08): the opened model beside a summary of all three and what saving changes. */
.ai-models-layout { display: grid; grid-template-columns: minmax(0, 1fr) minmax(240px, 300px); gap: 20px; align-items: start; min-width: 0; }
@container settings-shell (max-width: 1000px) { .ai-models-layout { grid-template-columns: minmax(0, 1fr); } }
</style>
