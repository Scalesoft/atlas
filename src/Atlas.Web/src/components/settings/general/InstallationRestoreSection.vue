<script setup lang="ts">
import { computed, ref, watch } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import { Input } from "../../ui/input";
import { api, errorMessage, post } from "../../../api";
import { formatNumber } from "../../../i18n";
import t from "../../../i18n/settings-backups";
import ConfirmDialog from "../ConfirmDialog.vue";
import InlineReveal from "../InlineReveal.vue";
import ResultBanner from "../ResultBanner.vue";
import SettingsRow from "../SettingsRow.vue";
import SettingsSection from "../SettingsSection.vue";
import SummaryList from "../SummaryList.vue";
import type { SummaryItem } from "../types";
import { disclosureHooks } from "../disclosure";
import { archiveEndpoint } from "./backups";
import { gitBackupEndpoint, restoreConfirmWord, type GitRestorePreview, type GitRestoreResult } from "./gitBackup";
import ZipDropzone from "./ZipDropzone.vue";

/**
 * Obnova celé instalace (format v7) from one of its two transports: `git`, the configured repository the server
 * clones, or `zip`, a complete archive uploaded here (GAP2-1). The server checks the backup first (`restore/preview`:
 * mode, row counts, problems, an installation that is not empty) and only then restores in one transaction after a
 * safety archive. The flow is check, review, a destructive confirmation with a typed word, result. Without the
 * backup key only the content comes back; with it, accounts, rights and settings too. An archive of formats 1–6
 * goes to BackupImportSection instead.
 */
const props = withDefaults(defineProps<{ source: "git" | "zip"; configured?: boolean }>(), { configured: false });
const key = ref("");
const archive = ref<File | null>(null);
const preview = ref<GitRestorePreview>();
const result = ref<GitRestoreResult>();
const busy = ref(false);
const error = ref("");
const confirming = ref(false);
const typed = ref("");

const total = computed(() => Object.values(preview.value?.rows ?? {}).reduce((sum, rows) => sum + rows, 0));
const facts = computed<SummaryItem[]>(() =>
  preview.value
    ? [
        { key: "mode", icon: "key-round", label: t.restore.scopeLabel, value: preview.value.mode === "full" ? t.restore.scopeFull : t.restore.scopeContent },
        { key: "rows", icon: "database", label: t.restore.rows, value: formatNumber(total.value) },
      ]
    : [],
);
const ready = computed(() => (props.source === "git" ? props.configured : !!archive.value));
const copy = computed(() => (props.source === "git" ? t.restore.git : t.restore.zip));
/** The repository goes as JSON, the archive as the same upload the import of archives sends. */
function send<T>(path: string, acknowledge?: boolean) {
  if (props.source === "git") return post<T>(`${gitBackupEndpoint}/${path}`, { key: key.value.trim() || null, ...(acknowledge === undefined ? {} : { acknowledge }) });
  const form = new FormData();
  form.append("file", archive.value!);
  if (key.value.trim()) form.append("key", key.value.trim());
  if (acknowledge !== undefined) form.append("acknowledge", String(acknowledge));
  return api<T>(`${archiveEndpoint}/${path}`, { method: "POST", body: form });
}

// A check belongs to the backup and the key it was run with.
watch([key, archive], () => {
  preview.value = undefined;
  error.value = "";
});

/** The restored installation has its own accounts and sessions: reloading lands on the sign-in. */
function reload() { location.reload(); }

async function check() {
  if (busy.value || !ready.value) return;
  busy.value = true;
  error.value = "";
  result.value = undefined;
  try {
    preview.value = await send<GitRestorePreview>("restore/preview");
  } catch (failure) {
    preview.value = undefined;
    error.value = errorMessage(failure);
  } finally {
    busy.value = false;
  }
}
function start() {
  if (!preview.value || preview.value.problems.length) return;
  typed.value = "";
  error.value = "";
  confirming.value = true;
}
async function perform() {
  if (busy.value || typed.value.trim() !== restoreConfirmWord()) return;
  busy.value = true;
  error.value = "";
  try {
    result.value = await send<GitRestoreResult>("restore", true);
    confirming.value = false;
    preview.value = undefined;
    key.value = "";
    archive.value = null;
  } catch (failure) {
    error.value = errorMessage(failure);
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <SettingsSection :title="copy.title" :icon="source === 'git' ? 'git-branch' : 'folder'" :description="copy.description" :data-restore="source">
    <ResultBanner v-if="result" :message="result.message" @dismiss="result = undefined" />
    <div v-if="result" class="st-actions" data-restore-next>
      <Button type="button" data-restore-reload @click="reload"><AtlasIcon name="refresh" :size="16" />{{ t.restore.reloadApp }}</Button>
    </div>
    <p v-if="result?.warnings.length" class="st-note" data-restore-warnings><AtlasIcon name="triangle-alert" :size="15" />{{ result.warnings.join(" ") }}</p>
    <p v-if="source === 'git' && !configured" class="st-note" data-restore-unconfigured><AtlasIcon name="info" :size="15" />{{ t.restore.unconfigured }}</p>
    <!-- Without a repository there is nothing to check, so the key and the check stay out of sight until it is set up. -->
    <template v-if="source === 'zip' || configured">
      <SettingsRow v-if="source === 'zip'" :label="t.restore.zipRow" :label-for="false" stacked>
        <ZipDropzone v-model="archive" :label="t.restore.dropzone" :disabled="busy" />
      </SettingsRow>
      <SettingsRow v-slot="{ id, describedBy }" :label="t.restore.key" :help="t.restore.keyHelp">
        <Input :id="id" v-model="key" class="st-mono" type="password" autocomplete="off" spellcheck="false" :aria-describedby="describedBy" :disabled="busy" data-restore-key @keydown.enter.prevent="check" />
      </SettingsRow>
      <div class="st-actions">
        <Button variant="outline" type="button" :disabled="busy || !ready" data-restore-check @click="check">
          <AtlasIcon :name="busy && !confirming ? 'loader-circle' : 'search'" :size="16" :class="{ 'atlas-spinner': busy && !confirming }" />{{ copy.check }}
        </Button>
      </div>
    </template>
    <Transition :css="false" v-bind="disclosureHooks">
      <p v-if="error && !confirming" class="restore-error" role="alert" data-restore-error><AtlasIcon name="triangle-alert" :size="15" />{{ error }}</p>
    </Transition>
    <InlineReveal :open="!!preview" class="reveal-tail">
      <div v-if="preview" class="restore-preview" data-restore-preview>
        <p class="st-note" role="status"><AtlasIcon :name="preview.problems.length ? 'triangle-alert' : 'circle-check'" :size="15" />{{ preview.message }}</p>
        <SummaryList :items="facts" :label="copy.summary" />
        <ul v-if="preview.problems.length" class="restore-list is-problem" data-restore-problems>
          <li v-for="item in preview.problems" :key="item">{{ item }}</li>
        </ul>
        <ul v-if="preview.warnings.length" class="restore-list" data-restore-preview-warnings>
          <li v-for="item in preview.warnings" :key="item">{{ item }}</li>
        </ul>
        <div class="st-actions">
          <Button variant="destructive" type="button" :disabled="busy || preview.problems.length > 0" data-restore-run @click="start">
            <AtlasIcon name="history" :size="16" />{{ t.restore.run }}
          </Button>
        </div>
      </div>
    </InlineReveal>

    <ConfirmDialog :open="confirming" :title="copy.confirm" :confirm-label="t.restore.run" confirm-icon="history" :busy="busy" :error="confirming ? error : undefined" :confirm-disabled="typed.trim() !== restoreConfirmWord()" @confirm="perform" @cancel="confirming = false">
      <p>{{ preview?.message }}</p>
      <p>{{ t.restore.confirmText(formatNumber(total)) }}</p>
      <SettingsRow v-slot="{ id }" :label="t.restore.typeToConfirm(restoreConfirmWord())" stacked>
        <Input :id="id" v-model="typed" autocomplete="off" :placeholder="restoreConfirmWord()" :disabled="busy" data-restore-confirm-word @keydown.enter.prevent="perform" />
      </SettingsRow>
    </ConfirmDialog>
  </SettingsSection>
</template>

<style scoped>
.restore-preview { display: grid; gap: 10px; padding-top: 4px; }
.restore-list { display: grid; gap: 4px; margin: 0; padding-left: 18px; font-size: 13px; line-height: 1.5; overflow-wrap: anywhere; }
.restore-list.is-problem { color: var(--error); }
.restore-error { display: flex; align-items: center; gap: 6px; margin: 0; color: var(--error); font-size: 13px; font-weight: 550; }
</style>
