<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from "vue";
import { scrollBehavior } from "../../../../motion/aiMotion";
import { api, errorMessage } from "../../../../api";
import { focusWhenPresent } from "../../../../keyboard/navigation";
import type { AiAccessView } from "../../../../administration";
import { Button } from "../../../ui/button";
import AtlasIcon from "../../../AtlasIcon.vue";
import FlowProgress from "../../FlowProgress.vue";
import InlineReveal from "../../InlineReveal.vue";
import { disclosureHooks } from "../../disclosure";
import SettingsSection from "../../SettingsSection.vue";
import SettingsList from "../../SettingsList.vue";
import SettingsListRow from "../../SettingsListRow.vue";
import StatusBadge from "../../StatusBadge.vue";
import LoadingNote from "../../LoadingNote.vue";
import ErrorState from "../../ErrorState.vue";
import { useDirtyGuard } from "../../dirtyGuard";
import { useSettingsRoute } from "../../useSettingsRoute";
import AiReportsSection from "./AiReportsSection.vue";
import AiOverviewSection from "./AiOverviewSection.vue";
import AiProfileSection from "./AiProfileSection.vue";
import AiIndexSection from "./AiIndexSection.vue";
import AiAccessSection from "./AiAccessSection.vue";
import { useAiAdminHealth } from "./useAiAdminHealth";
import { locale } from "../../../../i18n";
import t from "../../../../i18n/settings-ai";
import { featureTexts, kindIcons, kindLabels, profileStateView, type AiFeaturesView, type AiProfileView, type AiSwitchView, type IndexStatus, type ProfileKind } from "./aiAdmin";

// This is a composition of the existing saved forms, not a second set of drafts or API contracts.
// Only one form owns the settings dirty guard at a time; a save reveals its dependent section.
// Refresh 2.5 (board 07): the progress as a strip of steps, the next step highlighted in the list with its form opening
// in place, then what is set: the models, the features and who may use AI, each with a way to change it.
const router = useSettingsRoute();
const guard = useDirtyGuard();
const health = useAiAdminHealth();
const sw = ref<AiSwitchView>();
const embeddings = ref<AiProfileView>();
const chat = ref<AiProfileView>();
const index = ref<IndexStatus>();
const access = ref<AiAccessView>();
const features = ref<AiFeaturesView>();
const loading = ref(true), error = ref("");
type Stage = "power" | "embeddings" | "index" | "chat" | "access";
const active = ref<Stage>();
const root = ref<HTMLElement>();
let disposed = false;
const blocked = computed(() => loading.value || !!guard?.dirty.value);
const profileReady = (profile?: AiProfileView) => !!profile?.configured && profile.enabled;
const steps = computed(() => [
 { id: "power" as const, title: t.setup.steps.power.title, short: t.setup.steps.power.short, icon: "sliders-horizontal", complete: !!sw.value?.enabled, summary: sw.value?.enabled ? t.setup.steps.power.on : t.setup.steps.power.off },
 { id: "embeddings" as const, title: t.setup.steps.embeddings.title, short: t.setup.steps.embeddings.short, icon: "search", complete: profileReady(embeddings.value), summary: profileReady(embeddings.value) ? embeddings.value!.model! : t.setup.steps.embeddings.todo },
 { id: "index" as const, title: t.setup.steps.index.title, short: t.setup.steps.index.short, icon: "book-open", complete: !!index.value?.active, summary: index.value?.active ? t.setup.steps.index.done(index.value.active.indexedPages) : t.setup.steps.index.todo },
 { id: "chat" as const, title: t.setup.steps.chat.title, short: t.setup.steps.chat.short, icon: "quote", complete: profileReady(chat.value), summary: profileReady(chat.value) ? chat.value!.model! : t.setup.steps.chat.todo },
 { id: "access" as const, title: t.setup.steps.access.title, short: t.setup.steps.access.short, icon: "users", complete: !!access.value?.groups.length, summary: access.value?.groups.length ? access.value.groups.map(group => group.groupName).join(", ") : t.setup.steps.access.todo },
]);
const firstMissing = computed(() => steps.value.findIndex(step => !step.complete));
const visible = computed(() => steps.value.filter((step, i) => firstMissing.value < 0 || i <= firstMissing.value || step.complete));
const allSaved = computed(() => firstMissing.value < 0);
const nextLabel = computed(() => t.setup.nextStep(firstMissing.value + 1, steps.value.length));
const strip = computed(() => steps.value.map((step, i) => ({
 id: step.id,
 label: step.short,
 detail: step.complete ? t.setup.done : i === firstMissing.value ? t.setup.next : t.setup.waiting,
 state: (step.complete ? "done" : i === firstMissing.value ? "current" : "todo") as "done" | "current" | "todo",
 available: !blocked.value && (step.complete || i <= firstMissing.value),
 controls: `ai-setup-${step.id}`,
})));
async function edit(id: Stage) {
 if (blocked.value) return;
 // The open step's own button (labelled "Otevřeno") would otherwise close its form for a tick and mount it again.
 if (active.value === id) return;
 active.value = undefined;
 // Let the previous owner release the guard before the next owner registers.
 await nextTick();
 if (!disposed) active.value = id;
}
async function openFromStrip(id: string) {
 await edit(id as Stage);
 await nextTick();
 root.value?.querySelector(`[data-ai-setup-step="${id}"]`)?.scrollIntoView({ block: "nearest", behavior: scrollBehavior(root.value) });
}
async function refresh() {
 loading.value = true; error.value = "";
 try {
  const [master, embed, answers, content, policy] = await Promise.all([
   api<AiSwitchView>("/admin/ai/switch"), api<AiProfileView>("/admin/ai/profiles/embeddings"),
   api<AiProfileView>("/admin/ai/profiles/chat"), api<IndexStatus>("/admin/ai/index"), api<AiAccessView>("/admin/ai/access"),
  ]);
  if (disposed) return;
  sw.value = master; embeddings.value = embed; chat.value = answers; index.value = content; access.value = policy;
  active.value = undefined;
  await nextTick();
  if (!disposed) active.value = steps.value.find(step => !step.complete)?.id;
 } catch (failure) { if (!disposed) error.value = errorMessage(failure); }
 finally { if (!disposed) loading.value = false; }
 // The summary of the features is an extra: a server without it still sets AI up.
 api<AiFeaturesView>("/admin/ai/features").then(view => { if (!disposed) features.value = view; }).catch(() => undefined);
 void health.refresh();
}
onMounted(refresh); onBeforeUnmount(() => { disposed = true; });
/** A saved step's form unmounts with its focused Save: the keyboard goes on in the form that opens next (StepFlow does the same). */
async function advance() {
 const here = document.activeElement;
 // Where a lost focus lands (the body, the view's heading) still counts as here; a field elsewhere keeps it.
 const keyboard = !here || here === document.body || here.tagName === "H1" || !!root.value?.contains(here);
 await refresh();
 if (!keyboard || disposed || !active.value) return;
 await nextTick();
 // The next form loads its profile first, so its first control appears a few frames later.
 const form = root.value?.querySelector(`#ai-setup-${active.value}`);
 if (form) await focusWhenPresent(form, 'input:not([disabled]):not([type="hidden"]), [role="radio"][tabindex="0"], [role="switch"]:not([disabled]), textarea:not([disabled]), button:not([disabled])', 60);
}

// What is set (board 07): the three models from the overview, the features and who may use AI.
const modelUses = (): Record<ProfileKind, string> => t.setup.modelUses;
const models = computed(() => (["chat", "quick", "embeddings", "rerank"] as ProfileKind[]).map(kind => {
 const status = health.overview.value?.profiles.find(profile => profile.kind === kind);
 return { kind, view: profileStateView(status), model: status?.model ?? null, href: router.href(`general/modules/ai/${kind}`) };
}));
const configuredModels = computed(() => models.value.filter(model => model.view.label !== t.states.notSet && model.kind !== "rerank").length);
const featureItems = computed(() => features.value?.items ?? []);
const featuresOn = computed(() => featureItems.value.filter(item => item.enabled).length);
const shownFeatures = computed(() => featureItems.value.slice(0, 5));
</script>

<template>
 <div ref="root" class="ai-setup" data-ai-setup>
  <ErrorState v-if="error" :title="t.setup.loadFailed" :text="error" :busy="loading" @retry="refresh" />
  <LoadingNote v-else-if="loading && !sw" :label="t.setup.loading" />
  <template v-else-if="sw">
   <FlowProgress variant="strip" :items="strip" :busy="blocked" :label="t.setup.progressLabel" @select="openFromStrip" />

   <SettingsSection :title="t.setup.title" icon="list-checks" :description="t.setup.description">
    <SettingsList :label="t.setup.listLabel">
     <SettingsListRow v-for="(step, i) in visible" :key="step.id" :title="step.title" :description="step.summary" :icon="step.complete ? 'circle-check' : step.icon" :icon-tone="step.complete ? 'success' : steps.indexOf(step) === firstMissing ? 'accent' : undefined" :class="{ 'is-next': !step.complete && steps.indexOf(step) === firstMissing }" :data-ai-setup-step="step.id">
      <template v-if="!step.complete && steps.indexOf(step) === firstMissing" #title><span class="ai-setup-eyebrow">{{ nextLabel }}</span>{{ step.title }}</template>
      <template #actions>
       <Button :variant="!step.complete && steps.indexOf(step) === firstMissing && active !== step.id ? 'default' : 'ghost'" size="sm" :disabled="blocked" :aria-expanded="active === step.id" :aria-controls="`ai-setup-${step.id}`" :aria-label="t.setup.editLabel(step.title)" :data-ai-setup-edit="step.id" @click="edit(step.id)">{{ active === step.id ? t.setup.open : step.complete ? t.setup.edit : t.setup.set }}<AtlasIcon name="down" :size="14" /></Button>
      </template>
      <InlineReveal :id="`ai-setup-${step.id}`" :open="active === step.id">
       <div v-if="active === step.id" class="ai-setup-form">
        <AiOverviewSection v-if="step.id === 'power'" guided @saved="advance" />
        <AiProfileSection v-else-if="step.id === 'embeddings' || step.id === 'chat'" :kind="step.id" guided @saved="advance" />
        <AiIndexSection v-else-if="step.id === 'index'" guided @changed="advance" />
        <AiAccessSection v-else @saved="advance" />
       </div>
      </InlineReveal>
     </SettingsListRow>
    </SettingsList>
    <Transition :css="false" v-bind="disclosureHooks"><p v-if="guard?.dirty.value" class="st-note" role="status">{{ t.setup.dirtyNote }}</p></Transition>
   </SettingsSection>

   <!-- Saving the last step completes the setup: the confirmation rises in instead of appearing in one frame. -->
   <Transition name="st-step">
   <SettingsSection v-if="allSaved" :title="t.setup.completeTitle" icon="circle-check" tone="success" data-ai-setup-complete>
    <p class="st-note">{{ t.setup.completeText }}</p>
    <div class="st-actions"><Button as-child variant="outline"><a :href="router.href('general/modules/ai/overview')">{{ t.setup.checkOperation }}</a></Button><Button as-child variant="ghost"><a :href="router.href('general/modules/ai/features')">{{ t.module.tabs.features }}</a></Button></div>
   </SettingsSection>
   </Transition>

   <div class="ai-setup-summary">
    <SettingsSection :title="t.models.title" data-ai-setup-models>
     <template #actions><span class="ai-setup-count">{{ t.setup.configuredCount(configuredModels) }}</span></template>
     <SettingsList :label="t.models.region">
      <SettingsListRow v-for="model in models" :key="model.kind" :title="kindLabels[model.kind]" :description="model.model ?? modelUses()[model.kind]" :icon="kindIcons[model.kind]" icon-tone="neutral" :data-ai-setup-model="model.kind">
       <template #title><a class="ai-setup-link" :href="model.href">{{ kindLabels[model.kind] }}</a></template>
       <template #meta><StatusBadge :tone="model.view.tone" :icon="model.view.icon" :label="model.view.label" size="sm" /></template>
      </SettingsListRow>
     </SettingsList>
    </SettingsSection>
    <SettingsSection :title="t.setup.featuresTitle" data-ai-setup-features>
     <template #actions><a class="st-link ai-setup-count" :href="router.href('general/modules/ai/features')"><template v-if="features">{{ t.setup.featuresOn(featuresOn, featureItems.length) }} · </template>{{ t.setup.edit }}</a></template>
     <ul v-if="shownFeatures.length" class="ai-setup-features st-bleed">
      <li v-for="item in shownFeatures" :key="item.task">
       <span class="ai-setup-feature-name">{{ featureTexts[item.task]?.title ?? item.task }}</span>
       <span class="ai-setup-feature-needs">{{ kindLabels[item.profileKind].toLocaleLowerCase(locale) }}</span>
       <StatusBadge :tone="item.enabled ? 'success' : 'neutral'" :icon="item.enabled ? 'circle-check' : 'ban'" :label="item.enabled ? t.states.on : t.states.off" size="sm" />
      </li>
     </ul>
     <p v-else class="st-note">{{ t.setup.featuresNote }}</p>
    </SettingsSection>
   </div>

   <SettingsSection :title="t.setup.whoTitle" icon="users" :description="access?.groups.length ? access.groups.map(group => group.groupName).join(', ') : t.setup.whoNone" data-ai-setup-access>
    <template #actions><Button as-child variant="outline" size="sm"><a :href="router.href('general/modules/ai/access')">{{ access?.groups.length ? t.setup.editGroups : t.access.addGroup }}</a></Button></template>
   </SettingsSection>

   <AiReportsSection v-if="embeddings?.configured || chat?.configured" kind="usage" compact />
  </template>
 </div>
</template>
<style scoped>
.ai-setup { display: grid; gap: 18px; min-width: 0; container: ai-setup / inline-size; }
.ai-setup-form { padding: 6px 0 14px; min-width: 0; }
/* The next step reads as the call to action of the page (board 07): a tinted band with its order above the title. */
.ai-setup :deep(.settings-list-row.is-next) { background: color-mix(in srgb, var(--accent-soft) 38%, var(--surface)); }
/* The list reaches the card's edge: a tinted last row follows the card's corners (14 px radius less its 1 px border). */
.ai-setup :deep(.settings-list-row.is-next:last-child) { border-radius: 0 0 calc(var(--st-radius) - 1px) calc(var(--st-radius) - 1px); }
.ai-setup-eyebrow { display: block; margin-bottom: 2px; color: var(--accent); font-size: 11.5px; font-weight: 700; letter-spacing: .06em; text-transform: uppercase; }
.ai-setup-summary { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 18px; align-items: start; }
@container settings-shell (max-width: 860px) { .ai-setup-summary { grid-template-columns: minmax(0, 1fr); } }
.ai-setup-count { color: var(--muted); font-size: 13px; font-weight: 500; }
a.ai-setup-count { color: var(--accent); font-weight: 600; }
.ai-setup-link { color: inherit; text-decoration: none; }
@media (hover: hover) and (pointer: fine) {
  .ai-setup-link:hover { color: var(--accent); text-decoration: underline; text-underline-offset: 3px; }
}
.ai-setup-features { display: grid; margin: 0; padding: 0; list-style: none; }
.ai-setup-features > li { display: flex; align-items: center; gap: 10px; min-height: 42px; padding: 0 var(--st-row-pad-x, 18px); border-top: 1px solid var(--st-line-soft, var(--line)); }
.ai-setup-features > li:first-child { border-top: 0; }
.ai-setup-feature-name { flex: 1; min-width: 0; }
.ai-setup-feature-needs { color: var(--muted); font-size: 12px; }
@container ai-setup (max-width: 560px) { .ai-setup-form { padding-top: 4px; } }
</style>
