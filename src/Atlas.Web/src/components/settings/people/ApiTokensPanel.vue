<script setup lang="ts">
import t from "../../../i18n/settings-people";
import { computed, onBeforeUnmount, onMounted, ref } from "vue";
import { apiTokenAdminApi } from "../../../accessApi";
import { errorMessage } from "../../../api";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import ConfirmDialog from "../ConfirmDialog.vue";
import SettingsSection from "../SettingsSection.vue";
import ErrorState from "../ErrorState.vue";
import LoadingNote from "../LoadingNote.vue";
import SearchField from "../SearchField.vue";
import type { AdminApiToken } from "../access/accessModel";
import { matchesQuery } from "../access/accessView";
import { useOptionalSettingsFlash } from "../flash";
import { useOptionalSettingsRoute } from "../useSettingsRoute";
import ApiTokenList from "./ApiTokenList.vue";

/**
 * Personal API tokens for the wiki administrator: every token of the wiki (API tokeny) or one person's (the Tokeny tab of
 * the card, `userId`). Revoking someone's token asks in a ConfirmDialog and is audited like the owner's own revoke.
 */
const props = withDefaults(defineProps<{ userId?: string; api?: typeof apiTokenAdminApi }>(), { userId: undefined, api: () => apiTokenAdminApi });
const emit = defineEmits<{ reviewed: [] }>();
const flash = useOptionalSettingsFlash();
const router = useOptionalSettingsRoute();

const tokens = ref<AdminApiToken[]>([]);
const loading = ref(true);
const loaded = ref(false);
const error = ref("");
const query = ref("");
const revoking = ref<AdminApiToken>();
const busy = ref(false);
const revokeError = ref("");
let disposed = false;

async function load() {
  loading.value = true;
  error.value = "";
  try {
    const list = await props.api.list();
    if (disposed) return;
    tokens.value = props.userId ? list.filter((token) => token.userId === props.userId) : list;
    loaded.value = true;
  } catch (failure) {
    if (!disposed) error.value = errorMessage(failure);
  } finally {
    if (!disposed) loading.value = false;
  }
}
// The upgrade to token rights gave every account that had tokens, and every write token, the right to write (since 3.6: Zápis) so
// nothing stopped working; until a wiki administrator confirms, the wiki says so here and in the Přehled (api_levels_review).
const levelsReview = ref(false);
const reviewing = ref(false);
const reviewError = ref("");
async function loadReview() {
  if (props.userId) return;
  try {
    const summary = await props.api.summary();
    if (!disposed) levelsReview.value = summary.levelsReview === true;
  } catch {
    // The notice is an extra: a server without the summary shows the list as before.
  }
}
async function confirmReview() {
  if (reviewing.value) return;
  reviewing.value = true;
  reviewError.value = "";
  try {
    const summary = await props.api.confirmLevelsReview();
    if (disposed) return;
    levelsReview.value = summary.levelsReview === true;
    emit("reviewed");
    flash?.notify(t.tokens.levelsConfirmed, { icon: "circle-check" });
  } catch (failure) {
    if (!disposed) reviewError.value = errorMessage(failure);
  } finally {
    if (!disposed) reviewing.value = false;
  }
}
onMounted(() => {
  void load();
  void loadReview();
});
onBeforeUnmount(() => (disposed = true));

const shown = computed(() => tokens.value.filter((token) => matchesQuery(`${token.name} ${token.userName}`, query.value)));
const userHref = (id: string) => router?.href(`people/accounts/${id}/tokens`);
async function revoke() {
  const token = revoking.value;
  if (!token || busy.value) return;
  busy.value = true;
  revokeError.value = "";
  try {
    await props.api.revoke(token.id);
    revoking.value = undefined;
    flash?.notify(t.tokens.tokenRevoked(token.name, token.userName), { icon: "ban" });
    await load();
  } catch (failure) {
    revokeError.value = errorMessage(failure);
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <div class="api-tokens-panel" :aria-busy="loading || busy || undefined">
    <ErrorState v-if="error" :title="t.tokens.loadFailed" :text="error" :busy="loading" @retry="load()" />
    <LoadingNote v-if="loading && !loaded" :label="t.tokens.loading" />
    <SettingsSection v-if="levelsReview" :title="t.tokens.reviewTitle" icon="triangle-alert" :description="t.tokens.reviewDescription" data-levels-review>
      <div class="st-actions">
        <Button size="sm" type="button" :disabled="reviewing" data-levels-review-confirm @click="confirmReview"><AtlasIcon name="circle-check" :size="15" />{{ t.tokens.reviewConfirm }}</Button>
      </div>
      <p v-if="reviewError" class="st-note" role="alert"><AtlasIcon name="triangle-alert" :size="15" />{{ reviewError }}</p>
    </SettingsSection>
    <template v-if="loaded">
      <div v-if="!userId && tokens.length > 6" class="api-tokens-filters" role="search" :aria-label="t.tokens.filter">
        <SearchField v-model="query" :label="t.tokens.searchLabel" :placeholder="t.tokens.searchPlaceholder" data-token-search />
      </div>
      <ApiTokenList :tokens="shown" :show-user="!userId" :busy="busy" :user-href="userHref" @revoke="revoking = $event; revokeError = ''" />
    </template>
    <ConfirmDialog :open="!!revoking" :title="t.tokens.revokeDialog" :confirm-label="t.tokens.revokeDialog" confirm-icon="ban" :busy="busy" :error="revokeError || undefined" @confirm="revoke" @cancel="revoking = undefined">
      <p>{{ t.tokens.revokeLead }} <strong>{{ revoking?.name }}</strong> ({{ revoking?.userName }}) {{ t.tokens.revokeTail }}</p>
      <p>{{ t.tokens.revokeNote }}</p>
    </ConfirmDialog>
  </div>
</template>

<style scoped>
.api-tokens-panel { display: grid; gap: 14px; min-width: 0; }
.api-tokens-filters { display: flex; flex-wrap: wrap; gap: 8px; }
</style>
