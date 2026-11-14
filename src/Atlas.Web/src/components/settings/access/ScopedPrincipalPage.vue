<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from "vue";
import t from "../../../i18n/settings-access";
import { accessApi } from "../../../accessApi";
import { errorMessage } from "../../../api";
import { spaceSettingsHash } from "../../../settingsRoutes";
import { pageAccessRoute } from "../../sharing/pageAccessRoute";
import ErrorState from "../ErrorState.vue";
import LoadingSkeleton from "../../LoadingSkeleton.vue";
import SettingsPage from "../SettingsPage.vue";
import PrincipalAccessEditor from "./PrincipalAccessEditor.vue";
import type { PrincipalAccess } from "./accessModel";
import type { AccessRow } from "./accessView";

/**
 * A person's or group's card limited to one space, for its administrator (Nastavení prostoru › Přístupy → a person):
 * GET /api/access/principals/{kind}/{id}?spaceId= returns only that space's entries. Changes work as on the full card.
 */
const props = defineProps<{ kind: "user" | "group"; id: string; spaceId: string; spaceName: string; backHref: string; groupHref?: (groupId: string) => string | undefined }>();
const access = ref<PrincipalAccess>();
const loading = ref(false);
const error = ref("");
let request = 0;
let disposed = false;
async function load() {
  const current = ++request;
  loading.value = true;
  try {
    const next = await accessApi.principal(props.kind, props.id, props.spaceId);
    if (disposed || current !== request) return;
    access.value = next;
    error.value = "";
  } catch (failure) {
    if (!disposed && current === request) error.value = errorMessage(failure);
  } finally {
    if (!disposed && current === request) loading.value = false;
  }
}
watch(() => [props.kind, props.id, props.spaceId], () => void load(), { immediate: true });
onBeforeUnmount(() => (disposed = true));

function rowHref(row: AccessRow) {
  if (row.kind === "space") return props.backHref;
  if (row.kind === "vault") return spaceSettingsHash(props.spaceId, `vaults/${row.resourceId}/access`);
  if (row.kind === "page") return pageAccessRoute(row.resourceId);
  return undefined;
}
</script>

<template>
  <SettingsPage
    :title="access?.principal.name ?? (kind === 'group' ? t.scoped.group : t.scoped.person)"
    :icon="kind === 'group' ? 'users' : 'account'"
    :description="[access?.principal.detail, t.scoped.description(spaceName)].filter(Boolean).join(' · ')"
    :back="{ href: backHref, label: t.scoped.back }"
    data-scoped-principal
  >
    <ErrorState v-if="error" :title="t.loadFailed" :text="error" :busy="loading" @retry="load()" />
    <LoadingSkeleton v-if="loading && !access" :label="t.loading" />
    <PrincipalAccessEditor
      v-if="access"
      :access="access"
      :reload="load"
      :row-href="rowHref"
      :group-href="groupHref"
      :scope-note="t.scoped.scopeNote(spaceName)"
      :empty-text="t.scoped.empty(spaceName)"
    />
  </SettingsPage>
</template>
