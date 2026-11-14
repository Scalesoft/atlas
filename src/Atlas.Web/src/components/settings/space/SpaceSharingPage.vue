<script setup lang="ts">
import t from "../../../i18n/settings-space";
import { computed, defineComponent, h, onBeforeUnmount, ref, watch } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import { Switch } from "../../ui/switch";
import { ApiError, errorMessage } from "../../../api";
import { formatDate, formatDateTime } from "../../../formatDate";
import { sharingApi, type ShareLink, type SharingPolicy } from "../../../sharing";
import ConfirmDialog from "../ConfirmDialog.vue";
import ConflictNote from "../ConflictNote.vue";
import EmptyState from "../EmptyState.vue";
import ErrorState from "../ErrorState.vue";
import InlineReveal from "../InlineReveal.vue";
import LoadingNote from "../LoadingNote.vue";
import SettingsList from "../SettingsList.vue";
import SettingsListRow from "../SettingsListRow.vue";
import SettingsPage from "../SettingsPage.vue";
import SettingsRow from "../SettingsRow.vue";
import SettingsSection from "../SettingsSection.vue";
import StatusBadge from "../StatusBadge.vue";
import { disclosureHooks } from "../disclosure";
import { useSettingsFlash } from "../flash";
import type { StatusTone } from "../types";

// Sdílení of a space: the anonymous links policy (turning it off revokes every active link, so it
// asks first) and the inventory of the space's links, fifty at a time, each revocable. `embedded` renders the same
// content without its own page header (the Odkazy tab of the space in the security centre).
const props = withDefaults(defineProps<{ spaceId: string; embedded?: boolean }>(), { embedded: false });
const EmbeddedFrame = defineComponent((_, { slots }) => () => h("div", { class: "space-sharing-embedded" }, slots.default?.()));
const frame = computed(() => (props.embedded ? EmbeddedFrame : SettingsPage));
const frameProps = computed(() =>
  props.embedded
    ? {}
    : { title: t.sharing.title, icon: "link", description: t.sharing.description },
);
const flash = useSettingsFlash();

const policy = ref<SharingPolicy>();
const links = ref<ShareLink[]>([]);
const hasMore = ref(false);
const loading = ref(false);
const busy = ref(false);
const error = ref("");
const conflict = ref("");
const confirmDisable = ref(false);
const disableError = ref("");
const revoking = ref<ShareLink>();
const revokeError = ref("");
const active = computed(() => links.value.filter((link) => link.status === "active").length);
let request = 0;
let disposed = false;
const current = (id: number, spaceId: string) => !disposed && id === request && spaceId === props.spaceId;

async function load() {
  const id = ++request;
  const spaceId = props.spaceId;
  loading.value = true;
  error.value = "";
  try {
    const [nextPolicy, page] = await Promise.all([sharingApi.policy(spaceId), sharingApi.spaceLinks(spaceId)]);
    if (!current(id, spaceId)) return;
    policy.value = nextPolicy;
    links.value = page.items;
    hasMore.value = page.hasMore;
  } catch (failure) {
    if (current(id, spaceId)) error.value = errorMessage(failure);
  } finally {
    if (current(id, spaceId)) loading.value = false;
  }
}
watch(() => props.spaceId, () => {
  policy.value = undefined;
  links.value = [];
  hasMore.value = false;
  busy.value = false;
  conflict.value = "";
  confirmDisable.value = false;
  revoking.value = undefined;
  void load();
}, { immediate: true });
onBeforeUnmount(() => {
  disposed = true;
});

function toggle(enabled: boolean) {
  if (!policy.value || busy.value) return;
  // Turning off is destructive (all active links end); turning on is not.
  if (!enabled) {
    disableError.value = "";
    confirmDisable.value = true;
  } else void setEnabled(true);
}
async function setEnabled(enabled: boolean) {
  if (!policy.value || busy.value) return;
  const id = request;
  const spaceId = props.spaceId;
  busy.value = true;
  conflict.value = "";
  disableError.value = "";
  try {
    const nextPolicy = await sharingApi.setPolicy(spaceId, { enabled, expectedVersion: policy.value.version });
    const page = await sharingApi.spaceLinks(spaceId);
    if (!current(id, spaceId)) return;
    policy.value = nextPolicy;
    links.value = page.items;
    hasMore.value = page.hasMore;
    confirmDisable.value = false;
    flash.notify(enabled ? t.sharing.enabled : t.sharing.disabled, { icon: enabled ? "link" : "unlink" });
  } catch (failure) {
    if (!current(id, spaceId)) return;
    if (failure instanceof ApiError && failure.status === 409) {
      confirmDisable.value = false;
      conflict.value = t.sharing.conflict;
      await load();
    } else if (confirmDisable.value) disableError.value = errorMessage(failure);
    else error.value = errorMessage(failure);
  } finally {
    if (!disposed && spaceId === props.spaceId) busy.value = false;
  }
}
async function revoke() {
  const link = revoking.value;
  if (!link || busy.value) return;
  const id = request;
  const spaceId = props.spaceId;
  busy.value = true;
  revokeError.value = "";
  try {
    await sharingApi.revokeLink(link.pageId, link.id);
    const page = await sharingApi.spaceLinks(spaceId);
    if (!current(id, spaceId)) return;
    links.value = page.items;
    hasMore.value = page.hasMore;
    revoking.value = undefined;
    flash.notify(t.sharing.revoked(link.name, link.pageTitle), { icon: "unlink" });
  } catch (failure) {
    if (current(id, spaceId)) revokeError.value = errorMessage(failure);
  } finally {
    if (current(id, spaceId)) busy.value = false;
  }
}
async function more() {
  if (busy.value) return;
  const id = request;
  const spaceId = props.spaceId;
  busy.value = true;
  try {
    const page = await sharingApi.spaceLinks(spaceId, links.value.length);
    if (!current(id, spaceId)) return;
    links.value.push(...page.items);
    hasMore.value = page.hasMore;
  } catch (failure) {
    if (current(id, spaceId)) error.value = errorMessage(failure);
  } finally {
    if (current(id, spaceId)) busy.value = false;
  }
}

const statusBadge: Record<ShareLink["status"], { tone: StatusTone; icon: string; label: string }> = {
  active: { tone: "success", icon: "link", get label() { return t.sharing.valid; } },
  expired: { tone: "neutral", icon: "hourglass", get label() { return t.sharing.expired; } },
  revoked: { tone: "neutral", icon: "unlink", get label() { return t.sharing.revokedStatus; } },
  unavailable: { tone: "warning", icon: "triangle-alert", get label() { return t.sharing.unavailable; } },
};
function detail(link: ShareLink) {
  const opened = link.lastOpenedAt ? t.sharing.lastOpened(formatDateTime(link.lastOpenedAt)) : "";
  return t.sharing.detail(link.name, link.live, link.revisionVersion, link.createdByName, formatDate(link.createdAt), opened);
}
</script>

<template>
  <component :is="frame" v-bind="frameProps">
    <ConflictNote v-if="conflict" :text="conflict" data-sharing-conflict @dismiss="conflict = ''" />
    <ErrorState v-if="error" :title="t.sharing.loadFailed" :text="error" :busy="loading" @retry="load()" />
    <LoadingNote v-if="loading && !policy" :label="t.sharing.loading" />
    <template v-if="policy">
      <SettingsSection :title="t.sharing.links" icon="link">
        <SettingsRow :label="t.sharing.allow" :help="t.sharing.allowHelp" :label-for="false">
          <template #default="{ labelId, describedBy }">
            <div class="sharing-toggle">
              <Switch :model-value="policy.enabled" :disabled="busy" :aria-labelledby="labelId" :aria-describedby="describedBy" data-sharing-toggle @update:model-value="toggle" />
              <StatusBadge :tone="policy.enabled ? 'success' : 'neutral'" :icon="policy.enabled ? 'link' : 'unlink'" :label="policy.enabled ? t.sharing.on : t.sharing.off" size="sm" />
            </div>
          </template>
        </SettingsRow>
        <Transition :css="false" v-bind="disclosureHooks"><p v-if="!policy.enabled" class="st-note"><AtlasIcon name="info" :size="15" />{{ t.sharing.offNote }}</p></Transition>
      </SettingsSection>

      <!-- Nothing to list while sharing is off and no link exists: the switch above says it all. The section unfolds and folds
           with the switch instead of popping in and out. -->
      <InlineReveal :open="links.length > 0 || policy.enabled">
      <SettingsSection :title="t.sharing.spaceLinks" icon="link" :count="links.length" :description="active ? t.sharing.validNow(active, hasMore) : undefined">
        <SettingsList v-if="links.length" :label="t.sharing.list">
          <SettingsListRow v-for="link in links" :key="link.id" :title="link.pageTitle" :description="detail(link)" icon="file-text" :data-share-link="link.id">
            <template #meta>
              <StatusBadge :tone="statusBadge[link.status].tone" :icon="statusBadge[link.status].icon" :label="link.status === 'active' ? t.sharing.until(formatDateTime(link.expiresAt)) : statusBadge[link.status].label" size="sm" />
            </template>
            <template v-if="link.status === 'active'" #actions>
              <Button variant="ghost" size="sm" type="button" :disabled="busy" :aria-label="t.sharing.revokeLink(link.name)" data-revoke-link @click="revoking = link; revokeError = ''"><AtlasIcon name="unlink" :size="15" />{{ t.sharing.revoke }}</Button>
            </template>
          </SettingsListRow>
        </SettingsList>
        <EmptyState v-else compact icon="link" :title="t.sharing.noLinks" :text="policy.enabled ? t.sharing.noLinksOn : t.sharing.noLinksOff" />
        <div v-if="hasMore" class="st-actions">
          <Button variant="outline" size="sm" type="button" :disabled="busy" data-load-more @click="more"><AtlasIcon name="down" :size="15" />{{ t.sharing.loadMore }}</Button>
        </div>
      </SettingsSection>
      </InlineReveal>
    </template>

    <ConfirmDialog :open="confirmDisable" :title="t.sharing.disableTitle" :confirm-label="t.sharing.disableConfirm" confirm-icon="unlink" :busy="busy" :error="disableError" :cancel-label="t.sharing.keepOn" @confirm="setEnabled(false)" @cancel="confirmDisable = false">
      <p>{{ t.sharing.disableText(active, hasMore) }}</p>
      <p>{{ t.sharing.disableNote }}</p>
    </ConfirmDialog>
    <ConfirmDialog :open="!!revoking" :title="t.sharing.revokeTitle" :confirm-label="t.sharing.revokeTitle" confirm-icon="unlink" :cancel-label="t.sharing.keep" :busy="busy" :error="revokeError" @confirm="revoke" @cancel="revoking = undefined">
      <p>{{ t.sharing.revokeLead }} <strong>{{ revoking?.name }}</strong> {{ t.sharing.revokeMid }} {{ revoking?.pageTitle }} {{ t.sharing.revokeTail }}</p>
    </ConfirmDialog>
  </component>
</template>

<style scoped>
.space-sharing-embedded { display: grid; gap: 14px; min-width: 0; }
.sharing-toggle { display: flex; align-items: center; gap: 10px; min-height: 32px; }
</style>
