<script setup lang="ts">
import { ref } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { disclosureHooks } from "../disclosure";
import { accessApi } from "../../../accessApi";
import ConflictNote from "../ConflictNote.vue";
import { useOptionalSettingsFlash } from "../flash";
import AccessChangeDialogs from "./AccessChangeDialogs.vue";
import PrincipalAccessView from "./PrincipalAccessView.vue";
import { targetName, useAccessChanges } from "./accessChanges";
import type { AccessChangeDraft, AccessSource, PrincipalAccess } from "./accessModel";
import type { AccessRow } from "./accessView";

/**
 * A person's or group's card (PrincipalAccessView) with its changes: Změnit opens the row's inline editor, Odebrat asks,
 * and both go to PUT/DELETE /api/access/resources/{kind}/{id}/grants of that row's resource for this principal. The card
 * is loaded by the page (it also needs the principal for its header); `reload` reads it again after a change.
 */
const props = withDefaults(
  defineProps<{
    access: PrincipalAccess;
    reload: () => Promise<unknown>;
    readonly?: boolean;
    scopeNote?: string;
    groupHref?: (groupId: string) => string | undefined;
    rowHref?: (row: AccessRow) => string | undefined;
    emptyText?: string;
    api?: Pick<typeof accessApi, "grant" | "revoke">;
  }>(),
  { readonly: false, scopeNote: undefined, groupHref: undefined, rowHref: undefined, emptyText: undefined, api: () => accessApi },
);
const flash = useOptionalSettingsFlash();
const editing = ref<string | null>(null);

const changes = useAccessChanges({
  api: props.api,
  reload: props.reload,
  done: (text, icon) => {
    editing.value = null;
    flash?.notify(text, { icon });
  },
});
const subject = () => ({ kind: props.access.principal.kind, id: props.access.principal.id, name: props.access.principal.name });
const target = (row: AccessRow) => ({ kind: row.kind, id: row.resourceId, name: targetName(row.kind, row.kind === "space" ? props.access.entries.find((entry) => entry.kind === "space" && entry.resourceId === row.resourceId)?.spaceName ?? row.title : row.title) });

function toggle(row: AccessRow) {
  editing.value = editing.value === row.key ? null : row.key;
}
function save(row: AccessRow, grant: AccessSource, draft: AccessChangeDraft) {
  void changes.grant({ target: target(row), subject: subject(), level: draft.level, previous: grant.level, startAt: draft.startAt, expiresAt: draft.expiresAt, expectedVersion: grant.version ?? null });
}
function revoke(row: AccessRow, grant: AccessSource) {
  changes.revoke({ target: target(row), subject: subject(), level: grant.level, version: grant.version });
}
</script>

<template>
  <div class="principal-access-editor" :aria-busy="changes.busy.value || undefined">
    <ConflictNote v-if="changes.conflict.value" :text="changes.conflict.value" data-access-conflict @dismiss="changes.conflict.value = ''" />
    <Transition :css="false" v-bind="disclosureHooks">
      <p v-if="changes.error.value && !changes.pending.value" class="st-note principal-access-error" role="alert"><AtlasIcon name="triangle-alert" :size="15" />{{ changes.error.value }}</p>
    </Transition>
    <PrincipalAccessView
      :access="access"
      :readonly="readonly"
      :scope-note="scopeNote"
      :editing="editing"
      :busy="changes.busy.value"
      :group-href="groupHref"
      :row-href="rowHref"
      :empty-text="emptyText"
      @change="toggle"
      @cancel="editing = null"
      @save="save"
      @revoke="revoke"
    />
    <AccessChangeDialogs :changes="changes" />
  </div>
</template>

<style scoped>
.principal-access-editor { display: grid; gap: 14px; min-width: 0; }
.principal-access-error { color: var(--error); }
</style>
