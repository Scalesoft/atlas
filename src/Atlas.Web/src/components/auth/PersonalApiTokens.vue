<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, reactive, ref, watch } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import { Button } from "../ui/button";
import { Input } from "../ui/input";
import ChoiceCards from "../settings/ChoiceCards.vue";
import ConfirmDialog from "../settings/ConfirmDialog.vue";
import ConfirmWithReauth from "../settings/ConfirmWithReauth.vue";
import EmptyState from "../settings/EmptyState.vue";
import ErrorState from "../settings/ErrorState.vue";
import InlineReveal from "../settings/InlineReveal.vue";
import LoadingNote from "../settings/LoadingNote.vue";
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
import type { ChoiceOption, FlowStep, StatusTone, SummaryItem } from "../settings/types";
import CopyField from "../settings/general/CopyField.vue";
import { provideReauthEnvironment } from "../settings/useReauth";
import { createApiReauthEnvironment } from "../settings/security/apiReauth";
import { createSecurityState } from "../settings/security/securityState";
import { ApiError, api, errorMessage } from "../../api";
import { reauthRequiredCode, securityChange } from "../../protection";
import { formatDate, formatDateTime } from "../../formatDate";
import { locale } from "../../i18n";
import t from "../../i18n/components-auth";
import { apiChannelInfo, apiChannels, apiLevel, apiLevelInfo, levelOfScopes, levelRank, levelsUpTo, rightsText, scopesOfLevel, type ApiLevel } from "../../apiTokenRights";
import { useHeightEase } from "../../composables/useHeightEase";
import { dismissOut, popStatus, revealHeight } from "../../motion/aiMotion";

/**
 * Osobní API (profile): the caller's personal tokens. A new token is an inline flow in place of the
 * lists (Název · Rozsah · Kanály · Platnost · Souhrn, then the one-time value); only revoking asks in a small
 * destructive dialog. A token never widens the holder's own space rights, and its scope (Čtení ⊂ Zápis) never
 * exceeds the level an administrator set for the account: higher scopes are not offered at all.
 * The channels (REST API, MCP) say where the token is accepted. A token the ceiling trimmed or one that cannot be
 * used yet says so in its row. A token outlives the session that issues it, so issuing one is confirmed by a fresh sign-in
 * (ConfirmWithReauth in the summary step, SEC-A-2); the ten-minute window keeps a series of tokens cheap.
 */
const emit = defineEmits<{ dirty: [value: boolean]; busy: [value: boolean]; catalogUpdated: [value: TokenCatalog] }>();
interface TokenInfo {
  id: string;
  name: string;
  prefix: string;
  scopes: string[];
  activeFrom: string;
  expiresAt: string;
  activeTo: string | null;
  /** rest and/or mcp; absent in a response from before channels existed (rest). */
  channels?: string[];
  /** The scopes within the owner's current level: what the token may do on its next request. */
  effectiveScopes?: string[];
  /** False for an expired or closed token, a level of none, or an mcp-only token while the MCP module is off. */
  usable?: boolean;
}
export interface TokenCatalog {
  canIssueTokens: boolean;
  permissionVersion: string | null;
  tokens: TokenInfo[];
  /** The owner's ceiling: none, read, draft or publish. */
  level?: string;
  mcpEnabled?: boolean;
  mcpUrl?: string | null;
  apiBaseUrl: string;
  openApiUrl: string;
}
/** Where the tokens come from and go to: the profile endpoints, or sample data in the gallery. */
export interface TokenSource {
  list: () => Promise<TokenCatalog>;
  /** proof: the fresh sign-in, or null inside the re-authentication window. */
  issue: (body: { name: string; scopes: string[]; channels: string[]; expiresInDays: number }, proof: string | null) => Promise<{ token: TokenInfo; secret: string }>;
  revoke: (id: string) => Promise<unknown>;
}
const props = withDefaults(defineProps<{ source?: TokenSource; initialCatalog?: TokenCatalog }>(), {
  source: () => ({
    list: () => api<TokenCatalog>("/profile/api-tokens"),
    issue: (body, proof) => securityChange<{ token: TokenInfo; secret: string }>("/profile/api-tokens", "POST", body, proof),
    revoke: (id) => api(`/profile/api-tokens/${encodeURIComponent(id)}`, { method: "DELETE" }),
  }),
});
const catalog = ref<TokenCatalog | undefined>(props.initialCatalog);
// The profile is not inside the settings host, so the fresh sign-in of a new token is provided here.
const reauthState = createSecurityState();
provideReauthEnvironment(createApiReauthEnvironment(reauthState));
const loading = ref(!props.initialCatalog), loadError = ref(""), busy = ref(false);
// The result line after a change, and the row it concerns in its list (rises in and glows once). A revoked
// token leaves the active list with a fade while it arrives among the invalid ones, so the list is part of it.
const result = ref(""), highlight = ref<{ id: string; list: "active" | "inactive" }>();
const highlighted = (token: TokenInfo, list: "active" | "inactive") => highlight.value?.id === token.id && highlight.value.list === list;
let disposed = false;

async function load() {
  loading.value = true;
  try {
    const data = await props.source.list();
    if (disposed) return;
    catalog.value = data;
    emit("catalogUpdated", data);
    loadError.value = "";
  } catch (failure) {
    if (!disposed) loadError.value = errorMessage(failure);
  } finally {
    if (!disposed) loading.value = false;
  }
}
const isActive = (token: TokenInfo) => !token.activeTo && new Date(token.expiresAt).getTime() > Date.now();
const active = computed(() => catalog.value?.tokens.filter(isActive) ?? []);
const inactive = computed(() => catalog.value?.tokens.filter((token) => !isActive(token)) ?? []);
const levelTone = { none: "neutral", read: "neutral", write: "accent" } as const;
const scopeBadge = (token: TokenInfo): { tone: StatusTone; icon: string; label: string } => {
  const level = levelOfScopes(token.scopes);
  return { tone: levelTone[level], icon: apiLevelInfo[level].icon, label: apiLevelInfo[level].label };
};
const channelsOf = (token: TokenInfo) => apiChannels(token.channels);
const ceiling = computed<ApiLevel>(() => (catalog.value?.level !== undefined ? apiLevel(catalog.value.level) : catalog.value?.canIssueTokens ? "read" : "none"));
const mcpOn = computed(() => catalog.value?.mcpEnabled === true);
// A token the ceiling trimmed: its own scopes reach higher than what it may do now.
const trimmedTo = (token: TokenInfo): ApiLevel | undefined => {
  if (!token.effectiveScopes) return undefined;
  const effective = levelOfScopes(token.effectiveScopes);
  return levelRank(effective) < levelRank(levelOfScopes(token.scopes)) ? effective : undefined;
};
const unusableReason = (token: TokenInfo): string | undefined => {
  if (token.usable !== false) return undefined;
  if (token.effectiveScopes && !token.effectiveScopes.length) return t.tokens.unusableRevoked;
  const channels = channelsOf(token);
  if (channels.length && channels.every((channel) => channel === "mcp") && !mcpOn.value) return t.tokens.unusableMcp;
  return t.tokens.unusable;
};
const restriction = (token: TokenInfo): string | undefined => {
  if (unusableReason(token)) return undefined;
  const level = trimmedTo(token);
  return level ? t.tokens.restricted(apiLevelInfo[level].label.toLocaleLowerCase(locale)) : undefined;
};
const stateBadge = (token: TokenInfo): { tone: StatusTone; icon: string; label: string } =>
  token.activeTo ? { tone: "neutral", icon: "ban", label: t.tokens.revokedBadge } : { tone: "warning", icon: "hourglass", label: t.tokens.expiredBadge };
const endedAt = (token: TokenInfo) => (token.activeTo ? t.tokens.revokedAt(formatDateTime(token.activeTo)) : t.tokens.expiredAt(formatDateTime(token.expiresAt)));

// Nový token: only the levels up to the account's ceiling are offered; a higher one is not shown at all.
const accessOptions = computed<ChoiceOption[]>(() =>
  levelsUpTo(ceiling.value).map((level) => ({ value: level, label: apiLevelInfo[level].label, description: apiLevelInfo[level].description, icon: apiLevelInfo[level].icon })),
);
const channelOptions = computed<ChoiceOption[]>(() => [
  { value: "rest", label: "REST API", description: t.tokens.restApiHelp, icon: apiChannelInfo.rest.icon },
  { value: "mcp", label: "MCP", description: mcpOn.value ? t.tokens.mcpHelp : t.tokens.mcpHelpOff, icon: apiChannelInfo.mcp.icon },
]);
const periodOptions = (): ChoiceOption[] => [
  { value: "30", label: t.tokens.days(30), icon: "calendar" },
  { value: "90", label: t.tokens.days(90), icon: "calendar" },
  { value: "180", label: t.tokens.days(180), icon: "calendar" },
  { value: "365", label: t.tokens.year, icon: "calendar" },
  { value: "custom", label: t.tokens.custom, icon: "pencil" },
];
const blank = () => ({ name: "", access: "read" as ApiLevel, channels: ["rest"] as string[], period: "90", customDays: 90 });
const draft = reactive(blank());
const creating = ref(false), step = ref("name"), createError = ref("");
const issued = ref<{ token: TokenInfo; secret: string }>();
const secretHost = ref<HTMLElement>();
const issuedForMcp = computed(() => !!issued.value && channelsOf(issued.value.token).includes("mcp"));
const days = computed(() => (draft.period === "custom" ? Number(draft.customDays) : Number(draft.period)));
const daysError = computed(() => (Number.isInteger(days.value) && days.value >= 1 && days.value <= 365 ? "" : t.tokens.daysError));
const expiresPreview = computed(() => (daysError.value ? "" : formatDate(new Date(Date.now() + days.value * 86_400_000))));
const accessLabel = computed(() => apiLevelInfo[draft.access].label);
const channelsLabel = computed(() => apiChannels(draft.channels).map((channel) => apiChannelInfo[channel].label).join(t.tokens.and));
const channelsError = computed(() => (draft.channels.length ? "" : t.tokens.channelsError));
const drafted = computed(() => creating.value && !issued.value && (!!draft.name.trim() || draft.access !== "read" || draft.channels.join() !== "rest" || days.value !== 90));
watch(computed(() => drafted.value || !!issued.value), (value) => emit("dirty", value), { flush: "sync" });
watch(busy, (value) => emit("busy", value), { flush: "sync" });

const steps = computed<FlowStep[]>(() => [
  { id: "name", label: t.tokens.stepName.label, icon: "pencil", title: t.tokens.stepName.title, hint: t.tokens.stepName.hint, summary: draft.name.trim() || undefined, ready: !!draft.name.trim() },
  { id: "access", label: t.tokens.stepAccess.label, icon: "shield-check", title: t.tokens.stepAccess.title, hint: t.tokens.stepAccess.hint, summary: accessLabel.value, ready: true },
  { id: "channels", label: t.tokens.stepChannels.label, icon: "plug", title: t.tokens.stepChannels.title, hint: t.tokens.stepChannels.hint, summary: channelsError.value ? undefined : channelsLabel.value, ready: !channelsError.value },
  { id: "validity", label: t.tokens.stepValidity.label, icon: "calendar", title: t.tokens.stepValidity.title, hint: t.tokens.stepValidity.hint, summary: daysError.value ? undefined : t.tokens.days(days.value), ready: !daysError.value },
  { id: "summary", label: t.tokens.stepSummary.label, icon: "circle-check", title: t.tokens.stepSummary.title, hint: t.tokens.stepSummary.hint, hideNext: true },
  { id: "secret", label: t.tokens.stepSecret.label, icon: "key-round", title: t.tokens.stepSecret.title, final: true },
]);
const summary = computed<SummaryItem[]>(() => [
  { key: "name", icon: "pencil", label: t.tokens.stepName.label, value: draft.name.trim() },
  { key: "access", icon: "shield-check", label: t.tokens.stepAccess.label, value: accessLabel.value },
  { key: "channels", icon: "plug", label: t.tokens.stepChannels.label, value: channelsLabel.value },
  { key: "validity", icon: "calendar", label: t.tokens.stepValidity.label, value: t.tokens.validityValue(t.tokens.days(days.value), expiresPreview.value) },
]);

function startCreate() {
  if (busy.value || !catalog.value?.canIssueTokens) return;
  Object.assign(draft, blank());
  if (!levelsUpTo(ceiling.value).includes(draft.access)) draft.access = levelsUpTo(ceiling.value)[0] ?? "read";
  step.value = "name";
  createError.value = "";
  result.value = "";
  focusAfter = "[data-token-name]";
  creating.value = true;
  void reauthState.load();
}
function leaveCreate() {
  focusAfter = "[data-token-new]";
  creating.value = false;
  issued.value = undefined;
  createError.value = "";
  Object.assign(draft, blank());
}
// The list and the creation swap with the same leave and rise as the steps of a flow, and the box around them eases from the
// height of the view that left to the one that arrives (as StepFlow's stage does), so nothing below jumps by the difference.
// Focus follows the view swap: into the name field, and back to "Nový API token" afterwards.
let focusAfter = "";
function entered() {
  if (focusAfter) root.value?.querySelector<HTMLElement>(focusAfter)?.focus();
  focusAfter = "";
}
const attempt = ref(0);
async function create(proof: string | null) {
  if (busy.value || !catalog.value?.canIssueTokens) return;
  busy.value = true;
  createError.value = "";
  try {
    const created = await props.source.issue({
      name: draft.name.trim(),
      scopes: scopesOfLevel(draft.access),
      channels: apiChannels(draft.channels),
      expiresInDays: days.value,
    }, proof);
    if (disposed) return;
    issued.value = created;
    step.value = "secret";
    // P7: the confirm button went with the summary; the keyboard lands on the secret (its first control: show or copy).
    void nextTick(() => { if (secretHost.value) { revealHeight(secretHost.value); secretHost.value.querySelector<HTMLElement>("button")?.focus(); } });
    void load();
  } catch (failure) {
    if (disposed) return;
    createError.value = errorMessage(failure);
    // A refused fresh sign-in (reauth_required) asks again in a new confirmation.
    if (failure instanceof ApiError && failure.code === reauthRequiredCode) attempt.value++;
  } finally {
    if (!disposed) busy.value = false;
  }
}
// The value leaves the page with the acknowledgement: neither this component nor OneTimeSecret keeps it.
function finishCreate() {
  const token = issued.value?.token;
  if (token) {
    highlight.value = { id: token.id, list: "active" };
    result.value = t.tokens.issued(token.name ?? draft.name.trim(), formatDateTime(token.expiresAt));
  }
  leaveCreate();
}

// Zneplatnit: the one destructive step.
const revoking = ref<TokenInfo>(), revokeError = ref("");
function askRevoke(token: TokenInfo) {
  revoking.value = token;
  revokeError.value = "";
  result.value = "";
}
async function revoke() {
  const token = revoking.value;
  if (!token || busy.value) return;
  busy.value = true;
  revokeError.value = "";
  try {
    await props.source.revoke(token.id);
    if (disposed) return;
    revoking.value = undefined;
    // The row leaves the active list with a short fade while it is already among the invalid ones.
    const row = [...(root.value?.querySelectorAll<HTMLElement>("[data-token]") ?? [])].find((item) => item.dataset.token === token.id);
    const leaving = row ? dismissOut(row) : null;
    await leaving?.finished.catch(() => undefined);
    highlight.value = { id: token.id, list: "inactive" };
    result.value = t.tokens.revoked(token.name);
    await load();
  } catch (failure) {
    if (!disposed) revokeError.value = errorMessage(failure);
  } finally {
    if (!disposed) busy.value = false;
  }
}

// Dokumentace API: the profile's OpenAPI document in the editor's Scalar view, in place. The plugin code and Scalar
// load only when the user opens it. Try It stays off: the Scalar frame is same-origin and would use the user's cookies.
const docsOpen = ref(false), docsLoading = ref(false), docsError = ref("");
const docsSection = ref<HTMLElement>(), docsHost = ref<HTMLElement>();
let disposeDocs: (() => void) | undefined, docsGeneration = 0;
// The section is an InlineReveal: it folds with its frame in it, and the frame is disposed when the host leaves the
// document (the function ref below gets null then), not the moment Skrýt is pressed, which left an empty box to fold.
function setDocsHost(element: unknown) {
  docsHost.value = (element as HTMLElement | null) ?? undefined;
  if (!element) { disposeDocs?.(); disposeDocs = undefined; }
}
function closeDocs() {
  docsGeneration++;
  docsOpen.value = false; docsLoading.value = false; docsError.value = "";
}
async function mountDocs() {
  const token = ++docsGeneration;
  disposeDocs?.(); disposeDocs = undefined;
  docsLoading.value = true; docsError.value = "";
  try {
    const [document, { mountOpenApiReference }] = await Promise.all([
      api<unknown>("/profile/api-document"),
      import("@atlas/markdown/plugins/openapi"),
    ]);
    await nextTick();
    if (disposed || token !== docsGeneration || !docsHost.value) return;
    disposeDocs = mountOpenApiReference(docsHost.value, { version: 1, filename: "atlas-openapi.json", source: JSON.stringify(document) }, { allowRequests: false });
  } catch (failure) {
    if (!disposed && token === docsGeneration) docsError.value = errorMessage(failure);
  } finally {
    if (!disposed && token === docsGeneration) docsLoading.value = false;
  }
}
async function openDocs() {
  if (!docsOpen.value) { docsOpen.value = true; void mountDocs(); }
  await nextTick();
  docsSection.value?.scrollIntoView({ block: "start" });
}
// The token flow replaces the lists and with them the frame's host.
watch(creating, (value) => { if (value) closeDocs(); });

const levelNote = computed(() => (ceiling.value === "none" ? "" : t.tokens.levelNote(apiLevelInfo[ceiling.value].label.toLocaleLowerCase(locale))));
// A token whose usability changed since the last load pops its state (the prototype's status change).
const usability = new Map<string, boolean>();
watch(() => catalog.value?.tokens, (tokens) => {
  const changed = (tokens ?? []).filter((token) => usability.has(token.id) && usability.get(token.id) !== (token.usable !== false)).map((token) => token.id);
  usability.clear();
  for (const token of tokens ?? []) usability.set(token.id, token.usable !== false);
  if (changed.length) void nextTick(() => {
    for (const row of root.value?.querySelectorAll<HTMLElement>("[data-token]") ?? []) if (changed.includes(row.dataset.token ?? "")) popStatus(row.querySelector("[data-token-scope]") ?? row);
  });
});

const root = ref<HTMLElement>();
const { rememberHeight, easeHeight } = useHeightEase(root);
const example = computed(() => `curl --header "Authorization: Bearer ${t.tokens.example}" "${location.origin}/api/v1/openapi.json"`);
onMounted(() => { if (!props.initialCatalog) void load(); });
onUnmounted(() => {
  closeDocs();
  disposeDocs?.(); disposeDocs = undefined;
  disposed = true;
  issued.value = undefined;
  emit("dirty", false);
  emit("busy", false);
});
</script>

<template>
  <div ref="root" class="personal-api-tokens">
    <LoadingNote v-if="loading && !catalog" :label="t.tokens.loading" />
    <ErrorState v-else-if="loadError && !catalog" :title="t.tokens.loadFailed" :text="loadError" :busy="loading" @retry="load" />
    <Transition v-else-if="catalog" name="st-step" mode="out-in" @before-leave="rememberHeight" @enter="easeHeight" @after-enter="entered">
      <SettingsSection v-if="creating" key="create" :label="t.tokens.newToken">
        <ProgressiveFlow v-model="step" :steps="steps" :label="t.tokens.newToken" :context="t.tokens.newTokenContext" :busy="busy" @cancel="leaveCreate()">
          <template #name>
            <SettingsRow v-slot="{ id, describedBy }" :label="t.tokens.tokenName" :help="t.tokens.maxChars" stacked>
              <Input :id="id" v-model="draft.name" maxlength="100" :placeholder="t.tokens.namePlaceholder" autocomplete="off" :aria-describedby="describedBy" data-step-focus data-token-name />
            </SettingsRow>
          </template>
          <template #access>
            <ChoiceCards v-model="draft.access" :options="accessOptions" :label="t.tokens.access" hide-label :columns="2" data-token-access />
          </template>
          <template #channels>
            <ChoiceCards v-model="draft.channels" multiple :options="channelOptions" :label="t.tokens.channels" hide-label :columns="2" data-token-channels />
            <p v-if="channelsError" class="token-error" role="alert"><AtlasIcon name="triangle-alert" :size="15" />{{ channelsError }}</p>
            <p v-else-if="draft.channels.includes('mcp') && !mcpOn" class="st-note" role="note" data-token-mcp-off><AtlasIcon name="info" :size="15" />{{ t.tokens.mcpOff }}</p>
          </template>
          <template #validity>
            <OptionPills v-model="draft.period" :options="periodOptions()" :label="t.tokens.validity" hide-label data-token-validity />
            <InlineReveal :open="draft.period === 'custom'">
              <SettingsRow v-slot="{ id, describedBy, invalid }" :label="t.tokens.dayCount" :help="t.tokens.dayCountHelp" :error="daysError || undefined" stacked>
                <Input :id="id" v-model.number="draft.customDays" type="number" min="1" max="365" class="token-days" :aria-describedby="describedBy" :aria-invalid="invalid" data-token-days />
              </SettingsRow>
            </InlineReveal>
            <p v-if="expiresPreview" class="st-note" aria-live="polite"><AtlasIcon name="clock" :size="15" />{{ t.tokens.expires(expiresPreview) }}</p>
          </template>
          <template #summary>
            <SummaryList :items="summary" :label="t.tokens.summaryLabel" />
            <ConfirmWithReauth :key="attempt" :title="t.tokens.confirmTitle" :description="t.tokens.confirmDescription" :confirm-label="t.tokens.create" :cancel-label="t.tokens.cancel" :disabled="!!channelsError || !!daysError || !draft.name.trim()" :busy="busy" :error="createError || undefined" data-token-confirm @confirm="create" @cancel="leaveCreate()" />
          </template>
          <template #secret>
            <div ref="secretHost" class="token-secret" :aria-label="t.tokens.issuedLabel" role="group">
              <OneTimeSecret
                v-if="issued?.secret"
                :value="issued.secret"
                :label="t.tokens.newToken"
                :note="t.tokens.validUntil(formatDateTime(issued.token.expiresAt))"
                :confirm-label="t.tokens.stored"
                :finished-text="t.tokens.finished"
                data-token-secret
                @done="finishCreate"
              />
              <div v-if="issuedForMcp" class="token-mcp" data-token-mcp>
                <CopyField v-if="catalog.mcpUrl" :label="t.tokens.mcpAddress" :value="catalog.mcpUrl" :copy-label="t.tokens.copyAddress" :value-attrs="{ 'data-token-mcp-url': '' }" />
                <p v-else class="st-note"><AtlasIcon name="info" :size="15" />{{ t.tokens.mcpAddressLater }}</p>
                <p class="token-help">{{ t.tokens.envHelp }}</p>
              </div>
            </div>
          </template>
        </ProgressiveFlow>
      </SettingsSection>

      <div v-else key="lists" class="token-lists">
        <SettingsSection v-if="mcpOn" :title="t.tokens.connectMcp" icon="plug" :description="t.tokens.connectMcpDescription" data-mcp-profile-guide>
          <p v-if="!catalog.canIssueTokens" class="st-note" role="note" data-mcp-permission><AtlasIcon name="lock" :size="15" />{{ t.tokens.needRead }}</p>
          <ol class="mcp-setup-steps">
            <li v-if="catalog.canIssueTokens">{{ t.tokens.mcpStepCreate }}</li>
            <li v-else>{{ t.tokens.mcpStepAfter }}</li>
            <li>{{ t.tokens.mcpStepTool }} <code class="st-mono">Authorization: Bearer &lt;{{ t.tokens.yourToken }}&gt;</code>{{ t.tokens.mcpStepToolEnd }}</li>
          </ol>
          <CopyField v-if="catalog.mcpUrl" :label="t.tokens.mcpAddress" :value="catalog.mcpUrl" :copy-label="t.tokens.copyAddress" :value-attrs="{ 'data-mcp-profile-url': '' }" />
          <p v-else class="st-note">{{ t.tokens.mcpAddressFailed }}</p>
          <p class="st-note">{{ t.tokens.claudeCodeFile }}</p>
        </SettingsSection>
        <ResultBanner v-if="result" :message="result" @dismiss="result = ''" />
        <EmptyState v-if="!catalog.canIssueTokens && !catalog.tokens.length && !mcpOn" compact icon="lock" :title="t.tokens.notAllowed" :text="t.tokens.notAllowedText" />
        <SettingsSection v-if="catalog.canIssueTokens || catalog.tokens.length" :title="t.tokens.active" icon="key-round" :count="active.length" :description="t.tokens.activeDescription(levelNote)">
          <!-- The actions are a toolbar line of the list, so they wrap under each other on a phone. -->
          <div class="st-actions token-toolbar">
            <Button v-if="catalog.canIssueTokens" size="sm" type="button" :disabled="busy" data-token-new @click="startCreate"><AtlasIcon name="plus" :size="15" />{{ t.tokens.newButton }}</Button>
            <Button variant="outline" size="sm" type="button" :aria-expanded="docsOpen" data-api-docs @click="openDocs"><AtlasIcon name="book-open" :size="15" />{{ t.tokens.docsButton }}</Button>
            <LoadingNote inline :active="loading" :label="t.tokens.loadingShort" />
          </div>
          <p v-if="!catalog.canIssueTokens" class="st-note" role="note"><AtlasIcon name="lock" :size="15" />{{ t.tokens.mustAllow }}</p>
          <SettingsList v-if="active.length" :label="t.tokens.active">
            <SettingsListRow v-for="token in active" :key="token.id" :title="token.name" icon="key-round" :highlighted="highlighted(token, 'active')" :data-token="token.id" :menu="busy ? undefined : [{ id: 'revoke', label: t.tokens.revoke, icon: 'ban', shortcut: 'Delete', destructive: true }]" :menu-label="t.tokens.revokeLabel(token.name)" @select="askRevoke(token)">
              <template #description><code class="st-mono">{{ token.prefix }}…</code> · {{ t.tokens.validTo }} {{ formatDateTime(token.expiresAt) }} · {{ rightsText(token.scopes, token.channels) }}<template v-if="unusableReason(token) || restriction(token)"><br /><span class="token-note" :data-token-state="unusableReason(token) ? 'unusable' : 'restricted'">{{ unusableReason(token) ?? restriction(token) }}</span></template></template>
              <template #meta>
                <StatusBadge v-bind="scopeBadge(token)" size="sm" data-token-scope />
                <StatusBadge v-for="channel in channelsOf(token)" :key="channel" tone="neutral" :icon="apiChannelInfo[channel].icon" :label="apiChannelInfo[channel].label" size="sm" data-token-channel />
                <StatusBadge v-if="unusableReason(token)" tone="warning" icon="ban" :label="t.tokens.unusableBadge" size="sm" data-token-usable="false" />
              </template>
              <template #actions>
                <Button variant="ghost" size="sm" type="button" :disabled="busy" :aria-label="t.tokens.revokeLabel(token.name)" data-token-revoke @click="askRevoke(token)"><AtlasIcon name="ban" :size="15" />{{ t.tokens.revoke }}</Button>
              </template>
            </SettingsListRow>
          </SettingsList>
          <EmptyState v-else compact icon="key-round" :title="t.tokens.noActive" :text="catalog.canIssueTokens ? t.tokens.noActiveCreate : t.tokens.noActiveWait" />
        </SettingsSection>

        <InlineReveal v-if="catalog.canIssueTokens || catalog.tokens.length" :open="docsOpen" class="api-docs-reveal">
        <div ref="docsSection" class="api-docs">
          <SettingsSection :title="t.tokens.docsTitle" icon="book-open" :description="t.tokens.docsDescription(catalog.apiBaseUrl)">
            <template #actions>
              <Button variant="ghost" size="sm" type="button" data-api-docs-close @click="closeDocs"><AtlasIcon name="close" :size="15" />{{ t.tokens.hide }}</Button>
            </template>
            <LoadingNote v-if="docsLoading" :label="t.tokens.docsLoading" />
            <ErrorState v-else-if="docsError" :title="t.tokens.docsFailed" :text="docsError" @retry="mountDocs" />
            <div :ref="setDocsHost" class="api-docs-reference" :hidden="docsLoading || !!docsError" data-api-docs-reference />
          </SettingsSection>
        </div>
        </InlineReveal>

        <InlineReveal :open="inactive.length > 0" class="api-inactive-reveal">
        <SettingsSection :title="t.tokens.inactive" icon="history" :count="inactive.length" :description="t.tokens.inactiveDescription">
          <SettingsList :label="t.tokens.inactive">
            <SettingsListRow v-for="token in inactive" :key="token.id" :title="token.name" icon="key-round" icon-tone="neutral" :highlighted="highlighted(token, 'inactive')" :data-token="token.id">
              <template #description><code class="st-mono">{{ token.prefix }}…</code> · {{ endedAt(token) }}</template>
              <template #meta><StatusBadge v-bind="scopeBadge(token)" size="sm" /><StatusBadge v-for="channel in channelsOf(token)" :key="channel" tone="neutral" :icon="apiChannelInfo[channel].icon" :label="apiChannelInfo[channel].label" size="sm" /><StatusBadge v-bind="stateBadge(token)" size="sm" /></template>
            </SettingsListRow>
          </SettingsList>
        </SettingsSection>
        </InlineReveal>

        <SettingsSection v-if="catalog.canIssueTokens || catalog.tokens.length" :title="t.tokens.usage" icon="code-xml" advanced :summary="t.tokens.usageSummary">
          <p class="token-help">{{ t.tokens.usageHeader }} <code class="st-mono">Authorization: Bearer</code>{{ t.tokens.usageHeaderEnd }}</p>
          <pre class="token-example st-mono">{{ example }}</pre>
          <p class="token-help">{{ t.tokens.usageApi }} <code class="st-mono">{{ catalog.apiBaseUrl }}</code> {{ t.tokens.usageApiEnd }}</p>
          <div v-if="catalog.openApiUrl === '/api/profile/api-document'" class="st-actions">
            <Button variant="outline" size="sm" as-child><a :href="catalog.openApiUrl" download="atlas-openapi.json"><AtlasIcon name="download" :size="15" />{{ t.tokens.downloadApi }}</a></Button>
          </div>
        </SettingsSection>
      </div>
    </Transition>

    <ConfirmDialog :open="!!revoking" :title="t.tokens.revokeTitle" :confirm-label="t.tokens.revokeConfirm" confirm-icon="ban" :busy="busy" :error="revokeError || undefined" @confirm="revoke" @cancel="revoking = undefined">
      <p>{{ t.tokens.tokenWord }} <strong>{{ revoking?.name }}</strong> {{ t.tokens.revokeText }}</p>
      <p>{{ t.tokens.revokeIrreversible }}</p>
    </ConfirmDialog>
  </div>
</template>

<style scoped>
.personal-api-tokens { display: grid; gap: 18px; min-width: 0; }
/* A column of flex items, not a grid: the result banner unfolds itself (ResultBanner) with the gap above it, which a grid track cannot give back. */
.token-lists { display: flex; flex-direction: column; gap: 18px; min-width: 0; }
/* An empty wrapper must not add a gap of its own. A folded InlineReveal is never :empty (its wrapper keeps the clip), so the
   reveals below give their gap back themselves. */
.token-lists > :empty { display: none; }
.token-lists > :deep(.result-banner) { margin: 0; }
.token-days { max-width: 140px; }
.token-error { display: flex; align-items: center; gap: 6px; margin: 0; color: var(--error); font-size: 13px; font-weight: 550; }
/* The one-time value arrives with a rise and its key pops; reduced motion keeps only the fade. */
.token-secret { min-width: 0; }
.token-secret :deep(.ots-head .st-icon-tile) { animation: st-pop var(--atlas-motion-slow) var(--atlas-ease-out) var(--st-stagger) both; }
.token-note { color: var(--warning-text, var(--muted)); font-weight: 550; }
.token-mcp { display: grid; gap: 10px; margin-top: 12px; }
.mcp-setup-steps { display: grid; gap: 8px; margin: 0; padding-left: 22px; color: var(--text); line-height: 1.5; }
.mcp-setup-steps li { padding-left: 4px; }
.token-help { margin: 0; color: var(--muted); font-size: 13px; line-height: 1.6; }
.api-docs { scroll-margin-top: 72px; }
/* The folded reveal has no height but still takes its gap in the column; the section's own lead takes its place, so a closed
   one takes nothing and opening grows the gap with the height (as the appearance picker's panels do). */
.api-docs-reveal, .api-inactive-reveal { margin-top: -18px; }
.api-docs-reveal > :deep(.inline-reveal > .inline-reveal-inner), .api-inactive-reveal > :deep(.inline-reveal > .inline-reveal-inner) { padding-top: 18px; }
.api-docs-reference { display: flex; flex-direction: column; height: min(760px, 75dvh); overflow: hidden; border: 1px solid var(--line); border-radius: var(--st-radius-sm); background: #fff; }
.api-docs-reference[hidden] { display: none; }
.api-docs-reference :deep(.md-openapi-frame) { display: block; flex: 1; min-height: 0; width: 100%; border: 0; }
.token-example { margin: 0; padding: 12px 14px; overflow-x: auto; border: 1px solid var(--line); border-radius: var(--st-radius-sm); background: color-mix(in srgb, var(--canvas) 55%, var(--surface)); font-size: 12.5px; white-space: pre-wrap; overflow-wrap: anywhere; }
</style>
