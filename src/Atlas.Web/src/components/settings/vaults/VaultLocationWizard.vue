<script setup lang="ts">
import t from "../../../i18n/settings-vaults";
import { computed, ref, useId, watch } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { disclosureHooks } from "../disclosure";
import LoadingNote from "../LoadingNote.vue";
import ChoiceCards from "../ChoiceCards.vue";
import FlowProgress from "../FlowProgress.vue";
import { Button } from "../../ui/button";
import InlineReveal from "../InlineReveal.vue";
import SettingsSection from "../SettingsSection.vue";
import SummaryList from "../SummaryList.vue";
import type { ChoiceOption, SummaryItem } from "../types";
import { vaultLocationKinds, vaultLocationRootReady, withVaultLocationKind, type VaultLocation, type VaultLocationDraft, type VaultLocationKind } from "../../../vaults";
import VaultLocationFields from "./VaultLocationFields.vue";

/**
 * Add or edit a vault location in place: selecting a kind reveals fields, then a live summary.
 * Saving is explicit. Store-free: the
 * location to edit, `test` and `submit` come in as props; `submit` resolves with the saved location
 * or rejects with the reason, shown on the summary. `dirty` reports whether the draft differs.
 */
const props = withDefaults(
  defineProps<{
    location?: VaultLocation;
    test?: (draft: VaultLocationDraft) => Promise<string>;
    submit: (draft: VaultLocationDraft) => Promise<VaultLocation>;
  }>(),
  { location: undefined, test: undefined },
);
const emit = defineEmits<{ done: [location: VaultLocation]; cancel: []; dirty: [value: boolean] }>();

const initial = (): VaultLocationDraft => ({
  name: props.location?.name ?? "",
  kind: props.location?.kind ?? "smb",
  root: props.location?.root ?? (props.location && props.location.kind !== "smb" ? "" : "smb://"),
  username: props.location?.username ?? "",
  domain: props.location?.domain ?? "",
  password: "",
});
const draft = ref<VaultLocationDraft>(initial());
const selectedKind = ref<VaultLocationDraft["kind"] | null>(props.location?.kind ?? null);
const form = ref<HTMLFormElement>();
const uid = useId();
const busy = ref(false);
const failure = ref("");
const start = JSON.stringify(initial());
watch(draft, (value) => {
  emit("dirty", JSON.stringify(value) !== start);
  failure.value = "";
}, { deep: true });

const kindOptions: ChoiceOption[] = (Object.keys(vaultLocationKinds) as VaultLocationKind[])
  .map((value) => ({ value, label: vaultLocationKinds[value].label, description: vaultLocationKinds[value].description, icon: vaultLocationKinds[value].icon }));
const kind = computed({
  get: () => selectedKind.value,
  set: (value) => {
    if (!value || busy.value) return;
    selectedKind.value = value;
    draft.value = withVaultLocationKind(draft.value, value);
  },
});
const rootReady = computed(() => vaultLocationRootReady(draft.value));

const ready = computed(() => !!selectedKind.value && !!draft.value.name.trim() && rootReady.value);
// Once revealed, keep the summary in place while earlier choices are edited.
const summaryShown = ref(false);
watch(ready, (value) => { if (value) summaryShown.value = true; }, { immediate: true, flush: "sync" });
const missing = computed(() => {
  if (!draft.value.name.trim() && !rootReady.value) return t.location.nameAndPath;
  if (!draft.value.name.trim()) return t.location.name;
  return draft.value.root.trim() && draft.value.root.trim() !== "smb://"
    ? t.location.adjustPath
    : t.location.path;
});
const progress = computed(() => [
  { id: "kind", label: selectedKind.value ? vaultLocationKinds[selectedKind.value].badge : t.location.kind, state: selectedKind.value ? "done" : "current", available: true, detail: selectedKind.value ? t.location.selected : t.location.chooseKind },
  { id: "folder", label: t.location.folderAndAccess, state: ready.value ? "done" : selectedKind.value ? "current" : "todo", available: !!selectedKind.value, detail: ready.value ? t.location.filledIn : selectedKind.value ? t.location.toFill : t.location.afterKind },
  { id: "summary", label: t.location.review, state: ready.value ? "current" : "todo", available: summaryShown.value, detail: ready.value ? t.location.ready : summaryShown.value ? t.location.waiting : t.location.afterDetails },
].map((item) => ({ ...item, state: item.state as "done" | "current" | "todo", controls: `${uid}-${item.id}` })));
function goToSection(id: string) {
  const target = form.value?.querySelector<HTMLElement>(`[data-location-section="${id}"]`);
  target?.focus({ preventScroll: true });
  target?.scrollIntoView?.({ block: "nearest" });
}
const summary = computed<SummaryItem[]>(() => [
  { key: "name", icon: "folder", label: t.location.nameLabel, value: draft.value.name.trim() || t.location.addName },
  { key: "kind", icon: vaultLocationKinds[draft.value.kind].icon, label: t.location.kind, value: vaultLocationKinds[draft.value.kind].label },
  // A managed folder is known once the location is saved (the server derives it from the location's id).
  draft.value.kind === "managed"
    ? { key: "root", icon: "file-text", label: t.location.folder, value: draft.value.root.trim() || t.location.atlasCreates, code: !!draft.value.root.trim() }
    : { key: "root", icon: "file-text", label: t.location.folder, value: rootReady.value ? draft.value.root.trim() : t.location.addPath, code: rootReady.value },
  ...(draft.value.kind === "smb"
    ? [
        { key: "account", icon: "account", label: t.location.serviceAccount, value: [draft.value.domain.trim(), draft.value.username.trim()].filter(Boolean).join("\\") || t.location.noSignIn },
        { key: "password", icon: "key-round", label: t.location.accountPassword, value: draft.value.password ? t.location.newPassword : props.location?.hasPassword ? t.location.keepStored : t.location.noPassword },
      ]
    : []),
]);

async function save() {
  if (!ready.value || busy.value) return;
  busy.value = true;
  failure.value = "";
  try {
    const saved = await props.submit({ ...draft.value, name: draft.value.name.trim(), root: draft.value.kind === "managed" ? "" : draft.value.root.trim(), username: draft.value.username.trim(), domain: draft.value.domain.trim() });
    draft.value.password = "";
    emit("dirty", false);
    emit("done", saved);
  } catch (reason) {
    failure.value = reason instanceof Error ? reason.message : t.location.saveFailed;
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <SettingsSection :title="location ? t.location.edit : t.location.add" icon="folder">
    <form ref="form" class="location-form" :aria-busy="busy" @submit.prevent="save">
      <FlowProgress :items="progress" :busy="busy" @select="goToSection" />
      <div :id="`${uid}-kind`" data-location-section="kind" tabindex="-1" role="group" :aria-label="t.fields.kind">
        <ChoiceCards :model-value="kind" :options="kindOptions" :label="t.location.where" :description="t.location.whereHelp" :disabled="busy" :columns="3" size="hero" @update:model-value="kind = $event as VaultLocationKind" />
      </div>
      <InlineReveal :open="!!selectedKind">
        <div :id="`${uid}-folder`" class="location-block" data-location-section="folder" tabindex="-1" role="group" :aria-label="t.location.folderAndAccess">
          <VaultLocationFields v-model="draft" :show-kind="false" :has-stored-password="location?.hasPassword" :test="test" :disabled="busy" />
        </div>
      </InlineReveal>
      <InlineReveal :open="summaryShown">
        <SettingsSection :id="`${uid}-summary`" data-location-section="summary" tabindex="-1" :title="t.location.willSave" icon="circle-check" class="location-summary location-block">
          <SummaryList :items="summary" :label="t.location.summary" />
          <p v-if="!ready" class="location-incomplete" role="status"><AtlasIcon name="info" :size="16" />{{ missing }}</p>
          <p class="st-muted">{{ location ? t.location.changeNote : t.location.addNote }}</p>
        </SettingsSection>
      </InlineReveal>
      <Transition :css="false" v-bind="disclosureHooks">
        <p v-if="failure" class="location-failure" role="alert" data-location-error><AtlasIcon name="triangle-alert" :size="15" />{{ failure }}</p>
      </Transition>
      <footer class="location-actions">
        <Button type="button" variant="ghost" :disabled="busy" @click="emit('cancel')"><AtlasIcon name="close" :size="16" />{{ t.location.cancel }}</Button>
        <!-- The status sits between the buttons with its space reserved, so saving never moves them. -->
        <LoadingNote inline :active="busy" :label="t.location.saving" />
        <Button type="submit" :disabled="!ready || busy"><AtlasIcon name="check" :size="16" />{{ location ? t.location.saveChanges : t.location.add }}</Button>
      </footer>
    </form>
  </SettingsSection>
</template>

<style scoped>
/* The space between the parts is a margin of what is shown, not a grid gap: a closed InlineReveal reserves nothing. */
.location-form { display: grid; }
.location-form > :not(.inline-reveal-root):not(:first-child), .location-block { margin-top: 18px; }
[data-location-section]:focus-visible { outline: 2px solid var(--accent); outline-offset: 3px; --focus-offset: 3px; }
.location-incomplete { display: flex; align-items: center; gap: 8px; margin: 0; color: var(--text); font-size: 13px; }
.location-summary { border-color: var(--accent); background: var(--accent-soft); }
.location-actions { display: flex; flex-wrap: wrap; justify-content: space-between; gap: 12px; padding-top: 14px; border-top: 1px solid var(--line); }
.location-failure { display: flex; align-items: center; gap: 6px; margin: 0; color: var(--error); font-size: 13px; font-weight: 550; }
</style>
