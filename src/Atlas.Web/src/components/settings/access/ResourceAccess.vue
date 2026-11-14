<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { disclosureHooks } from "../disclosure";
import { accessApi } from "../../../accessApi";
import { errorMessage } from "../../../api";
import ConflictNote from "../ConflictNote.vue";
import ErrorState from "../ErrorState.vue";
import LoadingSkeleton from "../../LoadingSkeleton.vue";
import ResultBanner from "../ResultBanner.vue";
import SettingsSection from "../SettingsSection.vue";
import { useOptionalSettingsFlash } from "../flash";
import t from "../../../i18n/settings-access";
import AccessChangeDialogs from "./AccessChangeDialogs.vue";
import ResourceAccessPanel from "./ResourceAccessPanel.vue";
import ResourceGrantFlow from "./ResourceGrantFlow.vue";
import { targetName, useAccessChanges } from "./accessChanges";
import type { AccessChangeDraft, AccessSource, AccessSubject, ResourceAccess, ResourceAccessView } from "./accessModel";
import { searchesOnServer, type AccessRow } from "./accessView";

/**
 * Kdo má přístup of one resource, wired to the API: GET /api/access/resources/{kind}/{id}?view= into ResourceAccessPanel,
 * the inline change (level and validity, explicit save), a confirmed revoke and Přidat přístup. The flow is a link to
 * its own route when the surface has one (`addHref`), otherwise it opens in place of the list (a vault's or secret's
 * access). Every change goes through useAccessChanges (fresh sign-in where the kind needs it, conflicts reload).
 */
const props = withDefaults(
  defineProps<{
    kind: string;
    id: string;
    title?: string;
    addHref?: string;
    subjectHref?: (subject: AccessSubject) => string | undefined;
    groupHref?: (groupId: string) => string | undefined;
    note?: string;
    /** The rows only (ResourceAccessPanel compact), under a form of the host that adds: ShareItemDialog's ResourceShareForm. */
    compact?: boolean;
    api?: Pick<typeof accessApi, "resource" | "candidates" | "grant" | "revoke">;
  }>(),
  { title: undefined, addHref: undefined, subjectHref: undefined, groupHref: undefined, note: undefined, compact: false, api: () => accessApi },
);
const emit = defineEmits<{ changed: []; loaded: [access: ResourceAccess] }>();
const flash = useOptionalSettingsFlash();

const access = ref<ResourceAccess>();
const view = ref<ResourceAccessView>("effective");
const loading = ref(false);
const error = ref("");
const editing = ref<string | null>(null);
const adding = ref(false);
const notice = ref<{ key: number; text: string; icon: string }>();
const query = ref("");
// The server cut the whole list at 200 entries: the typed search then goes to the server too (q), so anyone can be found.
const truncated = ref(false);
let request = 0;
let disposed = false;
let searchTimer: ReturnType<typeof setTimeout> | undefined;

async function load() {
  const current = ++request;
  const q = truncated.value ? query.value.trim() : "";
  loading.value = true;
  try {
    const next = await (q ? props.api.resource(props.kind, props.id, view.value, q) : props.api.resource(props.kind, props.id, view.value));
    if (disposed || current !== request) return;
    if (!q) truncated.value = next.hasMore && searchesOnServer(props.kind);
    access.value = next;
    error.value = "";
    emit("loaded", next);
  } catch (failure) {
    if (!disposed && current === request) error.value = errorMessage(failure);
  } finally {
    if (!disposed && current === request) loading.value = false;
  }
}
watch(() => [props.kind, props.id, view.value], () => void load(), { immediate: true });
// Sent when the typing pauses; an older answer is dropped by `request`, and meanwhile the panel narrows the loaded rows.
watch(query, () => {
  clearTimeout(searchTimer);
  if (truncated.value) searchTimer = setTimeout(() => void load(), 250);
});
onBeforeUnmount(() => {
  disposed = true;
  clearTimeout(searchTimer);
});

let noticeKey = 0;
function announce(text: string, icon: string) {
  if (flash) flash.notify(text, { icon });
  else notice.value = { key: ++noticeKey, text, icon };
}
const changes = useAccessChanges({
  api: props.api,
  reload: load,
  done: (text, icon) => {
    editing.value = null;
    announce(text, icon);
    emit("changed");
  },
});
const name = computed(() => (access.value ? targetName(props.kind, access.value.resource.title) : ""));
const target = computed(() => ({ kind: props.kind, id: props.id, name: name.value }));

function toggle(row: AccessRow) {
  editing.value = editing.value === row.key ? null : row.key;
}
function save(row: AccessRow, grant: AccessSource, draft: AccessChangeDraft) {
  if (!row.subject) return;
  void changes.grant({
    target: target.value,
    subject: { kind: row.subject.kind, id: row.subject.id, name: row.subject.name },
    level: draft.level,
    previous: grant.level,
    startAt: draft.startAt,
    expiresAt: draft.expiresAt,
    expectedVersion: grant.version ?? null,
  });
}
function revoke(row: AccessRow, grant: AccessSource) {
  if (!row.subject) return;
  changes.revoke({ target: target.value, subject: { kind: row.subject.kind, id: row.subject.id, name: row.subject.name }, level: grant.level, version: grant.version });
}
function added(message: string) {
  adding.value = false;
  announce(message, "circle-check");
  void load();
  emit("changed");
}
defineExpose({ reload: load });
</script>

<template>
  <div class="resource-access-host" :aria-busy="loading || changes.busy.value || undefined">
    <ResultBanner v-if="notice" :key="notice.key" :message="notice.text" :icon="notice.icon" @dismiss="notice = undefined" />
    <ConflictNote v-if="changes.conflict.value" :text="changes.conflict.value" data-access-conflict @dismiss="changes.conflict.value = ''" />
    <Transition :css="false" v-bind="disclosureHooks">
      <p v-if="changes.error.value && !changes.pending.value" class="st-note resource-access-error" role="alert"><AtlasIcon name="triangle-alert" :size="15" />{{ changes.error.value }}</p>
    </Transition>
    <ErrorState v-if="error" :title="t.loadFailed" :text="error" :busy="loading" @retry="load()" />
    <LoadingSkeleton v-if="loading && !access && !error" :label="t.loading" />
    <SettingsSection v-if="adding && access" :label="t.panel.addFor(name)">
      <ResourceGrantFlow :kind="kind" :id="id" :name="name" :api="api" @done="added" @cancel="adding = false" />
    </SettingsSection>
    <ResourceAccessPanel
      v-else-if="access"
      v-model:view="view"
      v-model:query="query"
      :access="access"
      :title="title"
      :editing="editing"
      :busy="changes.busy.value"
      :add-href="addHref"
      :subject-href="subjectHref"
      :group-href="groupHref"
      :note="note"
      :compact="compact"
      @add="adding = true"
      @change="toggle"
      @cancel="editing = null"
      @save="save"
      @revoke="revoke"
    >
      <template v-if="$slots.before" #before><slot name="before" :access="access" /></template>
      <template v-if="$slots.after" #after><slot name="after" :access="access" /></template>
    </ResourceAccessPanel>
    <AccessChangeDialogs :changes="changes" />
  </div>
</template>

<style scoped>
.resource-access-host { display: grid; gap: 14px; min-width: 0; }
.resource-access-error { color: var(--error); }
</style>
