<script setup lang="ts">
import t from "../../i18n/settings-setup";
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from "vue";
import "../settings/settings.css";
import "../settings/settings-tailwind.css";
import { Button } from "../ui/button";
import { post, errorMessage } from "../../api";
import AtlasIcon from "../AtlasIcon.vue";
import AtlasBrandLogo from "../AtlasBrandLogo.vue";
import SettingsPage from "../settings/SettingsPage.vue";
import LoadingSkeleton from "../LoadingSkeleton.vue";
import EmptyState from "../settings/EmptyState.vue";
import ErrorState from "../settings/ErrorState.vue";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "../ui/tabs";
import { createDirtyGuard, provideDirtyGuard } from "../settings/dirtyGuard";
import { provideReauthEnvironment } from "../settings/useReauth";
import { createApiReauthEnvironment } from "../settings/security/apiReauth";
import { createSecurityState } from "../settings/security/securityState";
import { loadModules, moduleEntry, moduleIcon, type ModuleDescriptor } from "../settings/general/moduleRegistry";
import { holdUnsavedWork } from "../../lib/unsavedWork";

/**
 * The module steps of the first-run setup ("Připojte své nástroje"): one tab per module that is
 * shown in setup plus "Obnovit ze zálohy", each hosting the same module form as Správa wiki →
 * Moduly a integrace (settings/general/moduleRegistry). The forms report unsaved changes through
 * this host's own dirty guard; while one has a draft the steps stay closed. The last step
 * completes the setup on the server.
 */
const props = withDefaults(defineProps<{ isWikiAdmin: boolean; setup?: boolean }>(), { setup: false });
const emit = defineEmits<{ complete: []; dirty: [value: boolean]; imported: [spaceId: string] }>();
const guard = createDirtyGuard();
provideDirtyGuard(guard);
// The module forms confirm a security change (the sign-in providers, the Hub) with a fresh sign-in, as in the settings host.
const security = createSecurityState();
provideReauthEnvironment(createApiReauthEnvironment(security));
void security.load();
const dirty = guard.dirty;
const items = ref<ModuleDescriptor[]>([]);
const selected = ref("");
const loading = ref(true);
const busy = ref(false);
const error = ref("");
watch(dirty, (value) => emit("dirty", value));
const heading = ref<HTMLElement>();
// Set by select(): the next step page to mount takes the focus, so a screen reader announces the module.
let focusStep = false;
const current = computed(() => items.value.find((item) => item.id === selected.value));
const entry = computed(() => moduleEntry(current.value?.settingsUi));
const index = computed(() => items.value.findIndex((item) => item.id === selected.value));
const backupImport: ModuleDescriptor = { id: "backup-import", title: t.backupImport.title, description: t.backupImport.description, settingsUi: "backup-import", order: 1000, showInSetup: true };

async function load() {
  if (!props.isWikiAdmin) {
    loading.value = false;
    return;
  }
  loading.value = true;
  error.value = "";
  try {
    const result = await loadModules();
    items.value = result.filter((item) => !props.setup || item.showInSetup);
    if (props.setup) items.value.push(backupImport);
    selected.value = items.value[0]?.id ?? "";
  } catch (failure) {
    error.value = errorMessage(failure);
  } finally {
    loading.value = false;
  }
}
async function select(id: string) {
  if (dirty.value || busy.value) return;
  focusStep = true;
  selected.value = id;
}
// Reka mounts the active panel's content after the tab state settles, so focus follows the mount.
function stepMounted(page: unknown) {
  const element = (page as { $el?: unknown } | null)?.$el;
  if (!focusStep || !(element instanceof HTMLElement)) return;
  focusStep = false;
  const title = element.querySelector<HTMLElement>("h2");
  if (!title) return;
  title.tabIndex = -1;
  // The panel is still hidden while it mounts; a hidden element cannot take the focus.
  requestAnimationFrame(() => title.focus());
}
async function next() {
  if (dirty.value || busy.value) return;
  if (index.value < items.value.length - 1) return select(items.value[index.value + 1]!.id);
  await finish();
}
async function finish() {
  if (dirty.value || busy.value || loading.value) return;
  busy.value = true;
  error.value = "";
  try {
    await post("/admin/setup/complete");
    emit("complete");
  } catch (failure) {
    error.value = errorMessage(failure);
  } finally {
    busy.value = false;
  }
}
function reloadPage() { window.location.reload(); }
onMounted(() => void load());
// A module form's draft is unsaved work the shell asks about before the tab closes.
onUnmounted(holdUnsavedWork(() => dirty.value));
</script>

<template>
  <section class="module-host settings-ui" :class="{ 'module-wizard': setup }" :aria-label="t.modules">
    <template v-if="isWikiAdmin">
      <header v-if="setup" class="wizard-heading">
        <span class="wizard-brand"><AtlasBrandLogo motion="hero" /></span>
        <div>
          <span class="eyebrow">{{ t.eyebrow }}</span>
          <h1 ref="heading" tabindex="-1">{{ t.heading }}</h1>
          <p>{{ t.intro }}</p>
          <span class="completed"><AtlasIcon name="check" :size="14" />{{ t.completed }}</span>
        </div>
        <Button variant="outline" :disabled="dirty || busy || loading" data-setup-finish @click="finish">{{ busy ? t.opening : t.openWiki }}<AtlasIcon name="arrow-right" :size="16" /></Button>
      </header>
      <h2 v-else ref="heading" tabindex="-1" class="sr-only">{{ t.modulesHeading }}</h2>
      <LoadingSkeleton v-if="loading" :label="t.loading" />
      <Tabs v-else :model-value="selected" activation-mode="manual" class="module-layout">
        <!-- `as="nav"` keeps the element the scoped CSS styles by tag and the tests select on.
             @click rather than @update:model-value because reka-ui activates on mousedown, which
             would step past the `dirty || busy` guard on the way down. -->
        <TabsList as="nav" class="module-navigation" :aria-label="t.navigation">
          <TabsTrigger v-for="item in items" :key="item.id" :value="item.id" class="nav-item module-navigation-item" data-variant="list" :data-module="item.id" :disabled="dirty || busy" @click="select(item.id)">
            <AtlasIcon :name="moduleIcon(item.settingsUi)" /><span><strong>{{ item.title }}</strong><small>{{ item.description }}</small></span>
          </TabsTrigger>
        </TabsList>
        <TabsContent v-for="item in items" :key="item.id" :value="item.id" class="module-content">
          <SettingsPage v-if="item.id === selected" :ref="stepMounted" class="module-step-heading" :title="item.title" :description="item.description" :icon="moduleIcon(item.settingsUi)">
            <component :is="entry.component" v-if="entry" :key="selected" v-bind="item.settingsUi === 'backup-import' ? { importOnly: true } : {}" @imported="emit('imported', $event)" />
            <ErrorState v-else :title="t.formMissingTitle" :text="t.formMissingText" :retry-label="t.refresh" @retry="reloadPage" />
          </SettingsPage>
          <p v-if="dirty" class="dirty-hint" role="status">{{ t.dirty }}</p>
        </TabsContent>
      </Tabs>
      <footer v-if="setup && !loading">
        <Button v-if="index > 0" variant="outline" :disabled="dirty || busy" @click="select(items[index - 1]!.id)">{{ t.back }}</Button>
        <span class="muted">{{ items.length ? t.step(index + 1, items.length) : t.ready }}<small>{{ t.later }}</small></span>
        <Button variant="default" :disabled="dirty || busy" data-setup-next @click="next">{{ busy ? t.finishing : index === items.length - 1 || !items.length ? t.openWiki : t.nextModule }}<AtlasIcon name="arrow-right" :size="16" /></Button>
      </footer>
      <EmptyState v-if="!loading && !items.length && !error" icon="blocks" :title="t.emptyTitle" :text="t.emptyText" />
      <!-- A failed load retries the load; a failed finish is retried by the same Otevřít wiki button. -->
      <ErrorState v-if="error && !items.length" :title="t.loadFailed" :text="error" :busy="busy || loading" :retry-label="t.reload" @retry="load" />
      <p v-else-if="error" role="alert" class="notice error">{{ error }}</p>
    </template>
    <EmptyState v-else icon="lock" :title="t.adminOnlyTitle" :text="t.adminOnlyText" />
  </section>
</template>

<style scoped>
.module-host { color: var(--text); font-size: 14px; }
.module-wizard { width: min(1100px, calc(100% - 48px)); margin: 32px auto; background: var(--surface); border: 1px solid var(--line); border-radius: 24px; padding: 28px; box-shadow: 0 16px 60px #1639570a; box-sizing: border-box; }
.wizard-heading { display: flex; align-items: center; gap: 20px; margin: -28px -28px 28px; padding: 28px; border-bottom: 1px solid var(--line); border-radius: 24px 24px 0 0; background: linear-gradient(120deg, color-mix(in srgb, var(--accent) 7%, var(--surface)), var(--surface)); }
.wizard-heading > div { flex: 1; min-width: 0; }
.wizard-brand { width: 72px; height: 72px; flex: none; }
.eyebrow { color: var(--accent); font-size: 10px; font-weight: 700; letter-spacing: .12em; }
h1 { font-size: clamp(24px, 3vw, 30px); letter-spacing: -.035em; line-height: 1.2; margin: 8px 0; }
.wizard-heading p { color: var(--muted); line-height: 1.6; margin: 0; font-size: 13px; }
.completed { display: inline-flex; align-items: center; gap: 6px; margin-top: 12px; color: var(--accent); font-size: 11px; }
.module-layout { display: grid; grid-template-columns: 210px minmax(0, 1fr); gap: 24px; align-items: start; }
nav { display: flex; flex-direction: column; gap: 8px; border-right: 1px solid var(--line); }
nav strong { display: block; font-size: 13px; }
.module-navigation { width: 100%; height: auto; min-width: 0; padding: 0 18px 0 0; align-items: stretch; justify-content: flex-start; border-radius: 0; background: transparent; }
.module-navigation .module-navigation-item { flex: none; height: auto; min-width: 0; justify-content: flex-start; align-items: flex-start; white-space: normal; box-shadow: none; }
/* The shared navigation row (shell.css, .nav-item: 0,4,0) centred the icon on the whole two- or three-line item; it
   belongs to the title line. */
.module-navigation .module-navigation-item.nav-item[data-variant] { align-items: flex-start; }
.module-navigation-item > svg { margin-top: 1px; }
.module-navigation-item > span { min-width: 0; overflow-wrap: anywhere; }
.module-navigation-item small { display: block; margin-top: 5px; color: var(--muted); font-weight: 400; font-size: 12px; line-height: 1.5; }
.module-content { display: grid; gap: 16px; min-width: 0; margin: 0; }
/* Inactive Reka panels remain in the DOM. Never let grid override their hidden state. */
.module-content[hidden] { display: none; }
/* The step enters with SettingsPage's shared rise (motion tokens; reduced motion keeps the fade). */
footer { display: flex; align-items: center; gap: 12px; border-top: 1px solid var(--line); padding-top: 24px; margin-top: 16px; }
footer .muted { margin-right: auto; font-size: 12px; }
footer small { display: block; font-size: 11px; margin-top: 4px; }
.dirty-hint { color: var(--muted); font-size: 12px; margin: 0; }
@media (max-width: 820px) {
  .module-wizard { width: calc(100% - 24px); padding: 20px; margin: 12px auto; }
  .wizard-heading { margin: -20px -20px 20px; padding: 20px; flex-wrap: wrap; gap: 14px; }
  .wizard-brand { width: 48px; height: 48px; }
  .wizard-heading > div { flex-basis: calc(100% - 62px); }
  .module-layout { grid-template-columns: minmax(0, 1fr); gap: 24px; }
  nav { border-right: 0; border-bottom: 1px solid var(--line); }
  .module-navigation { flex-direction: row; flex-wrap: wrap; padding: 0 0 12px; }
  .module-navigation .module-navigation-item { flex: 1 1 140px; }
  .module-navigation-item small { display: none; }
  footer { flex-wrap: wrap; }
  h1 { font-size: 27px; }
}
@media (max-width: 480px) {
  .module-wizard { padding: 16px; }
  .wizard-heading { margin: -16px -16px 20px; padding: 20px 16px; }
  .module-navigation .module-navigation-item { flex-basis: 120px; padding: 10px; }
  footer .muted { flex-basis: 100%; order: -1; }
  footer > button:last-child { margin-left: auto; }
}
</style>
