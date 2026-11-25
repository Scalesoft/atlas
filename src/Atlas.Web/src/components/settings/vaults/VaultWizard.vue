<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { disclosureHooks } from "../disclosure";
import LoadingNote from "../LoadingNote.vue";
import { Button } from "../../ui/button";
import ChoiceCards from "../ChoiceCards.vue";
import OneTimeSecret from "../OneTimeSecret.vue";
import SearchableChoiceList from "../SearchableChoiceList.vue";
import SettingsRow from "../SettingsRow.vue";
import ProgressiveFlow from "../ProgressiveFlow.vue";
import SummaryList from "../SummaryList.vue";
import type { ChoiceOption, FlowStep, SummaryItem } from "../types";
import { vaultLocationKinds, vaultLocationRootReady, type VaultLocationDraft } from "../../../vaults";
import { formatDate } from "../../../formatDate";
import { foldText } from "../../../lib/foldText";
import { locale } from "../../../i18n";
import t from "../../../i18n/settings-vaults";
import type { VaultFileLister, VaultWizardDraft, VaultWizardFile, VaultWizardLocation, VaultWizardResult } from "./types";
import VaultLocationFields from "./VaultLocationFields.vue";

/**
 * Přidat trezor reveals each section as its prerequisites become valid and keeps earlier
 * choices editable: Co (ChoiceCards, založit / připojit) · Prostor ·
 * Umístění (+ "Nové umístění" inserts its own form) · Soubor a název (attaching picks a file
 * tile from `listFiles`; typing the name is only the fallback when listing fails) · 5 Heslo · 6 Souhrn · 7 Hotovo
 * (OneTimeSecret when the server generated the password). The space section is skipped when `spaceId`
 * is given. Data comes in as props; `submit` performs the creation (vaultApi.create / attach) and
 * returns the result, or rejects with the reason shown on the summary step.
 */
const props = withDefaults(
  defineProps<{
    spaces: ChoiceOption[];
    locations: VaultWizardLocation[];
    spaceId?: string;
    /** Only the wiki administrator may add a location. */
    canAddLocation?: boolean;
    submit: (draft: VaultWizardDraft) => Promise<VaultWizardResult>;
    testLocation?: (draft: VaultLocationDraft) => Promise<string>;
    /** Lists a location's files for attaching; without it (or when it fails) the file name is typed. */
    listFiles?: VaultFileLister;
  }>(),
  { spaceId: undefined, canAddLocation: true, testLocation: undefined, listFiles: undefined },
);
const emit = defineEmits<{ done: [vault: VaultWizardResult]; open: [vault: VaultWizardResult]; cancel: [] }>();

const step = ref("mode");
const mode = ref<string | null>(null);
const spaceId = ref<string | null>(props.spaceId ?? null);
const locationId = ref<string | null>(props.locations.length === 1 && !props.canAddLocation ? props.locations[0]!.id : null);
const newLocation = ref<VaultLocationDraft>({ name: "", kind: "smb", root: "smb://", username: "", domain: "", password: "" });
const name = ref("");
const fileName = ref("");
const fileTouched = ref(false);
const attachFile = ref<string | null>(null);
const passwordMode = ref<string | null>("generate");
const password = ref("");
const passwordAgain = ref("");
const attachPassword = ref("");
const busy = ref(false);
const failure = ref("");
const created = ref<VaultWizardResult>();
const generated = ref("");
const nameTouched = ref(false);
const attachFileTouched = ref(false);
const passwordTouched = ref(false);

const attach = computed(() => mode.value === "attach");
const addingLocation = computed(() => locationId.value === "__new");
const location = computed(() => (addingLocation.value ? undefined : props.locations.find((item) => item.id === locationId.value)));
const space = computed(() => props.spaces.find((item) => item.value === spaceId.value));
// Attaching picks from the folder listing; the typed name is the fallback when listing fails.
const files = ref<VaultWizardFile[] | null>(null);
const filesTruncated = ref(false);
const filesLoading = ref(false);
const filesError = ref("");
const fileQuery = ref("");
const manualFile = ref(!props.listFiles);
const listed = computed(() => !manualFile.value);
let listing: AbortController | undefined;
let listTimer: ReturnType<typeof setTimeout> | undefined;
async function loadFiles() {
  if (!props.listFiles || !attach.value || !spaceId.value || !location.value || manualFile.value) return;
  listing?.abort();
  const controller = (listing = new AbortController());
  filesLoading.value = true;
  filesError.value = "";
  try {
    const result = await props.listFiles(location.value.id, fileQuery.value.trim(), controller.signal);
    if (controller.signal.aborted) return;
    files.value = result.files;
    filesTruncated.value = result.truncated;
  } catch (reason) {
    if (controller.signal.aborted) return;
    files.value = null;
    filesError.value = reason instanceof Error ? reason.message : t.wizard.filesFailed;
  } finally {
    if (!controller.signal.aborted) filesLoading.value = false;
  }
}
watch(fileQuery, () => {
  clearTimeout(listTimer);
  listing?.abort();
  if (attach.value && !manualFile.value) listTimer = setTimeout(() => void loadFiles(), 250);
});
watch([locationId, mode, spaceId], () => {
  listing?.abort();
  clearTimeout(listTimer);
  files.value = null;
  filesError.value = "";
  filesLoading.value = false;
  filesTruncated.value = false;
  fileQuery.value = "";
  attachFile.value = null;
  attachPassword.value = "";
  attachFileTouched.value = false;
  manualFile.value = !props.listFiles || addingLocation.value;
}, { flush: "sync" });
watch([locationId, mode, spaceId, manualFile], () => {
  if (attach.value && !manualFile.value) void loadFiles();
});
onBeforeUnmount(() => {
  listing?.abort();
  clearTimeout(listTimer);
});

const modeOptions: ChoiceOption[] = [
  { value: "create", label: t.wizard.create, description: t.wizard.createHint, icon: "plus" },
  { value: "attach", label: t.wizard.attach, description: t.wizard.attachHint, icon: "plug" },
];
const locationOptions = computed<ChoiceOption[]>(() => [
  ...props.locations.map((item) => ({
    value: item.id,
    label: item.name,
    description: item.root ? `${item.root} · ${t.count(item.vaultCount)}` : t.count(item.vaultCount),
    icon: vaultLocationKinds[item.kind].icon,
    badge: vaultLocationKinds[item.kind].badge,
  })),
  ...(props.canAddLocation ? [{ value: "__new", label: t.wizard.newLocation, description: t.wizard.newLocationHint, icon: "folder-plus", tone: "neutral" as const }] : []),
]);
const fileSize = (bytes: number) => (bytes < 1024 ? `${bytes} B` : `${Math.max(1, Math.round(bytes / 1024)).toLocaleString(locale)} KiB`);
const fileOptions = computed<ChoiceOption[]>(() =>
  (files.value ?? []).map((file) => ({
    value: file.name,
    label: file.name,
    description: t.wizard.fileDescription(fileSize(file.sizeBytes), formatDate(file.lastWriteAt)),
    icon: "key-round",
    disabled: file.registered,
    disabledReason: file.registered ? (file.usedBy ? t.wizard.usedBy(file.usedBy) : t.wizard.usedByOther) : undefined,
  })),
);
const passwordOptions: ChoiceOption[] = [
  { value: "generate", label: t.wizard.generate, description: t.wizard.generateHint, icon: "shield-check", badge: t.wizard.recommended },
  { value: "custom", label: t.wizard.custom, description: t.wizard.customHint, icon: "key-round" },
];

const kdbx = /^[\w.\- ]+\.kdbx$/i;
const slug = (text: string) => foldText(text, "block").replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "");
watch(name, (value) => {
  if (!attach.value && !fileTouched.value) fileName.value = value ? `${slug(value)}.kdbx` : "";
});
watch(attachFile, (value) => {
  attachPassword.value = "";
  if (value && !name.value && (listed.value || kdbx.test(value))) name.value = value.replace(/\.kdbx$/i, "").replace(/[-_]+/g, " ").replace(/^\w/, (letter) => letter.toLocaleUpperCase("cs"));
});
watch(mode, () => {
  password.value = passwordAgain.value = "";
  passwordTouched.value = false;
  fileTouched.value = false;
  if (!attach.value) fileName.value = name.value ? `${slug(name.value)}.kdbx` : "";
});
watch([name, fileName, attachFile, password, passwordAgain, attachPassword, mode, spaceId, locationId, passwordMode, newLocation], () => {
  failure.value = "";
}, { deep: true });
watch(passwordMode, () => {
  password.value = passwordAgain.value = "";
  passwordTouched.value = false;
});
watch(() => [newLocation.value.kind, newLocation.value.root], () => {
  if (addingLocation.value) {
    attachFile.value = null;
    attachPassword.value = "";
  }
});

const nameError = computed(() => (nameTouched.value && !name.value.trim() ? t.wizard.nameError : undefined));
const fileError = computed(() => {
  if (attach.value || (!fileTouched.value && !fileName.value)) return undefined;
  if (!kdbx.test(fileName.value)) return t.wizard.fileError;
  return undefined;
});
const attachFileError = computed(() =>
  attach.value && !listed.value && attachFileTouched.value && !kdbx.test(attachFile.value ?? "") ? t.wizard.attachFileError : undefined,
);
const strength = computed(() => {
  const value = password.value;
  return [value.length >= 12, value.length >= 16, /[A-Z]/.test(value) && /[a-z]/.test(value), /\d/.test(value) && /[^\w]/.test(value)].filter(Boolean).length;
});
const strengthLabel = computed(() => t.wizard.strength[strength.value]!);
const passwordError = computed(() => {
  if (!passwordTouched.value) return undefined;
  if (password.value.length < 12) return t.wizard.passwordShort;
  if (password.value !== passwordAgain.value) return t.wizard.passwordMismatch;
  return undefined;
});
const targetFile = computed(() => (attach.value ? (attachFile.value ?? "") : fileName.value));
const targetPath = computed(() => {
  const root = addingLocation.value ? newLocation.value.root : location.value?.root;
  // Only the wiki administrator sees folder paths; everyone else sees the location's name.
  if (!root) return `${location.value?.name ?? ""} · ${targetFile.value}`;
  const separator = root.includes("/") ? "/" : "\\";
  return `${root.replace(/[\\/]+$/, "")}${separator}${targetFile.value}`;
});
const fileReady = computed(() => !!name.value.trim() && (attach.value && listed.value ? !!files.value?.some((file) => file.name === attachFile.value && !file.registered) : kdbx.test(targetFile.value)));
const passwordReady = computed(() => attach.value ? !!attachPassword.value : passwordMode.value === "generate" || (password.value.length >= 12 && password.value === passwordAgain.value));
const newLocationReady = computed(() => !!newLocation.value.name.trim() && vaultLocationRootReady(newLocation.value));

const steps = computed<FlowStep[]>(() => {
  const list: FlowStep[] = [
    { id: "mode", label: t.wizard.whatStep, icon: "lock", title: t.wizard.whatTitle, hint: t.wizard.whatHint, summary: mode.value ? (attach.value ? t.wizard.attachShort : t.wizard.createShort) : undefined, ready: !!mode.value },
  ];
  if (!props.spaceId) list.push({ id: "space", label: t.wizard.spaceStep, icon: "book-open", title: t.wizard.spaceTitle, hint: t.wizard.spaceHint, summary: space.value?.label, ready: !!space.value });
  list.push({ id: "location", label: t.wizard.whereStep, icon: "folder", title: attach.value ? t.wizard.whereAttach : t.wizard.whereCreate, hint: t.wizard.whereHint, summary: addingLocation.value ? newLocation.value.name || t.wizard.newLocation : location.value?.name, ready: !!location.value || (addingLocation.value && props.canAddLocation) });
  if (addingLocation.value)
    list.push({ id: "newLocation", label: t.wizard.newLocation, icon: "folder-plus", title: t.wizard.newLocation, hint: t.wizard.newLocationHintStep, summary: newLocation.value.name || undefined, ready: newLocationReady.value });
  list.push({
    id: "file",
    label: t.wizard.fileStep,
    icon: "file-text",
    title: attach.value ? t.wizard.fileTitleAttach : t.wizard.fileTitleCreate,
    hint: attach.value
      ? listed.value ? t.wizard.fileHintListed : t.wizard.fileHintTyped
      : t.wizard.fileHintCreate,
    summary: targetFile.value || undefined,
    ready: fileReady.value,
  });
  list.push({
    id: "password",
    label: t.wizard.passwordStep,
    icon: "key-round",
    title: attach.value ? t.wizard.passwordTitleAttach : t.wizard.passwordTitleCreate,
    hint: attach.value ? t.wizard.passwordHintAttach : t.wizard.passwordHintCreate,
    summary: attach.value ? (attachPassword.value ? t.wizard.entered : undefined) : passwordMode.value === "generate" ? t.wizard.generate : password.value ? t.wizard.own : undefined,
    ready: passwordReady.value,
  });
  list.push({ id: "summary", label: t.wizard.summaryStep, icon: "circle-check", title: t.wizard.summaryTitle, hint: t.wizard.summaryHint, manual: true, nextLabel: attach.value ? t.wizard.attachVault : t.wizard.createVault, nextIcon: attach.value ? "plug" : "plus" });
  list.push({ id: "done", label: t.wizard.doneStep, icon: "check", title: created.value ? t.wizard.ready(created.value.name) : t.wizard.doneStep, final: true });
  return list;
});

const summary = computed<SummaryItem[]>(() => [
  { key: "mode", icon: attach.value ? "plug" : "plus", label: t.wizard.action, value: attach.value ? t.wizard.attachExisting : t.wizard.create },
  { key: "name", icon: "lock", label: t.wizard.name, value: name.value },
  { key: "space", icon: space.value?.icon ?? "book-open", label: t.wizard.space, value: space.value?.label },
  { key: "location", icon: vaultLocationKinds[(addingLocation.value ? newLocation.value.kind : location.value?.kind) ?? "managed"].icon, label: t.wizard.location, value: addingLocation.value ? t.wizard.isNew(newLocation.value.name) : location.value?.name },
  { key: "file", icon: "file-text", label: t.wizard.file, value: targetPath.value, code: true },
  { key: "password", icon: "key-round", label: t.wizard.masterPassword, value: attach.value ? t.wizard.enteredPassword : passwordMode.value === "generate" ? t.wizard.generatedOnce : t.wizard.ownPassword },
  { key: "protection", icon: "fingerprint", label: t.wizard.protection, value: t.wizard.protectionValue },
  { key: "access", icon: "shield-check", label: t.wizard.access, value: t.wizard.accessValue },
]);

async function onNext(current: FlowStep) {
  if (current.id !== "summary" || busy.value || steps.value.some((item) => item.ready === false)) return;
  busy.value = true;
  failure.value = "";
  const draft = newLocation.value;
  try {
    created.value = await props.submit({
      mode: attach.value ? "attach" : "create",
      vault: { spaceId: spaceId.value!, locationId: addingLocation.value ? "" : locationId.value!, name: name.value.trim(), fileName: targetFile.value.trim() },
      password: attach.value ? attachPassword.value : passwordMode.value === "generate" ? null : password.value,
      newLocation: addingLocation.value ? { ...draft, name: draft.name.trim(), root: draft.root.trim() } : undefined,
    });
    generated.value = created.value.generatedPassword ?? "";
    password.value = passwordAgain.value = attachPassword.value = "";
    step.value = "done";
  } catch (reason) {
    failure.value = reason instanceof Error ? reason.message : t.wizard.failed;
  } finally {
    busy.value = false;
  }
}
function finish() {
  generated.value = "";
  if (created.value) emit("done", created.value);
}
</script>

<template>
  <ProgressiveFlow autofocus v-model="step" :steps="steps" :label="t.wizard.label" :context="space ? t.wizard.inSpace(space.label) : t.wizard.vaults" :busy="busy" @next="onNext" @cancel="emit('cancel')">
    <template #mode>
      <ChoiceCards v-model="mode" :options="modeOptions" :label="t.wizard.whatLabel" hide-label :columns="2" size="hero" />
    </template>
    <template #space>
      <ChoiceCards v-model="spaceId" :options="spaces" :label="t.wizard.space" hide-label :columns="2" />
    </template>
    <template #location>
      <ChoiceCards v-if="locationOptions.length" v-model="locationId" :options="locationOptions" :label="t.wizard.location" hide-label :columns="2" />
      <p v-else class="st-note" role="status"><AtlasIcon name="triangle-alert" :size="15" />{{ t.wizard.noLocation }}</p>
      <p class="st-note"><AtlasIcon name="info" :size="15" />{{ t.wizard.adminAdds }}</p>
    </template>
    <template #newLocation>
      <VaultLocationFields v-model="newLocation" :test="testLocation" />
    </template>
    <template #file>
      <div class="vault-form">
        <template v-if="attach && listed">
          <SearchableChoiceList v-model="attachFile" v-model:query="fileQuery" :options="fileOptions" :label="t.wizard.filesLabel" :placeholder="t.wizard.searchFile" :empty-text="filesLoading ? t.wizard.loadingFiles : fileQuery ? t.wizard.noMatch : t.wizard.noFiles" :aria-busy="filesLoading" data-vault-files />
          <LoadingNote :active="filesLoading" :label="t.wizard.loadingFiles" />
          <p v-if="filesTruncated" class="vault-files-note"><AtlasIcon name="info" :size="15" />{{ t.wizard.truncated }}</p>
          <div v-if="filesError" class="vault-files-error" role="alert" data-vault-files-error>
            <p><AtlasIcon name="triangle-alert" :size="15" />{{ filesError }}</p>
            <div class="st-actions">
              <Button variant="outline" size="sm" type="button" @click="loadFiles"><AtlasIcon name="refresh" :size="15" />{{ t.wizard.reload }}</Button>
              <Button variant="ghost" size="sm" type="button" data-vault-manual-file @click="manualFile = true"><AtlasIcon name="pencil" :size="15" />{{ t.wizard.typeName }}</Button>
            </div>
          </div>
        </template>
        <SettingsRow v-if="attach && !listed" v-slot="{ id, describedBy, invalid }" :label="t.wizard.fileInLocation" :help="t.wizard.fileInLocationHelp" :error="attachFileError">
          <input :id="id" v-model="attachFile" class="st-input st-mono" :aria-describedby="describedBy" :aria-invalid="invalid" spellcheck="false" data-step-focus data-vault-attach-file @input="attachFileTouched = true" />
        </SettingsRow>
        <SettingsRow v-slot="{ id, describedBy, invalid }" :label="t.wizard.vaultName" :help="t.wizard.vaultNameHelp" :error="nameError">
          <input :id="id" v-model="name" class="st-input" :placeholder="t.wizard.vaultNamePlaceholder" :aria-describedby="describedBy" :aria-invalid="invalid" maxlength="200" data-vault-name :data-step-focus="attach ? undefined : ''" @input="nameTouched = true" />
        </SettingsRow>
        <SettingsRow v-if="!attach" v-slot="{ id, describedBy, invalid }" :label="t.wizard.file" :help="t.wizard.fileHelp" :error="fileError">
          <input :id="id" v-model="fileName" class="st-input st-mono" :aria-describedby="describedBy" :aria-invalid="invalid" spellcheck="false" data-vault-file @input="fileTouched = true" />
        </SettingsRow>
        <p class="vault-path"><AtlasIcon :name="attach ? 'plug' : 'file-plus'" :size="15" />{{ attach ? t.wizard.willAttach : t.wizard.willCreate }}: <code class="st-mono">{{ targetPath }}</code></p>
      </div>
    </template>
    <template #password>
      <div v-if="attach" class="vault-form">
        <SettingsRow v-slot="{ id, describedBy }" :label="t.wizard.passwordTitleAttach" :help="t.wizard.attachPasswordHelp">
          <input :id="id" v-model="attachPassword" class="st-input" type="password" autocomplete="off" :aria-describedby="describedBy" data-step-focus data-vault-attach-password />
        </SettingsRow>
      </div>
      <ChoiceCards v-else v-model="passwordMode" :options="passwordOptions" :label="t.wizard.masterPassword" hide-label :columns="2" :reveal-for="['custom']">
        <template #reveal>
          <div class="vault-form">
            <SettingsRow v-slot="{ id, describedBy, invalid }" :label="t.wizard.password" :help="t.wizard.passwordHelp" :error="passwordError">
              <input :id="id" v-model="password" class="st-input" type="password" autocomplete="new-password" :aria-describedby="describedBy" :aria-invalid="invalid" @input="passwordTouched = true" />
              <span class="vault-strength" :data-score="strength" aria-live="polite"><span class="vault-strength-bar"><i v-for="index in 4" :key="index" :data-on="index <= strength || undefined" /></span>{{ password ? strengthLabel : "" }}</span>
            </SettingsRow>
            <SettingsRow v-slot="{ id }" :label="t.wizard.passwordAgain">
              <input :id="id" v-model="passwordAgain" class="st-input" type="password" autocomplete="new-password" @input="passwordTouched = true" />
            </SettingsRow>
          </div>
        </template>
      </ChoiceCards>
    </template>
    <template #summary>
      <SummaryList :items="summary" :label="t.wizard.summaryLabel" />
      <LoadingNote v-if="busy" :label="attach ? t.wizard.verifying : t.wizard.creating" />
      <Transition :css="false" v-bind="disclosureHooks">
        <p v-if="failure" class="vault-failure" role="alert" data-vault-wizard-error><AtlasIcon name="triangle-alert" :size="15" />{{ failure }}</p>
      </Transition>
    </template>
    <template #done>
      <div class="vault-done">
        <OneTimeSecret v-if="generated" :value="generated" @done="finish" />
        <template v-else>
          <p class="vault-done-ok" role="status"><span class="vault-done-check"><AtlasIcon name="check" :size="18" /></span>{{ attach ? t.wizard.attached(created?.entryCount ?? 0) : t.wizard.created }}</p>
          <div class="st-actions">
            <Button type="button" data-vault-open @click="created && emit('open', created)"><AtlasIcon name="lock" :size="16" />{{ t.wizard.open }}</Button>
            <Button variant="outline" type="button" @click="finish"><AtlasIcon name="list" :size="16" />{{ t.wizard.back }}</Button>
          </div>
        </template>
      </div>
    </template>
  </ProgressiveFlow>
</template>

<style scoped>
.vault-form { display: grid; gap: 4px; }
.vault-path { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; margin: 4px 0 0; color: var(--muted); font-size: 12.5px; }
.vault-path code { color: var(--text); overflow-wrap: anywhere; }
.vault-strength { display: flex; align-items: center; gap: 8px; color: var(--muted); font-size: 12px; }
.vault-strength-bar { display: flex; gap: 3px; }
.vault-strength-bar i { width: 26px; height: 5px; border-radius: 3px; background: var(--hover); transition: background-color var(--atlas-motion-base) var(--atlas-ease-out); }
.vault-strength[data-score="1"] i[data-on], .vault-strength[data-score="0"] i[data-on] { background: var(--error); }
.vault-strength[data-score="2"] i[data-on] { background: var(--warning); }
.vault-strength[data-score="3"] i[data-on], .vault-strength[data-score="4"] i[data-on] { background: var(--success); }
.vault-files-note { display: flex; align-items: center; gap: 6px; margin: 0; color: var(--muted); font-size: 12.5px; }
.vault-files-error { display: grid; gap: 6px; padding: 10px 12px; border: 1px solid color-mix(in srgb, var(--error) 35%, var(--line)); border-radius: var(--st-radius-sm); animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.vault-files-error p { display: flex; align-items: center; gap: 6px; margin: 0; color: var(--error); font-size: 13px; font-weight: 550; }
.vault-failure { display: flex; align-items: center; gap: 6px; margin: 0; color: var(--error); font-size: 13px; font-weight: 550; }
.vault-done { display: grid; gap: 12px; }
.vault-done-ok { display: flex; align-items: center; gap: 10px; margin: 0; font-size: 14.5px; font-weight: 600; }
.vault-done-check { display: grid; place-items: center; width: 32px; height: 32px; border-radius: 50%; color: var(--on-accent); background: var(--success); animation: st-pop var(--atlas-motion-pop) var(--atlas-ease-out); }
</style>
