<script setup lang="ts">
import { onBeforeUnmount, ref } from "vue";
import { accessApi } from "../../../accessApi";
import { errorMessage } from "../../../api";
import AccessChangeDialogs from "./AccessChangeDialogs.vue";
import PrincipalGrantFlow, { type PrincipalGrantDraft } from "./PrincipalGrantFlow.vue";
import { grantNeedsReauth, targetName, useAccessChanges } from "./accessChanges";
import type { AccessResource, PrincipalAccess } from "./accessModel";
import { directSource } from "./accessView";

/**
 * Přidat přístup from a person's or group's card, wired to the API: the "Který" step searches GET
 * /api/access/resources?kind=&q= (at most 20 the caller may grant), the grant goes out through PUT
 * /api/access/resources/{kind}/{id}/grants for this principal. A resource the principal already holds directly is changed
 * with its version. `done` carries the result sentence for the card's flash.
 */
const props = withDefaults(defineProps<{ access: PrincipalAccess; api?: Pick<typeof accessApi, "resources" | "grant" | "revoke"> }>(), { api: () => accessApi });
const emit = defineEmits<{ done: [message: string]; cancel: [] }>();

const targets = ref<AccessResource[]>([]);
const searching = ref(false);
let version = 0;
let timer: ReturnType<typeof setTimeout> | undefined;
onBeforeUnmount(() => clearTimeout(timer));

function search(kind: string, query: string) {
  clearTimeout(timer);
  const current = ++version;
  searching.value = true;
  timer = setTimeout(async () => {
    try {
      const found = await props.api.resources(kind, query);
      if (current === version) targets.value = found;
    } catch (failure) {
      if (current === version) changes.error.value = errorMessage(failure);
    } finally {
      if (current === version) searching.value = false;
    }
  }, query ? 250 : 0);
}

const changes = useAccessChanges({ api: props.api, reload: async () => undefined, done: (message) => emit("done", message) });
function save(draft: PrincipalGrantDraft, proof: string | null) {
  const principal = props.access.principal;
  const existing = props.access.entries.find((entry) => entry.kind === draft.target.kind && entry.resourceId === draft.target.id);
  const grant = existing ? directSource(existing.sources) : undefined;
  const iso = (value: string) => (value ? new Date(value).toISOString() : null);
  void changes.grant(
    {
      target: { kind: draft.target.kind, id: draft.target.id, name: targetName(draft.target.kind, draft.target.title) },
      subject: { kind: principal.kind, id: principal.id, name: principal.name },
      level: draft.level,
      previous: grant?.level ?? null,
      startAt: iso(draft.startAt),
      expiresAt: iso(draft.expiresAt),
      expectedVersion: grant?.version ?? null,
    },
    { proof },
  );
}
</script>

<template>
  <div class="principal-grant" data-principal-grant>
    <PrincipalGrantFlow
      :subject="access.principal.name"
      :subject-kind="access.principal.kind"
      :targets="targets"
      :searching="searching"
      :busy="changes.busy.value"
      :requires-reauth="grantNeedsReauth"
      :save-error="changes.error.value || changes.conflict.value || undefined"
      @search="search"
      @save="save"
      @cancel="emit('cancel')"
    />
    <AccessChangeDialogs :changes="changes" />
  </div>
</template>

<style scoped>
.principal-grant { min-width: 0; }
</style>
