<script setup lang="ts">
import ct from "../i18n/components-b";
import { computed, nextTick, onMounted, onUnmounted, reactive, ref, watch } from "vue";
import { api, ApiError, errorMessage, type Appearance } from "../api";
import { formatDateTime } from "../formatDate";
import { locale, type LanguageChoice } from "../i18n";
import t from "../i18n/profile";
import AtlasVersionInfo from "./AtlasVersionInfo.vue";
import { profilePageOf, themeAccents, type ProfileAccount, type ProfileTab, type ReadingWidth } from "../profile";
import type { AuthMethods } from "../administration";
import { accessRequestApi } from "../secretRelease";
import { scrollBehavior } from "../motion/aiMotion";
import { lazyComponent } from "../lazyComponent";
import { useAiCapabilities } from "../composables/useAiCapabilities";
import AtlasIcon from "./AtlasIcon.vue";
import AiPreferencesSection from "./profile/AiPreferencesSection.vue";
import AccentPicker from "./profile/AccentPicker.vue";
import ThemePreview from "./profile/ThemePreview.vue";
import type { TokenCatalog } from "./auth/PersonalApiTokens.vue";
import LoadingSkeleton from "./LoadingSkeleton.vue";
import { skeletonOf } from "./loadingPlaceholders";
import UnsavedChangesDialog from "./UnsavedChangesDialog.vue";
import { Button } from "./ui/button";
import { Input } from "./ui/input";
import ChoiceCards from "./settings/ChoiceCards.vue";
import ConfirmDialog from "./settings/ConfirmDialog.vue";
import ConflictNote from "./settings/ConflictNote.vue";
import EmptyState from "./settings/EmptyState.vue";
import ErrorState from "./settings/ErrorState.vue";
import LoadingNote from "./settings/LoadingNote.vue";
import OptionPills from "./settings/OptionPills.vue";
import SaveBar from "./settings/SaveBar.vue";
import SettingsPage from "./settings/SettingsPage.vue";
import SettingsRow from "./settings/SettingsRow.vue";
import SettingsSection from "./settings/SettingsSection.vue";
import SettingsShell from "./settings/SettingsShell.vue";
import SummaryList from "./settings/SummaryList.vue";
import { Switch } from "./ui/switch";
import type { ChoiceOption, SettingsNavGroup, SettingsNavItem, SummaryItem } from "./settings/types";
import { usePageSwap } from "./settings/pageSwap";

const lazy = (loader: Parameters<typeof lazyComponent>[0]) => lazyComponent(loader, { loading: LoadingSkeleton });
// The security sections load in their own height (a section card: a header and a few rows), so the page below them does not jump.
const lazySection = (loader: Parameters<typeof lazyComponent>[0]) => lazyComponent(loader, { loading: skeletonOf({ rows: 3, rowHeight: "64px" }) });
const PasswordSettings = lazySection(() => import("./auth/PasswordSettings.vue"));
const IdentityConnections = lazySection(() => import("./auth/IdentityConnections.vue"));
const OtherSessions = lazySection(() => import("./profile/OtherSessions.vue"));
const PersonalApiTokens = lazySection(() => import("./auth/PersonalApiTokens.vue"));
const PersonalLibraryPanel = lazy(() => import("./PersonalLibraryPanel.vue"));
const ProfileActivity = lazy(() => import("./ProfileActivity.vue"));
const MyAccessRequests = lazy(() => import("./settings/requests/MyAccessRequests.vue"));
const MyAccess = lazy(() => import("./settings/access/MyAccess.vue"));
const ProfileHubStatus = lazy(() => import("./settings/access/ProfileHubStatus.vue"));
const hubAccessRefresh = ref(0);

/**
 * Profil, the third settings surface (`#/profile/<section>`) in the same SettingsShell as the wiki and
 * space settings. The application shell owns the address and the appearance (it previews it live and
 * persists it); this page owns the forms and asks before a section change would drop a draft.
 */
const props = withDefaults(defineProps<{
  tab: ProfileTab; appearance: Appearance; appearanceDirty: boolean; appearancePending: boolean; appearanceError: string;
  /** The label of the way back in the header of a narrow screen; the application shell knows where it leads. */
  backLabel?: string;
  /** The reading measure of documents; previewed with the rest of Vzhled and saved by the same button. */
  readingWidth?: ReadingWidth;
  /** The signed-in account for the header of Účet a vzhled: login name and the wiki role. */
  username?: string;
  isWikiAdmin?: boolean;
  /** The server's release ("4.0.0+commit"); shown in the rail footer as the link to Co je nového. */
  version?: string;
}>(), { readingWidth: "comfortable", username: "", isWikiAdmin: false });
const emit = defineEmits<{
  navigate: [tab: ProfileTab]; back: []; preview: [value: Appearance]; previewReadingWidth: [value: ReadingWidth]; saveAppearance: []; cancelAppearance: [];
  dirty: [value: boolean]; busy: [value: boolean]; accountSaved: [name: string]; sessionChanged: []; accessChanged: []; selectPage: [id: string, type?: "page" | "board"]; logout: []; openConversations: []; aiPreferencesSaved: [];
}>();

/**
 * The sections in the groups of board 12: Účet (Účet a vzhled, Zabezpečení, Předvolby AI), Přístup (Moje přístupy,
 * Moje žádosti, Osobní API tokeny) and Činnost (Aktivita, Historie návštěv). Vzhled has no entry of its own: it is the
 * second half of Účet a vzhled, and `#/profile/appearance` lands on that half.
 */
type SectionGroup = "account" | "access" | "activity";
const sectionGroups: { id: SectionGroup; label: string }[] = [{ id: "account", label: ct.profilePage.navAccount }, { id: "access", label: ct.profilePage.navAccess }, { id: "activity", label: ct.profilePage.navActivity }];
const sections: { id: ProfileTab; label: string; icon: string; description: string; group?: SectionGroup }[] = [
  { id: "account", label: ct.profilePage.accountAndAppearance, icon: "account", group: "account", description: ct.profilePage.accountDescription },
  { id: "security", label: ct.profilePage.security, icon: "shield-check", group: "account", description: ct.profilePage.securityDescription },
  { id: "ai", label: ct.profilePage.aiPreferences, icon: "lightbulb", group: "account", description: ct.profilePage.aiDescription },
  { id: "access", label: ct.profilePage.myAccess, icon: "key-round", group: "access", description: ct.profilePage.myAccessDescription },
  { id: "requests", label: ct.profilePage.myRequests, icon: "user-check", group: "access", description: ct.profilePage.myRequestsDescription },
  { id: "api", label: ct.profilePage.apiTokens, icon: "code-xml", group: "access", description: ct.profilePage.apiTokensDescription },
  { id: "activity", label: ct.profilePage.activity, icon: "activity", group: "activity", description: ct.profilePage.activityDescription },
  { id: "visits", label: ct.profilePage.visits, icon: "history", group: "activity", description: ct.profilePage.visitsDescription },
  { id: "appearance", label: ct.profilePage.navAppearance, icon: "palette", description: ct.profilePage.appearanceDescription },
];
// The AI section exists only while an AI capability is available (AI-24). If the AI disappears during an edit, the open
// section stays on stage until the user leaves it, but the navigation no longer offers it; the preferences are the
// user's data and still save.
const { any: aiAvailable } = useAiCapabilities();
// This owned-token catalog is authoritative for both navigation and the API/MCP page. A denied account may still
// have older tokens to inspect and revoke, so a permission flag alone cannot decide whether to show the entry.
const apiCatalog = ref<TokenCatalog>();
const apiCatalogLoading = ref(true), apiCatalogError = ref("");
let apiCatalogRequest: AbortController | undefined;
async function loadApiCatalog() {
  apiCatalogRequest?.abort();
  const current = apiCatalogRequest = new AbortController();
  apiCatalogLoading.value = true;
  apiCatalogError.value = "";
  try {
    const result = await api<TokenCatalog>("/profile/api-tokens", { signal: current.signal });
    if (!current.signal.aborted) apiCatalog.value = result;
  } catch (failure) {
    if (!current.signal.aborted) apiCatalogError.value = errorMessage(failure);
  } finally {
    if (!current.signal.aborted) apiCatalogLoading.value = false;
  }
}
onMounted(() => { void loadApiCatalog(); });
const navSections = computed(() => sections.flatMap((item) => {
  if (!item.group) return [];
  if (item.id === "ai" && !aiAvailable.value) return [];
  if (item.id === "api" && apiCatalog.value && !apiCatalog.value.mcpEnabled && !apiCatalog.value.canIssueTokens && !apiCatalog.value.tokens.length) return [];
  if (item.id !== "api" || !apiCatalog.value?.mcpEnabled) return [item];
  return [{ ...item, label: apiCatalog.value.canIssueTokens ? "API a MCP" : "MCP", description: ct.profilePage.mcpDescription }];
}));
/** Účet and Vzhled are one page (Účet a vzhled); every other section is a page of its own. */
const pageTab = computed(() => profilePageOf(props.tab));
// The sections swap with the shared page hooks (settings/pageSwap.ts): the new one starts at the top, the old one fades where the reader saw it.
const swap = usePageSwap(pageTab);
const accountPage = computed(() => pageTab.value === "account");
const section = computed(() => navSections.value.find((item) => item.id === pageTab.value) ?? sections.find((item) => item.id === pageTab.value) ?? sections[0]!);
const pathOf = (tab: ProfileTab) => `/profile/${tab}`;
// Moje žádosti carries the number of requests still waiting for a decision (SettingsNav leaves a zero out).
const pendingRequests = ref(0);
let requestsDisposed = false;
async function loadPendingRequests() {
  try {
    const page = await accessRequestApi.mine();
    if (!requestsDisposed) pendingRequests.value = page.items.filter((item) => item.state === "pending").length;
  } catch { /* Only a hint in the navigation; {{ ct.profilePage.myRequests }} shows the list and its own error. */ }
}
onMounted(() => { void loadPendingRequests(); });
watch(() => props.tab, (tab, previous) => { if (previous === "requests" && tab !== "requests") void loadPendingRequests(); });
onUnmounted(() => { requestsDisposed = true; });

// Účet
const account = ref<ProfileAccount>();
// `error`: the account could not be loaded; `conflict`: a save met a newer version (409); `saveError`: any other refusal.
const name = ref(""), email = ref(""), error = ref(""), conflict = ref(""), saveError = ref("");
const loading = ref(false), pending = ref(false);
const changed = computed(() => !!account.value && (name.value !== account.value.displayName || email.value !== (account.value.email ?? "")));
const childDirty = reactive({ password: false, identities: false, tokens: false, ai: false });
const childBusy = reactive({ password: false, identities: false, tokens: false, ai: false });
const aiSection = ref<InstanceType<typeof AiPreferencesSection>>();
const busy = computed(() => pending.value || Object.values(childBusy).some(Boolean));
watch(() => changed.value || Object.values(childDirty).some(Boolean), (value) => emit("dirty", value), { flush: "sync" });
watch(busy, (value) => emit("busy", value), { flush: "sync" });
let request: AbortController | undefined;
async function loadAccount() {
  request?.abort();
  const current = (request = new AbortController());
  loading.value = true;
  error.value = "";
  conflict.value = "";
  saveError.value = "";
  try {
    const result = await api<ProfileAccount>("/profile/account", { signal: current.signal });
    if (current.signal.aborted) return;
    account.value = result;
    name.value = result.displayName;
    email.value = result.email ?? "";
  } catch (failure) {
    if (!current.signal.aborted) error.value = errorMessage(failure);
  } finally {
    if (!current.signal.aborted) loading.value = false;
  }
}
watch(
  () => props.tab,
  (tab) => {
    if ((tab === "account" || tab === "appearance" || tab === "security") && !account.value && !loading.value) void loadAccount();
  },
  { immediate: true },
);
// A fresh mount per section used to read the account again every time one that shows it was entered; the account that is
// already here stays on screen meanwhile (a quiet refresh, no loading line).
watch(pageTab, (page) => {
  if (account.value && !loading.value && !pending.value && (page === "account" || page === "security")) void loadAccount();
});
async function saveAccount() {
  if (!account.value || pending.value || loading.value || !changed.value) return;
  const current = (request = new AbortController());
  pending.value = true;
  conflict.value = "";
  saveError.value = "";
  try {
    const result = await api<ProfileAccount>("/profile/account", {
      method: "PUT",
      signal: current.signal,
      body: JSON.stringify({ displayName: name.value.trim(), email: email.value.trim() || null, expectedVersion: account.value.version }),
    });
    if (current.signal.aborted) return;
    account.value = result;
    name.value = result.displayName;
    email.value = result.email ?? "";
    emit("accountSaved", result.displayName);
  } catch (failure) {
    if (current.signal.aborted) return;
    if (failure instanceof ApiError && failure.status === 409) conflict.value = ct.profilePage.conflict;
    else saveError.value = errorMessage(failure);
  } finally {
    if (!current.signal.aborted) pending.value = false;
  }
}
function cancelAccount() {
  if (!account.value || pending.value || loading.value) return;
  name.value = account.value.displayName;
  email.value = account.value.email ?? "";
  saveError.value = "";
}
// Reloading over a draft asks first (ConfirmDialog), never window.confirm.
const confirmReload = ref(false);
function reloadAccount() {
  if (pending.value || loading.value) return;
  if (changed.value) confirmReload.value = true;
  else void loadAccount();
}
function reloadDiscarding() {
  confirmReload.value = false;
  void loadAccount();
}
const sessionFacts = computed<SummaryItem[]>(() => {
  const current = account.value;
  if (!current) return [];
  const time = (value: string | null) => formatDateTime(value) || ct.profilePage.notAvailable;
  const method = (value: string | null) => (value === "builtin" ? ct.profilePage.methodBuiltin : value ?? ct.profilePage.notAvailable);
  return [
    { key: "current", icon: "fingerprint", label: ct.profilePage.thisSignIn, value: `${time(current.currentSessionAuthenticatedAt)} · ${method(current.currentSessionAuthenticationMethod)}` },
    { key: "last", icon: "history", label: ct.profilePage.lastSignIn, value: `${time(current.lastSignInAt)} · ${method(current.lastSignInMethod)}` },
  ];
});
// The header of Účet a vzhled (board 12): initials, the saved name, login · kind of account · role, and the way to
// link a company account where the wiki has a provider to link (Zabezpečení → Propojená přihlášení).
const initialsOf = (value: string) => {
  const words = value.trim().split(/\s+/).filter(Boolean);
  return (words.length > 1 ? words.slice(0, 2).map((word) => word[0]).join("") : value.trim().slice(0, 2)).toLocaleUpperCase(locale);
};
const identityName = computed(() => account.value?.displayName ?? "");
const identityMeta = computed(() => {
  const source = account.value?.source;
  const kind = source === "builtin" ? ct.profilePage.kindLocal : source === "linked" ? ct.profilePage.kindLinked : source === "external" ? ct.profilePage.kindExternal : "";
  return [props.username, kind, props.isWikiAdmin ? ct.profilePage.wikiAdmin : ""].filter(Boolean).join(" · ");
});
const linkProviders = ref(false);
async function loadLinkProviders() {
  try {
    linkProviders.value = (await api<AuthMethods>("/auth/methods")).providers.length > 0;
  } catch { linkProviders.value = false; }
}
const offerLink = computed(() => linkProviders.value && account.value?.source === "builtin");
watch(accountPage, (value) => { if (value && !linkProviders.value) void loadLinkProviders(); }, { immediate: true });
onUnmounted(() => {
  apiCatalogRequest?.abort();
  request?.abort();
  emit("dirty", false);
  emit("busy", false);
});

// Vzhled: a live preview through the application shell; saving is explicit.
const appearance = reactive<Appearance>({ ...props.appearance, motion: props.appearance.motion ?? "system" });
watch(() => props.appearance, (value) => Object.assign(appearance, { ...value, motion: value.motion ?? "system" }), { deep: true });
watch(appearance, (value) => emit("preview", { ...value }), { deep: true });
const text = t.appearance;
const modeOptions: ChoiceOption[] = [
  { value: "light", label: text.mode.light },
  { value: "dark", label: text.mode.dark },
  { value: "system", label: text.mode.system },
];
const layoutOptions: ChoiceOption[] = [
  { value: "connected", label: text.layout.connected, description: text.layout.connectedHint, icon: "layers" },
  { value: "islands", label: text.layout.islands, description: text.layout.islandsHint, icon: "layout-dashboard" },
];
const readingOptions: ChoiceOption[] = [
  { value: "comfortable", label: text.reading.comfortable, icon: "align-center" },
  { value: "wide", label: text.reading.wide, icon: "maximize-2" },
];
// Jazyk (4.1): saved with the appearance; the application reloads in the new language (App.vue saveProfile).
const languageOptions: ChoiceOption[] = [
  { value: "auto", label: text.language.auto, icon: "globe" },
  { value: "cs", label: text.language.cs },
  { value: "en", label: text.language.en },
];
const languageChoice = computed({ get: () => appearance.language ?? "auto", set: (value: string) => (appearance.language = value as LanguageChoice) });
const layout = computed({ get: () => appearance.layout, set: (value) => (appearance.layout = value as Appearance["layout"]) });
const accent = computed({ get: () => appearance.accent, set: (value) => (appearance.accent = value) });
const mode = computed({ get: () => appearance.mode, set: (value) => (appearance.mode = value as Appearance["mode"]) });
// „Stránky otevírat“ is the stored `openLocked` (3.6) as a choice of two (owner 2026-11-07); rovnou k úpravám stays the default.
const openModeOptions: ChoiceOption[] = [
  { value: "edit", label: text.openMode.edit, icon: "pencil" },
  { value: "read", label: text.openMode.read, icon: "lock" },
];
const openMode = computed({ get: () => (appearance.openLocked ? "read" : "edit"), set: (value: string) => (appearance.openLocked = value === "read") });
const dragPages = computed({ get: () => appearance.dragPages !== false, set: (value: boolean) => (appearance.dragPages = value) });
const reduceMotion = computed({ get: () => appearance.motion === "reduce", set: (value: boolean) => (appearance.motion = value ? "reduce" : "system") });
const readingWidth = computed({ get: () => props.readingWidth, set: (value) => emit("previewReadingWidth", value as ReadingWidth) });
// The previews draw every palette from palettes.css in the mode being previewed; Systém follows the device.
const colorQuery = typeof window.matchMedia === "function" ? window.matchMedia("(prefers-color-scheme: dark)") : undefined;
const systemDark = ref(colorQuery?.matches ?? false);
const systemChanged = (event: MediaQueryListEvent) => (systemDark.value = event.matches);
colorQuery?.addEventListener?.("change", systemChanged);
onUnmounted(() => colorQuery?.removeEventListener?.("change", systemChanged));
const previewMode = computed(() => (appearance.mode === "system" ? (systemDark.value ? "dark" : "light") : appearance.mode));
// Omezit animace: off follows the device, so the row says what the device asks for.
const motionQuery = typeof window.matchMedia === "function" ? window.matchMedia("(prefers-reduced-motion: reduce)") : undefined;
const systemReduced = ref(motionQuery?.matches ?? false);
const motionChanged = (event: MediaQueryListEvent) => (systemReduced.value = event.matches);
motionQuery?.addEventListener?.("change", motionChanged);
onUnmounted(() => motionQuery?.removeEventListener?.("change", motionChanged));
const motionState = computed(() => (reduceMotion.value ? text.on : text.motion.system(systemReduced.value)));
// `#/profile/appearance` lands on the Vzhled half: once the page has its focus and the account above it has its
// height (loaded or failed), it scrolls there.
const appearanceBlock = ref<HTMLElement>();
function revealAppearance() {
  if (props.tab !== "appearance" || loading.value) return;
  void nextTick(() => requestAnimationFrame(() => requestAnimationFrame(() => {
    if (props.tab !== "appearance") return;
    appearanceBlock.value?.scrollIntoView?.({ block: "start", behavior: scrollBehavior(appearanceBlock.value) });
  })));
}
onMounted(revealAppearance);
watch(() => props.tab, revealAppearance);
watch(loading, (value, was) => { if (was && !value) revealAppearance(); });
// Historie návštěv: the page's own Aktualizovat reloads the embedded list.
const visitsRefresh = ref(0);

// Leaving a section or the profile with a draft asks explicitly, with a real save where there is one.
const sectionDirty = computed(() => {
  if (accountPage.value) return changed.value || props.appearanceDirty;
  if (props.tab === "security") return childDirty.password || childDirty.identities;
  if (props.tab === "api") return childDirty.tokens;
  if (props.tab === "ai") return childDirty.ai;
  return false;
});
const canSaveSection = computed(() => accountPage.value || props.tab === "ai");
// Without an AI capability a section that holds no draft has nothing to show any more.
watch(aiAvailable, (value) => { if (!value && props.tab === "ai" && !childDirty.ai && !locked.value) emit("navigate", "account"); });
const locked = computed(() => busy.value || props.appearancePending);
type PendingNav = { type: "navigate"; tab: ProfileTab } | { type: "back" };
const pendingNav = ref<PendingNav | null>(null);
const dialogSaving = ref(false);
const groups = computed<SettingsNavGroup[]>(() => sectionGroups
  .map((group) => ({
    id: group.id,
    label: group.label,
    items: navSections.value.filter((item) => item.group === group.id).map((item) => ({
      path: pathOf(item.id), label: item.label, icon: item.icon, disabled: locked.value,
      ...(item.id === "requests" && pendingRequests.value ? { count: pendingRequests.value, countLabel: ct.profilePage.awaitingDecision } : {}),
    })),
  }))
  .filter((group) => group.items.length));
function guardedNavigate(tab: ProfileTab) {
  if (locked.value || tab === props.tab) return;
  // Účet and Vzhled share a page: moving between its halves keeps both drafts.
  if (profilePageOf(tab) === pageTab.value) {
    emit("navigate", tab);
    return;
  }
  if (sectionDirty.value) {
    pendingNav.value = { type: "navigate", tab };
    return;
  }
  emit("navigate", tab);
}
// The shell's links keep their address (new tab, copy link); a click goes through the guard instead.
function navGuard(item: SettingsNavItem) {
  const tab = sections.find((entry) => pathOf(entry.id) === item.path)?.id;
  if (tab) guardedNavigate(tab);
  return false;
}
function guardedBack() {
  if (locked.value) return;
  if (sectionDirty.value) {
    pendingNav.value = { type: "back" };
    return;
  }
  emit("back");
}
function proceedPending() {
  const nav = pendingNav.value;
  pendingNav.value = null;
  if (!nav) return;
  if (nav.type === "navigate") emit("navigate", nav.tab);
  else emit("back");
}
function discardAndLeave() {
  if (accountPage.value) {
    cancelAccount();
    if (props.appearanceDirty) emit("cancelAppearance");
  } else if (props.tab === "security") {
    childDirty.password = false;
    childDirty.identities = false;
  } else if (props.tab === "api") childDirty.tokens = false;
  else if (props.tab === "ai") aiSection.value?.discard();
  proceedPending();
}
function waitAppearanceSaved(): Promise<void> {
  return new Promise((resolve) => {
    const stop = watch(
      () => props.appearancePending,
      (value) => {
        if (!value) {
          stop();
          resolve();
        }
      },
    );
  });
}
async function saveAndLeave() {
  dialogSaving.value = true;
  try {
    if (accountPage.value) {
      // Both halves save as their own bars do: the account first, then the appearance; a refusal stays here.
      if (changed.value) {
        await saveAccount();
        if (conflict.value || saveError.value) return;
      }
      if (props.appearanceDirty) {
        emit("saveAppearance");
        await waitAppearanceSaved();
        if (props.appearanceError) return;
      }
    } else if (props.tab === "ai") {
      if (!(await aiSection.value?.save())) return;
    }
    // Security and API drafts live inside their own forms; leaving discards them like "Zahodit".
  } finally {
    dialogSaving.value = false;
  }
  proceedPending();
}
</script>

<template>
  <section class="profile-page" aria-labelledby="profile-heading">
    <SettingsShell
      :title="ct.profilePage.profile"
      title-id="profile-heading"
      :eyebrow="ct.profilePage.eyebrow"
      :subtitle="ct.profilePage.subtitle"
      icon="account"
      :groups="groups"
      :nav-label="ct.profilePage.navLabel"
      :current-path="pathOf(pageTab)"
      :guard="navGuard"
    >
      <template #actions>
        <Button variant="outline" type="button" data-profile-back :disabled="locked" @click="guardedBack"><AtlasIcon name="back" :size="16" />{{ backLabel ?? ct.profilePage.back }}</Button>
      </template>
      <!-- Sign-out belongs to the account, so it sits under the profile sections rather than in the wiki sidebar.
           The shell asks about unsaved changes before it signs out. -->
      <template #rail-footer>
        <div class="profile-rail-footer">
          <AtlasVersionInfo class="profile-rail-version" :version="version" href="#/whats-new" data-profile-whats-new />
          <Button variant="ghost" size="sm" type="button" class="profile-rail-action" data-profile-logout :disabled="locked" @click="emit('logout')"><AtlasIcon name="logout" :size="16" />{{ ct.profilePage.signOut }}</Button>
        </div>
      </template>

      <!-- A section leaves with a fade while the next one rises in over it (settings.css, st-page), as in Správa wiki, and
           starts at its top. Only the keyed page is wrapped; what its slots receive is unchanged. The application keeps
           this page mounted between sections (App.vue openProfile), so the account is read again when a section that
           shows it is entered, as a fresh mount did. -->
      <Transition name="st-page" @before-enter="swap.beforeEnter" @before-leave="swap.beforeLeave">
      <SettingsPage :key="pageTab" :title="section.label" :icon="section.icon" :description="section.description" :class="{ 'profile-account-page': accountPage }" :data-profile-section="accountPage ? undefined : tab">
        <template v-if="tab === 'visits'" #actions>
          <Button variant="outline" size="sm" type="button" data-visits-refresh @click="visitsRefresh++"><AtlasIcon name="refresh" :size="15" />{{ ct.profilePage.refresh }}</Button>
        </template>
        <template v-if="accountPage">
          <!-- Účet a vzhled (board 12): who you are, then two sections with two-column rows. Each half keeps its own
               explicit save; the unsaved-changes question covers both. -->
          <header class="profile-identity" data-profile-identity>
            <span class="profile-identity-avatar" aria-hidden="true">{{ initialsOf(identityName || username) }}</span>
            <div class="profile-identity-text">
              <p class="profile-identity-name">{{ identityName || username }}</p>
              <p v-if="identityMeta" class="profile-identity-meta">{{ identityMeta }}</p>
            </div>
            <Button v-if="offerLink" variant="outline" type="button" class="profile-identity-link" data-profile-link :disabled="locked" @click="guardedNavigate('security')"><AtlasIcon name="link" :size="15" />{{ ct.profilePage.linkCompany }}</Button>
          </header>
          <div class="profile-block" data-profile-section="account">
            <LoadingNote v-if="loading && !account" :label="ct.profilePage.loadingAccount" />
            <ErrorState v-if="error" :title="ct.profilePage.accountFailed" :text="error" :busy="loading" @retry="reloadAccount" />
            <ConflictNote v-if="conflict" :text="conflict" :reload-label="ct.profilePage.reloadLabel" :busy="loading || pending" data-account-conflict @reload="reloadAccount" @dismiss="conflict = ''" />
            <SettingsSection v-if="account" :title="ct.profilePage.navAccount">
              <SettingsRow v-slot="{ id, describedBy }" icon="account" :label="ct.profilePage.displayName" :help="account.canEditDisplayName ? ct.profilePage.nameHelpEditable : ct.profilePage.nameHelpManaged">
                <Input :id="id" v-model="name" autocomplete="name" required maxlength="200" :readonly="!account.canEditDisplayName" :aria-describedby="describedBy" :disabled="pending || loading" data-profile-name />
              </SettingsRow>
              <SettingsRow v-slot="{ id, describedBy }" icon="megaphone" :label="ct.profilePage.contactEmail" :help="ct.profilePage.contactEmailHelp">
                <Input :id="id" v-model="email" type="email" autocomplete="email" maxlength="254" :placeholder="ct.profilePage.emailPlaceholder" :readonly="!account.canEditEmail" :aria-describedby="describedBy" :disabled="pending || loading" />
              </SettingsRow>
              <p v-if="account.source !== 'builtin'" class="st-note"><AtlasIcon name="info" :size="15" />{{ ct.profilePage.externalNote }}</p>
            </SettingsSection>
            <SaveBar :dirty="changed" :saving="pending" :error="saveError || undefined" :save-label="ct.profilePage.saveDetails" @save="saveAccount" @cancel="cancelAccount" />
          </div>
          <div ref="appearanceBlock" class="profile-block" data-profile-section="appearance">
            <SettingsSection :title="text.title">
              <SettingsRow icon="globe" :label="text.language.label" :help="text.language.help" :label-for="false">
                <OptionPills v-model="languageChoice" :options="languageOptions" :label="text.language.label" hide-label :disabled="appearancePending" data-appearance="language" />
              </SettingsRow>
              <SettingsRow icon="moon" :label="text.mode.label" :help="text.mode.help" :label-for="false">
                <ChoiceCards v-model="mode" :options="modeOptions" :label="text.mode.label" hide-label :columns="3" :disabled="appearancePending" class="appearance-themes" data-appearance="mode">
                  <template #option="{ option }"><ThemePreview :mode="option.value as Appearance['mode']" :accent="appearance.accent" /></template>
                </ChoiceCards>
              </SettingsRow>
              <SettingsRow icon="palette" :label="text.accent.label" :help="text.accent.help" :label-for="false">
                <AccentPicker v-model="accent" :accents="themeAccents" :mode="previewMode" :label="text.accent.label" :disabled="appearancePending" data-appearance="accent" />
              </SettingsRow>
              <SettingsRow icon="layers" :label="text.layout.label" :help="text.layout.help" :label-for="false">
                <ChoiceCards v-model="layout" :options="layoutOptions" :label="text.layout.label" hide-label :columns="2" :disabled="appearancePending" data-appearance="layout">
                  <template #option="{ option }"><span class="layout-preview" :class="option.value" aria-hidden="true"><i /><i /></span></template>
                </ChoiceCards>
              </SettingsRow>
              <SettingsRow icon="align-center" :label="text.reading.label" :help="text.reading.help" :label-for="false">
                <OptionPills v-model="readingWidth" :options="readingOptions" :label="text.reading.label" hide-label :disabled="appearancePending" data-appearance="reading-width" />
              </SettingsRow>
              <SettingsRow v-slot="{ id, describedBy }" icon="activity" :label="text.motion.label" :help="text.motion.help">
                <!-- The switch says its state in words (.st-switch-pill), and off names what the device asks for. -->
                <label class="st-switch-pill appearance-motion">
                  <Switch :id="id" v-model="reduceMotion" :aria-describedby="describedBy" :disabled="appearancePending" data-appearance="motion" />
                  <span class="appearance-motion-state">{{ motionState }}</span>
                </label>
              </SettingsRow>
              <SettingsRow icon="lock" :label="text.openMode.label" :help="text.openMode.help" :label-for="false">
                <OptionPills v-model="openMode" :options="openModeOptions" :label="text.openMode.label" hide-label :disabled="appearancePending" data-appearance="open-mode" />
              </SettingsRow>
              <SettingsRow v-slot="{ id, describedBy }" icon="grip-vertical" :label="text.dragPages.label" :help="text.dragPages.help">
                <label class="st-switch-pill">
                  <Switch :id="id" v-model="dragPages" :aria-describedby="describedBy" :disabled="appearancePending" data-appearance="drag-pages" />
                  <span class="appearance-motion-state">{{ dragPages ? text.on : text.off }}</span>
                </label>
              </SettingsRow>
            </SettingsSection>
            <SaveBar :dirty="appearanceDirty" :saving="appearancePending" :error="appearanceError || undefined" :save-label="text.save" @save="emit('saveAppearance')" @cancel="emit('cancelAppearance')" />
          </div>
        </template>

        <template v-else-if="tab === 'security'">
          <LoadingNote v-if="loading && !account" :label="ct.profilePage.loadingAccount" />
          <ErrorState v-if="error" :title="ct.profilePage.accountFailed" :text="error" :busy="loading" @retry="reloadAccount" />
          <SettingsSection :title="ct.profilePage.signIn" icon="fingerprint" :description="ct.profilePage.signInDescription">
            <SummaryList v-if="account" :items="sessionFacts" :label="ct.profilePage.signIn" />
          </SettingsSection>
          <PasswordSettings @changed="emit('sessionChanged')" @dirty="childDirty.password = $event" @busy="childBusy.password = $event" />
          <IdentityConnections @dirty="childDirty.identities = $event" @busy="childBusy.identities = $event" />
          <OtherSessions @changed="emit('sessionChanged')" />
        </template>

        <template v-else-if="tab === 'access'">
          <ProfileHubStatus @verified="hubAccessRefresh++; emit('accessChanged')" />
          <MyAccess :key="hubAccessRefresh" />
        </template>
        <MyAccessRequests v-else-if="tab === 'requests'" />
        <PersonalLibraryPanel v-else-if="tab === 'visits'" tab="recent" embedded :refresh-key="visitsRefresh" @select="(id: string, type: 'page' | 'board') => emit('selectPage', id, type)">
          <template #empty="{ text }"><EmptyState icon="history" :title="ct.profilePage.noVisits" :text="text" /></template>
          <template #error="{ message, busy: retrying, retry }"><ErrorState :title="ct.profilePage.visitsFailed" :text="message" :busy="retrying" @retry="retry" /></template>
        </PersonalLibraryPanel>
        <ProfileActivity v-else-if="tab === 'activity'" @select="emit('selectPage', $event)" />

        <AiPreferencesSection v-else-if="tab === 'ai'" ref="aiSection" @open-conversations="emit('openConversations')" @dirty="childDirty.ai = $event" @busy="childBusy.ai = $event" @saved="emit('aiPreferencesSaved'); emit('sessionChanged')" />

        <LoadingNote v-else-if="apiCatalogLoading && !apiCatalog" :label="ct.profilePage.loadingApi" />
        <ErrorState v-else-if="apiCatalogError && !apiCatalog" :title="ct.profilePage.apiFailed" :text="apiCatalogError" :busy="apiCatalogLoading" @retry="loadApiCatalog" />
        <PersonalApiTokens v-else :initial-catalog="apiCatalog" @catalog-updated="apiCatalog = $event" @dirty="childDirty.tokens = $event" @busy="childBusy.tokens = $event" />
      </SettingsPage>
      </Transition>
    </SettingsShell>

    <UnsavedChangesDialog :open="pendingNav !== null" :saving="dialogSaving" :can-save="canSaveSection" @stay="pendingNav = null" @discard="discardAndLeave" @save="saveAndLeave" />
    <ConfirmDialog :open="confirmReload" :title="ct.profilePage.reloadTitle" :confirm-label="ct.profilePage.reloadConfirm" confirm-icon="refresh" @confirm="reloadDiscarding" @cancel="confirmReload = false">
      <p>{{ ct.profilePage.reloadText }}</p>
    </ConfirmDialog>
  </section>
</template>

<style scoped>
.profile-page { min-width: 0; width: 100%; }
/* The foot of the rail: Odhlásit se under a quiet rule (board 12). The rule belongs to the row, so the button itself
   keeps the base Button's height, radius, hover, focus and press instead of the flat 36 px block refresh.css gave a direct child. */
.profile-rail-footer { margin-top: auto; padding-top: 8px; border-top: 1px solid var(--refresh-soft-line); }
.profile-rail-version { padding: 0 10px; }
.profile-rail-action { width: 100%; justify-content: flex-start; color: var(--muted); }
/* Účet a vzhled: the identity header takes the place of the page title, which stays for assistive technology. */
.profile-account-page :deep(.settings-page-header) { position: absolute; width: 1px; height: 1px; margin: -1px; padding: 0; overflow: hidden; clip-path: inset(50%); border: 0; white-space: nowrap; }
.profile-identity { display: flex; flex-wrap: wrap; align-items: center; gap: 12px 16px; min-width: 0; animation: st-rise-in var(--atlas-motion-slow) var(--atlas-ease-out) backwards; }
.profile-identity-avatar { display: grid; place-items: center; width: 56px; height: 56px; flex: none; border-radius: 50%; background: var(--accent-soft); color: var(--accent); font-size: 18px; font-weight: 700; letter-spacing: .01em; transition: background-color var(--atlas-motion-slow) var(--atlas-ease-out), color var(--atlas-motion-slow) var(--atlas-ease-out); }
.profile-identity-text { flex: 1 1 220px; display: grid; gap: 2px; min-width: 0; }
.profile-identity-name { margin: 0; font-size: 24px; line-height: 1.25; font-weight: 650; letter-spacing: -.015em; overflow-wrap: anywhere; }
.profile-identity-meta { margin: 0; color: var(--muted); font-size: 14px; }
.profile-identity-link { flex: none; }
/* Each half rises in after the header, the second a beat later; a deep link to Vzhled lands under the top bar. */
.profile-block { display: grid; gap: 18px; min-width: 0; scroll-margin-top: calc(var(--atlas-topbar-height, 52px) + 16px); animation: st-rise-in var(--atlas-motion-slow) var(--atlas-ease-out) backwards; animation-delay: calc(var(--atlas-motion-fast) / 3); }
.profile-block + .profile-block { animation-delay: calc(var(--atlas-motion-fast) * 2 / 3); }
/* Small previews inside the appearance tiles, drawn from the current tokens; colours change with a fade. */
.layout-preview { display: flex; width: 100%; height: 54px; overflow: hidden; border: 1px solid var(--line); border-radius: var(--atlas-radius-sm); background: var(--canvas); transition: background-color var(--atlas-motion-base) var(--atlas-ease-out), border-color var(--atlas-motion-base) var(--atlas-ease-out); }
.layout-preview > i { transition: background-color var(--atlas-motion-base) var(--atlas-ease-out), border-color var(--atlas-motion-base) var(--atlas-ease-out); }
.layout-preview > i:first-child { width: 29%; background: var(--accent-soft); border-right: 1px solid var(--line); }
.layout-preview > i:last-child { flex: 1; background: var(--surface); }
.layout-preview.islands { gap: 6px; padding: 6px; }
.layout-preview.islands > i { border: 1px solid var(--line); border-radius: calc(var(--atlas-radius-sm) / 2); }
/* Motiv: the preview leads the card, the radio and the title follow below it; the chosen card is tinted (board 12). */
.appearance-themes :deep(.choice-tile [data-slot="field"]) { flex-wrap: wrap; align-items: center; padding: 8px; gap: 8px; }
.appearance-themes :deep(.choice-tile-title) { font-weight: 500; }
.appearance-themes :deep(.choice-tile[data-selected] .choice-tile-title) { font-weight: 650; }
.appearance-themes :deep(.choice-tile-content) { display: contents; }
.appearance-themes :deep(.choice-extra) { order: -1; flex: 1 0 100%; margin: 0; }
.appearance-themes :deep(.choice-tile-control) { order: 0; margin: 0 0 0 2px; }
.appearance-themes :deep(.choice-tile-title) { order: 1; flex: 1; min-width: 0; font-size: 14px; }
.appearance-themes :deep(.choice-tile-description) { order: 2; flex: 1 0 100%; padding-left: 2px; }
/* Three small cards side by side even on a phone; there the miniature says what the description would. */
.appearance-themes :deep(.choice-grid) { grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 8px; }
@container (max-width: 519px) {
  .appearance-themes :deep(.choice-tile-description) { display: none; }
  .appearance-themes :deep(.choice-tile [data-slot="field"]) { padding: 8px; gap: 6px 8px; }
  .appearance-themes :deep(.choice-tile-title) { font-size: 13px; }
}
.appearance-motion { justify-self: start; }
.appearance-motion-state { color: var(--muted); }
</style>
