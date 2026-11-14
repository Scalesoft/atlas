<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from "vue";
import { accessApi } from "../../../accessApi";
import { errorMessage } from "../../../api";
import GrantFlow from "../GrantFlow.vue";
import type { GrantDraft, GrantSubject } from "../types";
import { levelOptions } from "./accessKinds";
import { grantNeedsReauth, reauthTargetOf, targetName, useAccessChanges } from "./accessChanges";
import AccessChangeDialogs from "./AccessChangeDialogs.vue";
import type { AccessLevel, ResourceAccess } from "./accessModel";
import { directSource } from "./accessView";
import { useCandidateSearch } from "./useCandidateSearch";

/**
 * Přidat přístup from a resource (a space, page, vault, secret or a plugin kind): GrantFlow with the kind's staircase.
 * People and groups come from the server as they are typed (GET /api/access/candidates, never the whole directory);
 * the grant goes out through PUT /api/access/resources/{kind}/{id}/grants, with the fresh sign-in the kind asks for.
 * An existing direct grant of the chosen subject is changed with its version instead of failing as a duplicate.
 */
const props = withDefaults(defineProps<{ kind: string; id: string; name?: string; api?: Pick<typeof accessApi, "resource" | "candidates" | "grant" | "revoke"> }>(), {
  name: undefined,
  api: () => accessApi,
});
const emit = defineEmits<{ done: [message: string, key: string]; cancel: [] }>();

const current = ref<ResourceAccess>();
const loadError = ref("");
let disposed = false;

async function load() {
  try {
    current.value = await props.api.resource(props.kind, props.id, "direct");
  } catch (failure) {
    if (!disposed) loadError.value = errorMessage(failure);
  }
}
onMounted(load);
onBeforeUnmount(() => {
  disposed = true;
});

const target = computed(() => props.name ?? (current.value ? targetName(props.kind, current.value.resource.title) : ""));
const levels = computed(() => levelOptions(props.kind));
let lastKey = "";
const changes = useAccessChanges({
  api: props.api,
  reload: load,
  done: (message) => emit("done", message, lastKey),
});

const { results: subjects, searching, find: search } = useCandidateSearch<GrantSubject>({ delayMs: 250, failed: (message) => { changes.error.value = message; } });
const find = (query: string, kind: "user" | "group") => search(query, async (term) =>
  (await props.api.candidates(kind, term)).map((item) => ({ id: item.id, kind: item.kind, name: item.name, detail: item.detail ?? undefined })));

function save(draft: GrantDraft, proof: string | null) {
  const subject = subjects.value.find((item) => item.id === draft.subjectId && item.kind === draft.kind);
  const existing = current.value?.entries.find((entry) => entry.subject.kind === draft.kind && entry.subject.id === draft.subjectId);
  const grant = existing ? directSource(existing.sources) : undefined;
  const iso = (value: string) => (value ? new Date(value).toISOString() : null);
  lastKey = `${draft.kind}:${draft.subjectId}`;
  void changes.grant(
    {
      target: { kind: props.kind, id: props.id, name: target.value },
      subject: { kind: draft.kind, id: draft.subjectId, name: subject?.name ?? existing?.subject.name ?? "" },
      level: draft.role as AccessLevel,
      previous: grant?.level ?? null,
      startAt: iso(draft.startAt),
      expiresAt: iso(draft.expiresAt),
      expectedVersion: grant?.version ?? null,
    },
    { proof },
  );
}
const requiresReauth = (draft: GrantDraft) => grantNeedsReauth(props.kind, draft.role as AccessLevel);
</script>

<template>
  <div class="resource-grant-flow" data-resource-grant-flow>
    <GrantFlow
      :subjects="subjects"
      :levels="levels"
      :target="target"
      :busy="changes.busy.value"
      :requires-reauth="requiresReauth"
      :reauth-target="reauthTargetOf({ kind, id })"
      :save-error="changes.error.value || changes.conflict.value || loadError || undefined"
      remote
      :searching="searching"
      @search="find"
      @save="save"
      @cancel="emit('cancel')"
    />
    <AccessChangeDialogs :changes="changes" />
  </div>
</template>

<style scoped>
.resource-grant-flow { min-width: 0; }
</style>
