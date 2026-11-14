<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from "vue";
import { accessApi } from "../../../accessApi";
import { errorMessage } from "../../../api";
import t from "../../../i18n/settings-access";
import ErrorState from "../ErrorState.vue";
import LoadingSkeleton from "../../LoadingSkeleton.vue";
import PrincipalAccessView from "./PrincipalAccessView.vue";
import { useAccessKinds } from "./accessKinds";
import type { PrincipalAccess } from "./accessModel";
import type { AccessRow } from "./accessView";

/**
 * Profil › Moje přístupy: everything the signed-in person can reach and where it comes from (GET /api/access/me), grouped
 * by space, read-only. Nothing is changed here; a missing access is asked for where it lives (a vault, a page).
 */
const props = withDefaults(defineProps<{ api?: Pick<typeof accessApi, "me"> }>(), { api: () => accessApi });
// The enabled kinds (plugins included) name and draw their rows; the core kinds stand in until they answer.
useAccessKinds();

const access = ref<PrincipalAccess>();
const loading = ref(true);
const error = ref("");
let disposed = false;

async function load() {
  loading.value = true;
  try {
    const next = await props.api.me();
    if (disposed) return;
    access.value = next;
    error.value = "";
  } catch (failure) {
    if (!disposed) error.value = errorMessage(failure);
  } finally {
    if (!disposed) loading.value = false;
  }
}
onMounted(load);
onBeforeUnmount(() => (disposed = true));

/** A space or a page opens in the wiki, a personal token in Osobní API; vaults and secrets open from the pages that use them. */
function rowHref(row: AccessRow) {
  if (row.kind === "space") return `#/spaces/${encodeURIComponent(row.resourceId)}`;
  if (row.kind === "page") return `#/pages/${encodeURIComponent(row.resourceId)}`;
  if (row.kind === "token") return "#/profile/api";
  return undefined;
}
</script>

<template>
  <div class="my-access" :aria-busy="loading || undefined" data-my-access>
    <ErrorState v-if="error" :title="t.loadFailed" :text="error" :busy="loading" @retry="load()" />
    <LoadingSkeleton v-if="loading && !access && !error" :label="t.loading" />
    <PrincipalAccessView v-if="access" :access="access" readonly :row-href="rowHref" :empty-text="t.mine.empty" />
  </div>
</template>

<style scoped>
.my-access { display: grid; gap: 14px; min-width: 0; }
</style>
