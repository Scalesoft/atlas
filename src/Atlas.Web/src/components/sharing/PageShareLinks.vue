<script setup lang="ts">
import { computed, nextTick, onUnmounted, ref } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import LoadingRegion from "../LoadingRegion.vue";
import { Button } from "../ui/button";
import ConfirmDialog from "../settings/ConfirmDialog.vue";
import { disclosureHooks } from "../settings/disclosure";
import EmptyState from "../settings/EmptyState.vue";
import ErrorState from "../settings/ErrorState.vue";
import InlineReveal from "../settings/InlineReveal.vue";
import OneTimeSecret from "../settings/OneTimeSecret.vue";
import OptionPills from "../settings/OptionPills.vue";
import ProgressiveFlow from "../settings/ProgressiveFlow.vue";
import ResultBanner from "../settings/ResultBanner.vue";
import SettingsList from "../settings/SettingsList.vue";
import SettingsListRow from "../settings/SettingsListRow.vue";
import SettingsRow from "../settings/SettingsRow.vue";
import SettingsSection from "../settings/SettingsSection.vue";
import StatusBadge from "../settings/StatusBadge.vue";
import SummaryList from "../settings/SummaryList.vue";
import { localInput } from "../settings/grantValidity";
import type { ChoiceOption, FlowStep, StatusTone, SummaryItem } from "../settings/types";
import { errorMessage } from "../../api";
import { formatDate, formatDateTime } from "../../formatDate";
import { shareLinkMaxDays, sharingApi, type ShareLink, type SharingPolicy } from "../../sharing";
import t from "../../i18n/components-sharing";

/**
 * „Anonymní odkazy“ of one page: the links with their state and a confirmed Zneplatnit (the words of Nastavení
 * prostoru → Sdílení), and a new link as a progressive flow that unfolds above the list: Název → Platnost → Souhrn,
 * then the URL shown exactly once (OneTimeSecret). The URL is never stored; after Pokračovat only the link's row remains.
 */
const props = defineProps<{ pageId: string; pageTitle: string; spaceId: string; isPublished: boolean }>();
const emit = defineEmits<{ changed: [] }>();
const policy = ref<SharingPolicy>(), links = ref<ShareLink[]>([]), hasMore = ref(false);
const loading = ref(false), busy = ref(false), error = ref("");
let retry: () => void = () => void load(), disposed = false;
const canCreate = computed(() => !!policy.value?.enabled && props.isPublished);
const active = computed(() => links.value.filter(link => link.status === "active").length);

async function load() {
  loading.value = true; error.value = ""; retry = () => void load();
  try {
    const [nextPolicy, page] = await Promise.all([sharingApi.policy(props.spaceId), sharingApi.pageLinks(props.pageId)]);
    if (disposed) return;
    policy.value = nextPolicy; links.value = page.items; hasMore.value = page.hasMore;
  } catch (failure) {
    if (!disposed) error.value = errorMessage(failure);
  } finally {
    if (!disposed) loading.value = false;
  }
}
async function more() {
  if (busy.value) return;
  busy.value = true; error.value = ""; retry = () => void more();
  try {
    const page = await sharingApi.pageLinks(props.pageId, links.value.length);
    if (disposed) return;
    links.value.push(...page.items); hasMore.value = page.hasMore;
  } catch (failure) {
    if (!disposed) error.value = errorMessage(failure);
  } finally {
    if (!disposed) busy.value = false;
  }
}
void load();

const notice = ref<{ key: number; text: string; icon: string }>(), highlight = ref<string>();
let noticeKey = 0;
function notify(text: string, icon: string) { notice.value = { key: ++noticeKey, text, icon }; }
function focus(selector: string) { void nextTick(() => document.querySelector<HTMLElement>(selector)?.focus({ preventScroll: true })); }

const statusBadge: Record<ShareLink["status"], { tone: StatusTone; icon: string; label: string }> = {
  active: { tone: "success", icon: "link", label: t.links.active },
  expired: { tone: "neutral", icon: "hourglass", label: t.links.expired },
  revoked: { tone: "neutral", icon: "unlink", label: t.links.revoked },
  unavailable: { tone: "warning", icon: "triangle-alert", label: t.links.unavailable },
};
function detail(link: ShareLink) {
  const opened = link.lastOpenedAt ? t.links.lastOpened(formatDateTime(link.lastOpenedAt)) : t.links.neverOpened;
  return t.links.detail(link.live, link.revisionVersion, link.createdByName, formatDate(link.createdAt), opened);
}

// Zneplatnit: the link stops working at once and cannot be restored.
const revoking = ref<ShareLink>(), revokeError = ref("");
async function revoke() {
  const link = revoking.value;
  if (!link || busy.value) return;
  busy.value = true; revokeError.value = "";
  try {
    await sharingApi.revokeLink(props.pageId, link.id);
    const page = await sharingApi.pageLinks(props.pageId);
    if (disposed) return;
    links.value = page.items; hasMore.value = page.hasMore;
    revoking.value = undefined; highlight.value = link.id;
    notify(t.links.revokedNotice(link.name), "unlink");
    emit("changed");
  } catch (failure) {
    if (!disposed) revokeError.value = errorMessage(failure);
  } finally {
    if (!disposed) busy.value = false;
  }
}

// A new link: one screen that unfolds as it is filled in.
const creating = ref(false), step = ref("name"), name = ref(""), days = ref("7"), customExpiry = ref("");
const createBusy = ref(false), createError = ref(""), issued = ref<{ url: string; link: ShareLink }>();
// The server's limit (shareLinkMaxDays): the presets end with it and the own date cannot be picked beyond it.
const maxDaysText = t.links.maxDays(shareLinkMaxDays);
const validityOptions: ChoiceOption[] = [
  { value: "1", label: t.links.day, icon: "clock" },
  { value: "7", label: t.links.days(7), icon: "calendar" },
  { value: String(shareLinkMaxDays), label: t.links.days(shareLinkMaxDays), icon: "calendar" },
  { value: "custom", label: t.links.customDate, icon: "pencil" },
];
const expiryMinimum = computed(() => localInput(new Date(Date.now() + 60_000)));
const expiryMaximum = computed(() => localInput(new Date(Date.now() + shareLinkMaxDays * 86_400_000)));
const expiry = computed(() => {
  if (days.value === "custom") {
    const value = new Date(customExpiry.value);
    return customExpiry.value && Number.isFinite(value.getTime()) && value.getTime() > Date.now() && value.getTime() <= Date.now() + shareLinkMaxDays * 86_400_000 ? value.toISOString() : "";
  }
  return new Date(Date.now() + Number(days.value) * 86_400_000).toISOString();
});
const expiryError = computed(() => days.value === "custom" && customExpiry.value && !expiry.value ? t.links.futureTime(maxDaysText) : "");
const steps = computed<FlowStep[]>(() => [
  { id: "name", label: t.links.stepName.label, icon: "pencil", title: t.links.stepName.title, hint: t.links.stepName.hint, summary: name.value.trim() || undefined, ready: !!name.value.trim() },
  { id: "validity", label: t.links.stepValidity.label, icon: "calendar", title: t.links.stepValidity.title, hint: t.links.validityHint(shareLinkMaxDays), summary: expiry.value ? t.links.validityUntil(formatDate(expiry.value)) : undefined, ready: !!expiry.value },
  { id: "summary", label: t.links.stepSummary.label, icon: "circle-check", title: t.links.stepSummary.title, hint: t.links.stepSummary.hint, manual: true, nextLabel: t.links.stepSummary.next, nextIcon: "link" },
  { id: "link", label: t.links.stepLink.label, icon: "link", title: t.links.stepLink.title, hint: t.links.stepLink.hint, final: true },
]);
const summary = computed<SummaryItem[]>(() => [
  { key: "page", icon: "file-text", label: t.links.page, value: props.pageTitle },
  { key: "name", icon: "pencil", label: t.links.stepName.label, value: name.value.trim() },
  { key: "expires", icon: "calendar", label: t.links.validUntilLabel, value: formatDateTime(expiry.value) },
  { key: "who", icon: "globe", label: t.links.whoOpens, value: t.links.anyone },
]);
function startCreating() {
  notice.value = undefined;
  name.value = ""; days.value = "7"; customExpiry.value = ""; createError.value = ""; step.value = "name";
  creating.value = true;
  focus('[data-step="name"] input');
}
function stopCreating() {
  creating.value = false;
  focus("[data-new-link]");
}
async function create(current: FlowStep) {
  if (current.id !== "summary" || createBusy.value || !name.value.trim() || !expiry.value) return;
  createBusy.value = true; createError.value = "";
  try {
    const result = await sharingApi.createLink(props.pageId, { name: name.value.trim(), expiresAt: expiry.value });
    if (disposed) return;
    issued.value = { url: new URL(result.url, location.origin).href, link: result.link };
    links.value = [result.link, ...links.value.filter(item => item.id !== result.link.id)];
    highlight.value = result.link.id;
    step.value = "link";
    emit("changed");
  } catch (failure) {
    if (!disposed) createError.value = errorMessage(failure);
  } finally {
    if (!disposed) createBusy.value = false;
  }
}
function finish() {
  // The URL leaves the page here: only the link's row stays.
  const link = issued.value?.link;
  issued.value = undefined;
  creating.value = false;
  if (link) notify(t.links.created(link.name, formatDateTime(link.expiresAt)), "link");
  focus("[data-new-link]");
}
onUnmounted(() => { disposed = true; issued.value = undefined; });
</script>

<template>
  <div class="page-links">
    <ResultBanner v-if="notice" :key="notice.key" :message="notice.text" :icon="notice.icon" @dismiss="notice = undefined" />
    <ErrorState v-if="error" :title="t.links.loadFailed" :text="error" :busy="loading || busy" @retry="retry()" />
    <LoadingRegion v-if="!policy && !error" :loading="loading" :ready="false" :rows="2" :label="t.links.loading" />
    <SettingsSection v-if="policy" :title="t.links.title" icon="link" :description="active ? t.links.describeActive(active, hasMore) : t.links.describeNone">
      <template #title-extra><span :key="links.length" class="st-count page-links-count">{{ links.length }}</span></template>
      <template v-if="canCreate && !creating" #actions>
        <Button size="sm" type="button" :disabled="busy" data-new-link @click="startCreating"><AtlasIcon name="plus" :size="15" />{{ t.links.newLink }}</Button>
      </template>
      <InlineReveal :open="creating">
        <div class="page-links-create" role="group" :aria-label="t.links.newLinkLabel">
          <ProgressiveFlow v-model="step" :steps="steps" :label="t.links.newLinkLabel" :busy="createBusy" @next="create" @cancel="stopCreating">
            <template #name>
              <SettingsRow v-slot="{ id, describedBy }" :label="t.links.linkName" :help="t.links.linkNameHelp">
                <input :id="id" v-model="name" class="st-input" name="name" maxlength="100" autocomplete="off" :aria-describedby="describedBy" data-step-focus />
              </SettingsRow>
            </template>
            <template #validity>
              <OptionPills v-model="days" :options="validityOptions" :label="t.links.validityLabel" hide-label />
              <InlineReveal :open="days === 'custom'">
                <label class="page-link-date">
                  <span><AtlasIcon name="calendar" :size="14" />{{ t.links.validUntilField }} <small data-link-limit>{{ maxDaysText }}</small></span>
                  <input v-model="customExpiry" class="st-input" type="datetime-local" name="expiresAt" :min="expiryMinimum" :max="expiryMaximum" :aria-invalid="expiryError ? true : undefined" :aria-describedby="expiryError ? 'page-link-expiry-error' : undefined" />
                </label>
              </InlineReveal>
              <Transition :css="false" v-bind="disclosureHooks"><p v-if="expiryError" id="page-link-expiry-error" class="page-link-error" role="alert"><AtlasIcon name="triangle-alert" :size="14" />{{ expiryError }}</p></Transition>
              <p class="st-note"><AtlasIcon name="info" :size="15" />{{ t.links.timezone }}</p>
            </template>
            <template #summary>
              <SummaryList :items="summary" :label="t.links.summaryLabel" />
              <Transition :css="false" v-bind="disclosureHooks"><p v-if="createBusy" class="st-note" role="status"><AtlasIcon name="loader-circle" :size="15" class="atlas-spinner" />{{ t.links.creating }}</p></Transition>
              <Transition :css="false" v-bind="disclosureHooks"><p v-if="createError" class="page-link-error" role="alert"><AtlasIcon name="triangle-alert" :size="14" />{{ createError }}</p></Transition>
            </template>
            <template #link>
              <OneTimeSecret
                v-if="issued"
                :value="issued.url"
                :label="t.links.newLinkLabel"
                :note="t.links.secretNote"
                :confirm-label="t.links.secretConfirm"
                :finished-text="t.links.secretFinished"
                data-issued-link
                @done="finish"
              />
            </template>
          </ProgressiveFlow>
        </div>
      </InlineReveal>
      <p v-if="!policy.enabled" class="st-note" data-links-disabled><AtlasIcon name="lock" :size="15" />{{ t.links.disabled }}</p>
      <p v-else-if="!isPublished" class="st-note"><AtlasIcon name="info" :size="15" />{{ t.links.notPublished }}</p>
      <SettingsList v-if="links.length" :label="t.links.listLabel">
        <SettingsListRow
          v-for="link in links"
          :key="link.id"
          :title="link.name"
          :description="detail(link)"
          icon="link"
          :icon-tone="link.status === 'active' ? undefined : 'neutral'"
          :highlighted="highlight === link.id"
          :data-share-link="link.id"
        >
          <template #meta>
            <Transition name="page-state" mode="out-in">
              <StatusBadge :key="link.status" :tone="statusBadge[link.status].tone" :icon="statusBadge[link.status].icon" :label="link.status === 'active' ? t.links.until(formatDateTime(link.expiresAt)) : statusBadge[link.status].label" size="sm" />
            </Transition>
          </template>
          <template v-if="link.status === 'active'" #actions>
            <Button variant="ghost" size="sm" type="button" :disabled="busy" :aria-label="t.links.revokeLabel(link.name)" data-revoke-link @click="revoking = link; revokeError = ''"><AtlasIcon name="unlink" :size="15" />{{ t.links.revoke }}</Button>
          </template>
        </SettingsListRow>
      </SettingsList>
      <EmptyState v-else compact icon="link" :title="t.links.emptyTitle" :text="canCreate ? t.links.emptyText : undefined" />
      <div v-if="hasMore" class="st-actions">
        <Button variant="outline" size="sm" type="button" :disabled="busy" data-load-more @click="more"><AtlasIcon name="down" :size="15" />{{ t.links.loadMore }}</Button>
      </div>
      <p class="st-note"><AtlasIcon name="info" :size="15" />{{ t.links.standalone }}</p>
    </SettingsSection>

    <ConfirmDialog :open="!!revoking" :title="t.links.revokeTitle" :confirm-label="t.links.revokeConfirm" confirm-icon="unlink" :cancel-label="t.links.keep" :busy="busy" :error="revokeError" @confirm="revoke" @cancel="revoking = undefined">
      <p>{{ t.links.revokeLink }} <strong>{{ revoking?.name }}</strong> {{ t.links.revokeOn(pageTitle) }}</p>
    </ConfirmDialog>
  </div>
</template>

<style scoped>
.page-links { display: grid; gap: 18px; min-width: 0; }
.page-links-count { animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.page-links-create { padding: 2px 0 16px; border-bottom: 1px solid var(--line); }
.page-link-date { display: grid; gap: 5px; max-width: 320px; padding-top: 4px; font-size: 13px; font-weight: 600; }
.page-link-date > span { display: inline-flex; align-items: center; gap: 6px; }
.page-link-date small { color: var(--muted); font-size: 12px; font-weight: 500; }
.page-link-error { display: flex; align-items: center; gap: 6px; margin: 0; color: var(--error); font-size: 13px; font-weight: 550; }
/* A link that stops being valid changes its state badge in place. */
.page-state-enter-active { animation: st-pop var(--atlas-motion-slow) var(--atlas-ease-out); }
.page-state-leave-active { animation: st-fade-out var(--atlas-motion-fast) var(--atlas-ease-in); }
</style>
