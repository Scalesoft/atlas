<script setup lang="ts">
import { computed, ref, watch } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import { api, errorMessage } from "../../../api";
import t from "../../../i18n/settings-backups";
import ChoiceCards from "../ChoiceCards.vue";
import ConfirmDialog from "../ConfirmDialog.vue";
import InlineReveal from "../InlineReveal.vue";
import ResultBanner from "../ResultBanner.vue";
import SettingsRow from "../SettingsRow.vue";
import SettingsSection from "../SettingsSection.vue";
import SummaryList from "../SummaryList.vue";
import type { ChoiceOption, SummaryItem } from "../types";
import ZipDropzone from "./ZipDropzone.vue";
import { disclosureHooks } from "../disclosure";

/**
 * Importovat zálohu: add a portable backup ZIP as copies, or restore the matching archived pages.
 * The server checks the archive first (what it holds, the suffix of copies); restoring then asks in a
 * ConfirmDialog because it overwrites pages. Used on the Zálohy page and, with `importOnly`, as the
 * "Obnovit ze zálohy" step of the first-run setup. It touches only content, never accounts or rights.
 * Revisions the editor would refuse are listed by the check, and the server refuses such an archive.
 */
type ImportMode = "add" | "restore";
interface ImportPreview {
  suffix: string;
  pages: number;
  attachments: number;
  message: string;
  /** Archived revisions the editor would refuse (the server lists the first 200): the server refuses the import while there are any. */
  invalid?: number;
  invalidDocuments?: { pageId: string; version: number; title: string; path?: string | null; reason: string }[];
  /** Atlas 2.x shapes the import repairs (a link in a link, a list item without its paragraph); the reason names what was left out. */
  repaired?: number;
  repairedDocuments?: { pageId: string; version: number; title: string; path?: string | null; reason: string }[];
}
withDefaults(defineProps<{ importOnly?: boolean }>(), { importOnly: false });
const emit = defineEmits<{ imported: [spaceId: string] }>();

const mode = ref<ImportMode>("add");
const archive = ref<File | null>(null);
const preview = ref<ImportPreview>();
const busy = ref(false);
const error = ref("");
const notice = ref("");
const confirming = ref(false);

const modeOptions = (): ChoiceOption[] => [
  { value: "add", label: t.importSection.add.label, description: t.importSection.add.description, icon: "copy", badge: t.importSection.add.badge },
  { value: "restore", label: t.importSection.restore.label, description: t.importSection.restore.description, icon: "history", tone: "warning" },
];
function clearPreview() {
  preview.value = undefined;
  error.value = "";
}
watch([mode, archive], () => {
  clearPreview();
  if (archive.value) notice.value = "";
});

const facts = computed<SummaryItem[]>(() =>
  preview.value
    ? [
        { key: "pages", icon: "file-text", label: t.importSection.pages, value: String(preview.value.pages) },
        { key: "attachments", icon: "paperclip", label: t.importSection.attachments, value: String(preview.value.attachments) },
        ...(mode.value === "add" ? [{ key: "suffix", icon: "pencil", label: t.importSection.suffix, value: preview.value.suffix || "—" }] : []),
      ]
    : [],
);
function body(acknowledge?: boolean) {
  const form = new FormData();
  form.append("file", archive.value!);
  form.append("mode", mode.value);
  if (acknowledge !== undefined) form.append("acknowledge", String(acknowledge));
  return form;
}
async function check() {
  if (!archive.value || busy.value) return;
  busy.value = true;
  error.value = "";
  notice.value = "";
  try {
    preview.value = await api<ImportPreview>("/admin/backups/import/preview", { method: "POST", body: body() });
  } catch (failure) {
    error.value = errorMessage(failure);
  } finally {
    busy.value = false;
  }
}
async function perform() {
  if (!preview.value || busy.value) return;
  busy.value = true;
  error.value = "";
  try {
    const result = await api<{ pages: number; attachments: number; message: string }>("/admin/backups/import", { method: "POST", body: body(true) });
    confirming.value = false;
    notice.value = result.message;
    archive.value = null;
    preview.value = undefined;
    emit("imported", "");
  } catch (failure) {
    error.value = errorMessage(failure);
  } finally {
    busy.value = false;
  }
}
function start() {
  if (mode.value === "restore") confirming.value = true;
  else void perform();
}
</script>

<template>
  <SettingsSection :title="t.importSection.title" icon="upload" :description="t.importSection.description" data-backup-import>
    <p v-if="importOnly" class="st-note"><AtlasIcon name="info" :size="15" />{{ t.importSection.importOnly }}</p>
    <ResultBanner v-if="notice" :message="notice" @dismiss="notice = ''" />
    <ChoiceCards :model-value="mode" :options="modeOptions()" :label="t.importSection.modeLabel" :columns="2" :disabled="busy" data-backup-import-mode @update:model-value="mode = $event === 'restore' ? 'restore' : 'add'">
      <template #reveal>
        <div class="backup-import-disclosure">
          <p v-if="mode === 'add'">
            {{ t.importSection.addNote.before }}<strong>{{ preview?.suffix || t.importSection.suffixShownAfterCheck }}</strong>{{ t.importSection.addNote.after }}
          </p>
          <p v-else>{{ t.importSection.restoreNote }}</p>
          <p>{{ t.importSection.contentOnly }}</p>
        </div>
      </template>
    </ChoiceCards>
    <SettingsRow :label="t.importSection.zipRow" :label-for="false" stacked>
      <ZipDropzone v-model="archive" :label="t.importSection.dropzone" :disabled="busy" />
    </SettingsRow>
    <div class="st-actions">
      <Button variant="outline" type="button" :disabled="!archive || busy" data-backup-import-check @click="check">
        <AtlasIcon :name="busy && !preview ? 'loader-circle' : 'search'" :size="16" :class="{ 'atlas-spinner': busy && !preview }" />{{ t.importSection.check }}
      </Button>
    </div>
    <Transition :css="false" v-bind="disclosureHooks">
      <p v-if="error && !confirming" class="backup-import-error" role="alert" data-backup-import-error><AtlasIcon name="triangle-alert" :size="15" />{{ error }}</p>
    </Transition>
    <InlineReveal :open="!!preview" class="reveal-tail">
      <div v-if="preview" class="backup-import-preview" data-backup-import-preview>
        <p class="st-note" role="status"><AtlasIcon name="circle-check" :size="15" />{{ t.importSection.previewNote(preview.message, preview.pages, preview.attachments) }}</p>
        <SummaryList :items="facts" :label="t.importSection.summaryLabel" />
        <div v-if="preview.invalid" class="backup-import-invalid" data-backup-import-invalid>
          <p class="backup-import-error" role="alert"><AtlasIcon name="triangle-alert" :size="15" />{{ t.importSection.invalid(preview.invalid) }}</p>
          <ul>
            <li v-for="item in preview.invalidDocuments" :key="`${item.pageId}:${item.version}`">
              <strong>{{ item.title }}</strong> {{ t.importSection.versionWithId(item.version, item.pageId) }} <code v-if="item.path">{{ item.path }}</code> {{ item.reason }}
            </li>
            <li v-if="(preview.invalidDocuments?.length ?? 0) < preview.invalid">{{ t.importSection.andMore(preview.invalid - (preview.invalidDocuments?.length ?? 0)) }}</li>
          </ul>
        </div>
        <div v-if="preview.repaired" class="backup-import-invalid" data-backup-import-repaired>
          <p class="st-note"><AtlasIcon name="info" :size="15" />{{ t.importSection.repaired(preview.repaired) }}</p>
          <ul>
            <li v-for="item in preview.repairedDocuments" :key="`${item.pageId}:${item.version}:${item.path}`">
              <strong>{{ item.title }}</strong> {{ t.importSection.version(item.version) }} <code v-if="item.path">{{ item.path }}</code> {{ item.reason }}
            </li>
            <li v-if="(preview.repairedDocuments?.length ?? 0) < preview.repaired">{{ t.importSection.andMore(preview.repaired - (preview.repairedDocuments?.length ?? 0)) }}</li>
          </ul>
        </div>
        <div class="st-actions">
          <Button :variant="mode === 'restore' ? 'destructive' : 'default'" type="button" :disabled="busy || !!preview.invalid" data-backup-import-run @click="start">
            <AtlasIcon :name="mode === 'restore' ? 'history' : 'copy'" :size="16" />{{ mode === "restore" ? t.importSection.runRestore : t.importSection.runAdd }}
          </Button>
        </div>
      </div>
    </InlineReveal>

    <ConfirmDialog :open="confirming" :title="t.importSection.confirmTitle" :confirm-label="t.importSection.runRestore" confirm-icon="history" :busy="busy" :error="confirming ? error : undefined" @confirm="perform" @cancel="confirming = false">
      <p>{{ t.importSection.confirmText(preview?.pages ?? 0) }}</p>
      <p>{{ t.importSection.confirmKept }}</p>
    </ConfirmDialog>
  </SettingsSection>
</template>

<style scoped>
.backup-import-disclosure { display: grid; gap: 6px; padding: 10px 14px; border-radius: var(--st-radius-sm); background: var(--hover); font-size: 13px; line-height: 1.55; }
.backup-import-disclosure p { margin: 0; }
.backup-import-preview { display: grid; gap: 10px; padding-top: 4px; }
.backup-import-invalid { display: grid; gap: 8px; }
.backup-import-invalid ul { display: grid; gap: 4px; max-height: 220px; overflow: auto; margin: 0; padding-left: 18px; font-size: 13px; line-height: 1.5; overflow-wrap: anywhere; }
.backup-import-error { display: flex; align-items: center; gap: 6px; margin: 0; color: var(--error); font-size: 13px; font-weight: 550; }
</style>
