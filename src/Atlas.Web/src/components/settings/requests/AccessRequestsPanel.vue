<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import t from "../../../i18n/settings-requests";
import AtlasIcon from "../../AtlasIcon.vue";
import { ApiError } from "../../../api";
import { reauthRequiredCode } from "../../../protection";
import { accessRequestApi, type AccessRequest } from "../../../secretRelease";
import ConflictNote from "../ConflictNote.vue";
import ErrorState from "../ErrorState.vue";
import LoadingNote from "../LoadingNote.vue";
import { useSettingsFlash } from "../flash";
import { securityErrorText } from "../security/securityErrors";
import { formatDate } from "../../../formatDate";
import AccessRequestList from "./AccessRequestList.vue";
import { useAccessRequestFeed } from "./accessRequestFeed";

/**
 * Žádosti o přístup of a manager on the shared live feed (accessRequestChanged over SignalR): all of them in global settings,
 * one space's in its settings, one vault's on its detail. Approving writes a grant (fresh sign-in for the request's scope);
 * a request decided by someone else meanwhile (409) disappears with a note.
 */
const props = withDefaults(defineProps<{ spaceId?: string; vaultId?: string }>(), { spaceId: undefined, vaultId: undefined });
const feed = useAccessRequestFeed();
const flash = useSettingsFlash();
const busy = ref(false);
const error = ref("");
const conflict = ref("");
const reauthFor = ref<string | null>(null);
onMounted(() => {
  if (!feed) return;
  if (!feed.loaded.value) void feed.load();
  feed.listen();
});

const requests = computed(() => {
  if (!feed) return [];
  if (props.vaultId) return feed.inVault(props.vaultId);
  if (props.spaceId) return feed.inSpace(props.spaceId);
  return feed.pending.value;
});

async function decide(request: AccessRequest, kind: "approve" | "reject", action: () => Promise<AccessRequest>, success: (done: AccessRequest) => string) {
  busy.value = true;
  error.value = "";
  try {
    const done = await action();
    feed?.settle(done);
    reauthFor.value = null;
    flash.notify(success(done), { icon: done.state === "approved" ? "circle-check" : "circle-x" });
    void feed?.load();
  } catch (failure) {
    if (failure instanceof ApiError && failure.status === 409) {
      conflict.value = t.panel.conflict;
      feed?.settle(request);
      void feed?.load();
    } else if (failure instanceof ApiError && failure.code === reauthRequiredCode && kind === "reject") {
      // Only if a server ever asks: a rejection needs no fresh sign-in. A refused approval (its window closed) says why in place,
      // and its confirmation asks for the sign-in again.
      reauthFor.value = request.id;
    } else error.value = securityErrorText(failure);
  } finally {
    busy.value = false;
  }
}
function approve(request: AccessRequest, expiresAt: string | null, proof: string | null) {
  void decide(request, "approve", () => accessRequestApi.approve(request.id, expiresAt, proof), (done) =>
    t.panel.approved(done.requesterName, done.grantExpiresAt ? formatDate(done.grantExpiresAt) : undefined),
  );
}
function reject(request: AccessRequest, note: string | null, proof: string | null) {
  void decide(request, "reject", () => accessRequestApi.reject(request.id, note, proof), (done) => t.panel.rejected(done.requesterName));
}
</script>

<template>
  <div class="access-requests" data-access-requests>
    <ConflictNote v-if="conflict" :text="conflict" @dismiss="conflict = ''" />
    <ErrorState v-if="feed?.error.value && !feed.pending.value.length" :title="t.panel.loadFailed" :text="feed.error.value" @retry="feed.load()" />
    <LoadingNote v-else-if="feed && !feed.loaded.value" :label="t.panel.loading" />
    <AccessRequestList v-else :requests="requests" :busy="busy" :error="error" :reauth-for="reauthFor" :compact="!!vaultId" @approve="approve" @reject="reject" />
    <p class="st-note"><AtlasIcon name="info" :size="15" />{{ t.panel.note }}</p>
  </div>
</template>

<style scoped>
.access-requests { display: grid; gap: 12px; min-width: 0; }
</style>
