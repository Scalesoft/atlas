<script setup lang="ts">
// First run and the new-space dialog are rare: they load on demand and stay out of the eager bundle.
const SetupWizard = lazyComponent(() => import("./components/auth/SetupWizard.vue"));
import AiQuotaStatus from "./components/ai/AiQuotaStatus.vue";
import AtlasBrandLogo from "./components/AtlasBrandLogo.vue";
import LoginLayout from "./components/auth/LoginLayout.vue";
import AtlasVersionInfo from "./components/AtlasVersionInfo.vue";
import { Badge } from "./components/ui/badge";
import { Button } from "./components/ui/button";
import { Toaster } from "./components/ui/sonner";
import { useToast } from "./composables/useToast";
import { markReleaseNotesSeen, releaseLine, releaseOf, showWhatsNew, whatsNewNotice } from "./whatsNew/notice";
import type { ReleaseNote } from "./whatsNew/releaseNotes";
import { useAiCapabilities } from "./composables/useAiCapabilities";
import { useModules } from "./composables/useModules";
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from "./components/ui/tooltip";
import { Input } from "./components/ui/input";
import { browserStorage, readStored, writeStored } from "./lib/browserStorage";
import { setLexiconAccount } from "./linking/lexicon";
import { unlinkedApi } from "./linking/unlinkedApi";
import { fieldElement, hostElement } from "./lib/templateRef";
import { switchSearchInputMode } from "./search/searchInputMode";
import {
  computed,
  defineComponent,
  nextTick,
  onMounted,
  onUnmounted,
  watchEffect,
  reactive,
  ref,
  shallowRef,
  watch,
  type ComponentPublicInstance,
} from "vue";
import { useElementVisibility } from "@vueuse/core";
import {
  api,
  ApiError,
  clearCsrf,
  errorMessage,
  dropEarlyReads,
  onSessionEnded,
  post,
  put,
  readPageBundle,
  type Appearance,
  type Page,
  type PageSummary,
  type SearchResult,
  type SearchWidget,
  type SetupStatus,
  type Space,
  type SpacePinPolicy,
  type User,
} from "./api";
import { personalLibraryApi, type PersonalPageState } from "./personalLibrary";
import { useUnreadNotifications } from "./notifications";
import { liveAccount } from "./live/accountChannel";
import { loadPreferredColors, preferredColorChanged } from "./live/collaboratorColors";
import { liveConnection } from "./live/liveConnection";
import { pageTopic } from "./live/liveFeed";
import { draftWording, pagePublicationApi, type PagePublication } from "./publishing/pagePublication";
import { usePageVerification } from "./verification/usePageVerification";
import { verificationMark, verificationShown } from "./verification/verificationMark";
import { isSpacePinned, pinnedFirst, setSpacePinned, SPACE_PIN_REQUIRED } from "./spacePins";
import { downloadContentExport, rememberCoverDetails, rememberedCoverDetails, type PdfCoverDetail } from "./contentExport";
import { plainTitle } from "./objects/relationsApi";
import { isBoardNode, isPageNode, isReferenceNode, pageAncestors, resolveTreeInsert, resolveTreeMove, treePlaceOf, type TreeNodeType, type TreePage, type TreeDrop } from "./treeMove";
import { carriesReference, readReferenceDrop, type ReferenceDrop } from "./boards/boardDrop";
import type { TreeAction } from "./components/PageTree.vue";
import { boardApi } from "./boards/boardApi";
import { readTreeExpansion, revealTreeAncestors, writeTreeExpansion } from "./treeExpansion";
import { organizationApi, treeReferenceApi, type OrganizationChanged } from "./organization";
import { readDocument } from "./document";
import { afterPaint, whenIdle } from "./idle";
import { PagePrefetchMap } from "./lib/pagePrefetchMap";
// A placeholder has the shape of what it stands in for: a form is a field and a button, a dialog is the open dialog (so nothing moves when it arrives).
import { badgePlaceholder, dialogSkeleton, formSkeleton, skeletonOf } from "./components/loadingPlaceholders";
const PageCreationFlow = lazyComponent(() => import("./components/PageCreationFlow.vue"), { loading: formSkeleton });
import { pageSourceApi, pageSourceFailure, type PageSourceSaveResult } from "./pageSource";
import { searchRequestPath as buildSearchRequestPath } from "./searchRequest";
import { loadBatchesTogether } from "./searchPaging";
import { orderSearchGroups } from "./searchGroupOrder";
import { isHeadingRow, isSemanticRow, rowPassage } from "./searchSemantic";
import { mergeSearchResults } from "./searchMerge";
import { createArrivalTracker } from "./searchArrival";
import { animateAtlas, FEEDBACK_HOLD_MS, forceReducedMotion, literalMs, motionMs, PROTOTYPE_MS } from "./motion/aiMotion";
import SemanticBadge from "./components/search/SemanticBadge.vue";
import ResultPassage from "./components/search/ResultPassage.vue";
import { isCollaborationPending, isCollaborationSuperseded, putWithCollaborationRetry, untilProjected } from "./savePage";
import { documentNotice } from "./documentPresentation";
import type { CollaborationStatus } from "./collaboration";
import type { CollaborationPeer } from "./collaborationPresence";
import StatusBadge from "./components/settings/StatusBadge.vue";
import CollaboratorAvatars from "./components/CollaboratorAvatars.vue";
import DocumentTitle from "./components/DocumentTitle.vue";
import type { MarkdownRichValue } from "@atlas/markdown/core";
import { MARKDOWN_LEAVE_START_EVENT, MARKDOWN_SHORTCUT_HELP_EVENT } from "./keyboard/editorEvents";
import SearchHighlight from "./components/SearchHighlight.vue";
import { cleanSnippet } from "./searchHighlight";
import SearchSuggestions, { noteVisit } from "./components/SearchSuggestions.vue";
// The comments' editor services (pages, people, YouTrack) come with the thread, not with the library's first screen (bundle budget).
const commentHost = shallowRef<typeof import("./comments/pageCommentHost").pageCommentHost>();
// The page's chrome (tags, icon, outline, connections, comments, dock): one list serves the lazy components and the fetch with the page itself.
const pageChrome = {
  tags: () => import("./components/PageTags.vue"),
  iconPicker: () => import("./components/PageIconPicker.vue"),
  outline: () => import("./components/PageOutline.vue"),
  connections: () => import("./components/PageConnections.vue"),
  comments: () => import("./comments/pageCommentHost").then(module => {
    commentHost.value = module.pageCommentHost;
    return import("./components/comments/CommentThread.vue");
  }),
  dock: () => import("./components/PageSidePanel.vue"),
};
const PageTags = lazyComponent(pageChrome.tags);
const PageIconPicker = lazyComponent(pageChrome.iconPicker);
import type { PageDecoration } from './pageDecorations';
import TagSearchSuggestions from "./components/TagSearchSuggestions.vue";
import { tagTone, type PageTag } from "./pageTags";
import { boardLink, contentItemLink, isPlainNavigationClick, pageLink, spaceLink } from "./pageLinks";
import AtlasIcon from "./components/AtlasIcon.vue";
import SidebarSpace from "./components/SidebarSpace.vue";
import PageStatusBadge from "./components/PageStatusBadge.vue";
import EmptyState from "./components/settings/EmptyState.vue";
// Draft approval previews revisions with the editor's renderer; keep that out of the shell's eager set.
const SpaceDraftApproval = lazyComponent(() => import("./components/SpaceDraftApproval.vue"), { loading: LoadingSkeleton });
// K2: the one „poslední použití kontaktu“ question of every board and page (contactLinking.ts).
const ContactLastUsageDialog = lazyComponent(() => import("./components/contacts/ContactLastUsageDialog.vue"));
import CopyPageLink from "./components/CopyPageLink.vue";
import LoginMethods from "./components/auth/LoginMethods.vue";
import { profilePageOf, type ProfileTab } from "./profile";
import ActionDisclosure from "./components/ActionDisclosure.vue";
import ActionDisclosureItem from "./components/ActionDisclosureItem.vue";
import SpaceMenuItems from "./components/SpaceMenuItems.vue";
const CreateSpaceForm = lazyComponent(() => import("./components/CreateSpaceForm.vue"), { loading: formSkeleton });
import AppDialog from "./components/AppDialog.vue";
import UnsavedChangesDialog from "./components/UnsavedChangesDialog.vue";
// The help carries the editor's keymap (@atlas/markdown/core): it loads when first asked for, not with the shell.
const KeyboardHelpDialog = lazyComponent(() => import("./components/KeyboardHelpDialog.vue"), { loading: dialogSkeleton(t.common.keyboardShortcuts, "wide") });
import { appShortcuts, createShortcutMatcher, type AppShortcutId } from "./keyboard/shortcuts";
import { matchCommands } from "./keyboard/commandMatch";
import { focusWhenPresent, landFocusWhenRendered, listKeys, tablistKeys } from "./keyboard/navigation";
import ActionDisclosureGroup from "./components/ActionDisclosureGroup.vue";
import { holdsRequirements, useProjectorPage } from "./projector/useProjectorPage";
import type { DeploymentDocument } from "./projector/deploymentMap";
import { formatDateTime, formatShortDate } from "./formatDate";
import { tellTwins } from "./spaceTwins";
import LoadingSkeleton from "./components/LoadingSkeleton.vue";
import { lazyComponent, preloadComponent } from "./lazyComponent";
import LoadingNote from "./components/settings/LoadingNote.vue";
// Public shares render the document with the editor's renderer; keep that out of the shell's eager set.
const PublicSharedPage = lazyComponent(() => import("./components/sharing/PublicSharedPage.vue"), { loading: LoadingSkeleton });
import { parseSharedToken, sharingApi, type PublicShareState } from "./sharing";
// Kontakty of a space and of the whole wiki: where people occur (the entity catalog, plan §14.4).
const ContactOverview = lazyComponent(() => import("./components/contacts/ContactOverview.vue"), { loading: LoadingSkeleton });
// Co je nového (`#/whats-new`): the release notes, bundled only with this page.
const WhatsNewPage = lazyComponent(() => import("./components/WhatsNewPage.vue"), { loading: LoadingSkeleton });
// Module Boards (Atlas 3): the board's chunk loads only behind `modules.enabled("boards")`, so a disabled module downloads nothing.
const BoardView = lazyComponent(() => import("./components/boards/BoardView.vue"), { loading: LoadingSkeleton });
// Module Projector (K29): Přehled nasazení and Na co čekáme, tabs of a space, load only behind `modules.enabled("projector")`.
const DeploymentOverview = lazyComponent(() => import("./projector/overview/DeploymentOverview.vue"), { loading: LoadingSkeleton });
const WaitingList = lazyComponent(() => import("./projector/overview/WaitingList.vue"), { loading: LoadingSkeleton });
const UnlinkedMentions = lazyComponent(() => import("./components/linking/UnlinkedMentions.vue"), { loading: LoadingSkeleton });
const ConfirmDialog = lazyComponent(() => import("./components/settings/ConfirmDialog.vue"));
const UsagePanel = lazyComponent(() => import("./components/objects/UsagePanel.vue"), { loading: skeletonOf({ rows: 2, rowHeight: "44px" }) });
const ProfilePage = lazyComponent(() => import("./components/ProfilePage.vue"), { loading: LoadingSkeleton });
const HomeDashboard = lazyComponent(() => import("./components/home/HomeDashboard.vue"), { loading: LoadingSkeleton });
import "./administration.css";
import "./profile.css";
import "./command-palette.css";
import SidebarResizer from './components/SidebarResizer.vue';
import WorkspaceSidePanel from './components/WorkspaceSidePanel.vue';
import { useWorkspacePanel } from './composables/useWorkspacePanel';
import { keepPeers, usePageViewPresence } from './composables/usePageViewPresence';
import { askAssistant, assistantCommands, assistantContextFor, openAssistant, revealCitedHeading, type SelectionAction } from './assistant/assistantEntries';
import { aiIntentAvailable, aiText } from './assist/aiIntents';
import { startBoardAi } from './assistant/boardAi';
import { useAssistant } from './assistant/useAssistant';
import { bindAssistantPanel } from './assistant/useAssistantPanel';
import AssistantLauncher from './components/assistant/AssistantLauncher.vue';
import SearchModeSwitch from './components/search/SearchModeSwitch.vue';
const SearchAnswerCard = lazyComponent(() => import("./components/search/SearchAnswerCard.vue"));
import { useSearchAnswer } from './search/useSearchAnswer';
import { moveAnswerToPanel } from './search/answerHandoff';
import AiStatus from './components/ai/AiStatus.vue';
import CommandIcon from './components/CommandIcon.vue';
// Knihovna → Prostory is the library's first tab and the landing at `#/`; the directory loads when it opens.
// Only the space overview lists pages with filters; the landing never needs it up front.
const SpacePageList = lazyComponent(() => import("./components/SpacePageList.vue"), { loading: LoadingSkeleton });
// O prostoru, the tab of a space for everyone who can open it.
const SpaceAbout = lazyComponent(() => import("./components/library/SpaceAbout.vue"), { loading: LoadingSkeleton });
import { useSpaceTemplates } from "./components/library/spaceTemplates";
// Knihovna's page: title, Nový prostor and the tabs of its parts (refresh 2.5); the parts below load lazily.
import LibraryPage from "./components/library/LibraryPage.vue";
import LibraryContent from "./components/library/LibraryContent.vue";
import SpaceHeader from "./components/library/SpaceHeader.vue";
const ModuleSettingsHost = lazyComponent(() => import("./components/admin/ModuleSettingsHost.vue"), { loading: LoadingSkeleton });
/** The library tab the address names; `#/` (none) shows Knihovna's first tab. */
const libraryTab = ref<LibraryView>();
/** The search a Knihovna address carries (`#/library/contacts?q=C%23`: who knows a topic). */
const libraryQuery = ref("");
const whatsNewOpen = ref(false);
/** After an update: the last release the account had seen, until Co je nového has opened once (its splash); and how to close the notice. */
let releaseSince: string | undefined;
let closeReleaseNotice = () => {};
const whatsNewSince = ref<string>();
/** The open board (`#/boards/<id>`), module Boards only. It opens in a page's frame (interaction spec §6): its space is the
 * selected space, the bar shows its place in the tree, its actions and who else is on it. */
const boardId = ref<string>();
/** What the board view reports once the board has loaded: its space, title and whether the reader may change it. */
const boardFrame = shallowRef<{ spaceId: string; title: string; canEdit: boolean }>();
const boardPeers = shallowRef<CollaborationPeer[]>([]);
const boardView = ref<{ goToPeer: (clientId: number) => void; locked: boolean; lockPulse: number; toggleLock: () => void;
  discussionOpen: boolean; toggleDiscussion: () => void; toggleDock: () => void; host: import("./boards/editorBlocks").BoardBlockHost }>();
const copyBoardLink = ref<InstanceType<typeof CopyPageLink>>();
const boardTrashOpen = ref(false), boardTrashBusy = ref(false), boardTrashError = ref("");
// Another board (or none): nothing of the previous one stays open or reported.
watch(boardId, () => {
  boardFrame.value = undefined;
  boardPeers.value = [];
  if (organizationOpen.value === "board") organizationOpen.value = undefined;
  boardTrashOpen.value = false;
  boardTrashError.value = "";
});
const modules = useModules();
const boardsEnabled = computed(() => modules.enabled("boards"));
const projectorEnabled = computed(() => modules.enabled("projector"));
/** A board is on screen in its frame (it has loaded and said where it lives). */
const boardOpen = computed(() => !!boardId.value && boardsEnabled.value && !!boardFrame.value);
const libraryRefresh = ref(0);
/** The reader's own marks changed (a recorded visit, a favourite): only the personal lists read again, not the page's connections. */
const personalRefresh = ref(0);
/** `GET /api/spaces` answers at most this many spaces; Knihovna → Prostory says so when the list is full. */
const SPACE_LIST_LIMIT = 200;
/** The personal library tabs the sidebar links to directly, under Knihovna (refresh 2.5). */
const sidebarLibraryTabs: { id: LibraryView; title: string; icon: string }[] = [
  // The personal entries first (roadmap 3.x §3.3: Můj den / Můj prostor / Sdíleno se mnou / Oznámení; Můj den does not exist yet).
  { id: "personal", title: t.common.mySpace, icon: "user" },
  { id: "shared", title: t.common.sharedWithMe, icon: "users" },
  { id: "notifications", title: t.common.notifications, icon: "notifications" },
  { id: "recent", title: t.common.recent, icon: "recent" },
  { id: "favorites", title: t.common.favorites, icon: "star" },
  { id: "drafts", title: t.common.myDrafts, icon: "drafts" },
];
const personalState = ref<PersonalPageState>();
const personalError = ref("");
const favoritePending = ref(false);
let personalVersion = 0;
let personalAbort: AbortController | undefined;
const moduleSetup = ref(false);
const backupWarning = ref<string | null>(null);
let backupHealthVersion = 0;
// Re-read when the window regains focus; no timer polls it (live updates arrive over SignalR, never by polling).
async function refreshBackupWarning() {
  const owner = user.value;
  if (!owner?.isWikiAdmin) { backupHealthVersion++; backupWarning.value = null; return; }
  const version = ++backupHealthVersion;
  try {
    const warning = (await api<{ warning: string | null }>("/admin/backups")).warning;
    if (user.value === owner && version === backupHealthVersion) backupWarning.value = warning;
  } catch {
    if (user.value === owner && version === backupHealthVersion) backupWarning.value = t.toast.backupUnknown;
  }
}
// The backups page read the state itself: its warning is the newer one, so an answer of a check still on its way is dropped.
function backupWarningReported(value: string | null) { backupHealthVersion++; backupWarning.value = value; }
// Správa wiki and Nastavení prostoru (plan 2026-09-27): one lazily loaded settings surface.
const SettingsHost = lazyComponent(() => import("./components/settings/SettingsHost.vue"), { loading: LoadingSkeleton });
// The switcher of the settings rail: only space settings show it, so it stays out of the shell's eager set.
const SpaceSwitcher = lazyComponent(() => import("./components/settings/SpaceSwitcher.vue"));
// Dialog titles of the menu items carry a trailing ellipsis; the opened dialog does not.
const dialogTitle = (label: string) => label.replace(/…$/, "");
const PageOrganizationPanel = lazyComponent(() => import("./components/PageOrganizationPanel.vue"), { loading: dialogSkeleton(dialogTitle(t.common.move), "wide") });
const organizationOpen = ref<"page" | "pageTrash" | "board" | "trash">();
const PagePdfExportDialog = lazyComponent(() => import("./components/PagePdfExportDialog.vue"), { loading: dialogSkeleton(dialogTitle(t.nav.autoPdf), "wide") });
const PagePdfExportStatus = lazyComponent(() => import("./components/PagePdfExportStatus.vue"));
const PagePdfDownloadDialog = lazyComponent(() => import("./components/PagePdfDownloadDialog.vue"));
const PageHistoryPanel = lazyComponent(() => import("./components/PageHistoryPanel.vue"), { loading: dialogSkeleton(t.common.pageHistory, "side") });
// Ověřený obsah (5.2): the chip at the title; it draws from the bundle, so its chunk is the only wait.
const PageVerification = lazyComponent(() => import("./components/PageVerification.vue"), { loading: badgePlaceholder });
const PageReadership = lazyComponent(() => import("./components/PageReadership.vue"), { loading: dialogSkeleton(t.common.readership, "side", "min(880px, calc(100vw - 64px))") });
const PageConnections = lazyComponent(pageChrome.connections);
// Comments are secondary data: their component loads after the article and reads only near the viewport (Fast first).
const PageDiscussionButton = lazyComponent(() => import("./components/comments/PageDiscussionButton.vue"));
const CommentThread = lazyComponent(pageChrome.comments, { loading: skeletonOf({ rows: 1, rowHeight: "41px" }) });
// The page dock (refresh 2.5): Obsah, Historie and Přístup beside the article, and the outline row without it.
const PageSidePanel = lazyComponent(pageChrome.dock, { loading: skeletonOf({ rows: 3, rowHeight: "44px" }) });
const PageOutline = lazyComponent(pageChrome.outline);
const PageMobileBar = lazyComponent(() => import("./components/PageMobileBar.vue"));
const PageUndoWedge = lazyComponent(() => import("./components/PageUndoWedge.vue"));
import type { PageSideTab } from "./components/PageSidePanel.vue";
import { pageSearchFocus, pageSearchQuery } from "./editor/pageSearchState";
import { quoteMarkdown, requestedComment, requestedQuote } from "./comments/commentFocus";
import { articleArrives } from "./editor/articleMotion";
import { useStageHeight } from "./components/library/stageHeight";
import { useTabIndicator } from "./components/library/tabIndicator";
import { revealInRow, vScrollEdges } from "./lib/scrollEdges";
import { installScrollMemory } from "./lib/scrollMemory";
import { truncatedTitle } from "./lib/truncatedTitle";
import { clearOnEscape } from "./lib/searchKeys";
import { forgetUnsavedWork, hasUnsavedWork } from "./lib/unsavedWork";
import { createCheckpointScheduler, untilQuiet } from "./editor/pageAutosave";
import { contactApi } from "./contacts/contactApi";
import { forgetPageReferences, seedPageReferences } from "./linking/pageReferenceAnswers";
import { forgetPageConnections } from "./pageConnections";
import { historyApi } from "./history";
import EditLockToggle from "./components/EditLockToggle.vue";
import PagePublicationState from "./components/publishing/PagePublicationState.vue";
const HistoryRevisionPreview = lazyComponent(() => import("./components/HistoryRevisionPreview.vue"));
import SaveState, { type SaveStateKind } from "./components/SaveState.vue";
// The page's chrome (tags, outline, connections, comments, dock) is fetched with the page itself, not after it arrives.
const loadPageChrome = () => Promise.all(Object.values(pageChrome).map(load => load())).catch(() => {});
const PageSharingView = lazyComponent(() => import("./components/sharing/PageSharingView.vue"), { loading: formSkeleton });
import { buildShellHash, parseShellHash, type LibraryView, type PageAccessTab, type PageCreationKind, type ShellRoute } from "./shellRoute";
import type DocumentEditorComponent from "./components/DocumentEditor.vue";
import { editorAssistEnabled, loadEditorAssist } from "./assist/loadAssist";
import type { AtlasAssistFactory } from "./assist/types";
import { detectSecrets } from "./assist/rules/secretDetector";
// The bar under a reader's selection comes with the first selection, not with the first screen.
import type { ReadonlySelection } from "./components/ai/ReadonlySelectionActions.vue";
const ReadonlySelectionActions = lazyComponent(() => import("./components/ai/ReadonlySelectionActions.vue"));
const PageSummaryAnswer = lazyComponent(() => import("./components/ai/PageSummary.vue"));
import { usePublishAssistant } from "./publishing/usePublishAssistant";
import { createPublishControlLoader } from "./publishing/publishControlLoader";
const loadPublishControl = () => import("./components/publishing/PublishControl.vue");
// The async wrapper stands in until the chunk has arrived (fetched on the intent to edit); from then on the component itself is
// rendered, at once: a wrapper mounts its content a task later, which made the control the late part of the top bar.
const publishControlLoader = createPublishControlLoader(loadPublishControl);
const PublishControlAsync = lazyComponent(loadPublishControl);
const PublishControl = computed(() => publishControlLoader.ready.value ?? PublishControlAsync);
import { publishRequest } from "./publishing/publication";
import { createPublicationChecks } from "./publishing/publicationChecks";
import type { PageRevision, PageRevisionSummary } from "./history";
import type { RecentChange } from "./components/home/homeApi";
import InlineReveal from "./components/settings/InlineReveal.vue";
import FilterMenu from "./components/settings/FilterMenu.vue";
// Only the search's folded filters use it, and its radio group (reka-ui, ohash) is about 5.5 KiB gzip the first screen does not need.
const OptionPills = lazyComponent(() => import("./components/settings/OptionPills.vue"));
import type { EditorPluginAction } from "./editorActions";
import { MarkdownIcon, type MarkdownIconName } from "@atlas/markdown/icons";
import t from "./i18n/shell";
import { formatNumber, rememberLanguage } from "./i18n";
import { globalSettingsBase, globalSettingsHome, resolveSettingsHash, sameSectionInSpace, sameSettingsSurface, spaceSettingsHash, type SettingsLocation } from "./settingsRoutes";
import { settingsPlaceHeading, settingsReturnLabel, type SettingsPlace } from "./settingsPlaces";
import { managedSpaces } from "./components/settings/context";
import { disclosureHooks } from "./components/settings/disclosure";
import { crossFade } from "./motion/crossFade";
let documentEditorModule: Promise<typeof import("./components/DocumentEditor.vue")> | undefined;
// Starts the editor chunk, and through its module scope code highlighting, without rendering anything.
// Opening a page and pointer or keyboard intent on a page link call it; application start only for an address that names a page.
function loadDocumentEditor() {
  documentEditorModule ??= import("./components/DocumentEditor.vue").catch((failure: unknown) => {
    documentEditorModule = undefined;
    throw failure;
  });
  return documentEditorModule;
}
// Start-up: the chunk of the view the address names downloads beside the session check instead of after the session,
// the spaces and the routed data have answered one after another. The imports are those of the lazy components below;
// the settings host loads its two surfaces as chunks of their own (settings/SettingsHost.vue), so the one the address names goes with it.
function preloadRoutedView(route: ShellRoute) {
  const chunk: Promise<unknown> | undefined = route.view === "page" ? loadDocumentEditor()
    : route.view === "settings" ? Promise.all([import("./components/settings/SettingsHost.vue"),
      route.settings.scope === "space" ? Promise.all([import("./components/settings/space/SpaceSettings.vue"), import("./components/settings/SpaceSwitcher.vue")]) : import("./components/settings/global/GlobalSettings.vue")])
    : route.view === "profile" ? import("./components/ProfilePage.vue")
    : route.view === "space" ? (route.tab === "about" ? import("./components/library/SpaceAbout.vue") : import("./components/SpacePageList.vue"))
    : undefined;
  chunk?.catch(() => {});
}
// A session that starts elsewhere (Knihovna, a space, settings) almost always opens an article next. Once its first view
// is on screen and the browser reports idle time, the page view's chunk is fetched, so that first article opens as fast
// as the following ones. Not where the browser cannot tell that it is idle, and not on a connection that asked to save data.
// It waits for the first view's own requests: started with them, it took the connection from Knihovna's cover images.
// Idle between two frames of a motion is not idle: evaluating the editor then takes the main thread for a few frames and
// the motion stutters (a space folding in the rail 2.7 s into a session dropped three frames at its turn). While
// something moves, the warm-up waits for it to end and asks for idle time again. Loops (a spinner, the living logo)
// never end and are not waited for.
const PAGE_VIEW_WARM_UP_MS = 2500;
function warmUpPageView() {
  if (typeof window.requestIdleCallback !== "function" || (navigator as Navigator & { connection?: { saveData?: boolean } }).connection?.saveData) return;
  const whenQuiet = () => window.requestIdleCallback?.(() => {
    // Settings and the profile are almost never followed by an article, so they do not pay for the editor. An open page has the
    // chunk already: the loop stops, it must not read the animations (a style recalculation) while someone types (3.6-G).
    if (!user.value || page.value || moduleSetup.value || ['settings', 'profile'].includes(parseShellHash(location.hash).view)) return;
    const moving = typeof document.getAnimations === "function"
      ? document.getAnimations().filter(animation => animation.playState === "running" && Number.isFinite(Number(animation.effect?.getComputedTiming().endTime)))
      : [];
    if (moving.length) void Promise.allSettled(moving.map(animation => animation.finished)).then(whenQuiet);
    else void loadDocumentEditor().catch(() => {});
  }, { timeout: 4000 });
  window.setTimeout(whenQuiet, PAGE_VIEW_WARM_UP_MS);
}
/** Pointing at, focusing or touching a link to a page (the tree, the search, the text, Přehled) loads the page view's code and its data. */
function pageLinkIntent(event: Event) {
  const link = event.target instanceof Element ? event.target.closest<HTMLAnchorElement>('a[href*="#/pages/"]') : null;
  if (!link) return;
  void loadDocumentEditor().catch(() => {});
  const id = /#\/pages\/([^/?#]+)/.exec(link.getAttribute("href") ?? "")?.[1];
  if (id && user.value) prefetchOnIntent(id);
}
const DocumentEditor = lazyComponent(async () => {
  const module = await loadDocumentEditor();
  // An editor mounts once with highlighting instead of being built twice; a slow highlighter never holds the page.
  // Reading renders statically and colours each code block when the highlighter arrives, so its text never waits
  // for the highlighter's chunks. A stand-in module without the export (component tests) counts as ready.
  const highlighting = editorActive.value && Reflect.has(module, "codeBlockHighlightingReady") ? module.codeBlockHighlightingReady : undefined;
  if (highlighting) await Promise.race([highlighting, new Promise(resolve => setTimeout(resolve, 1500))]);
  return module;
}, { loading: LoadingSkeleton });

const status = ref<SetupStatus>();
const publicToken = parseSharedToken(location.pathname);
const publicShare = ref<PublicShareState>({ kind: "loading" });
let publicShareVersion = 0;
async function loadPublicShare() {
  if (!publicToken) return;
  const version = ++publicShareVersion;
  publicShare.value = { kind: "loading" };
  try {
    const value = await sharingApi.sharedPage(publicToken);
    if (version === publicShareVersion) publicShare.value = { kind: "ready", value };
  } catch (failure) {
    if (version === publicShareVersion) publicShare.value = { kind: "error", missing: failure instanceof ApiError && failure.status === 404 };
  }
}
// One name for the whole wiki: the sidebar brand, the tab title and the breadcrumb
// root all read this, falling back only when the workspace has none configured.
const wikiName = computed(() => status.value?.workspaceName?.trim() || "Atlas");
watch(wikiName, (name) => { document.title = name; }, { immediate: true });
const user = ref<User>();
// The account's own live channel is the session's, set at sign-in and cleared at sign-out (accountChannel.ts).
watch(() => user.value?.id, id => { liveAccount.value = id; setLexiconAccount(id ?? null); if (id) void loadPreferredColors(); }, { immediate: true });
// The sidebar's Oznámení badge and the signal an open inbox reloads with (user: channel of the one live connection).
const { unread: unreadNotifications, signal: notificationSignal } = useUnreadNotifications(computed(() => user.value?.id));
const loading = ref(true);
/** The first /spaces answer has arrived: until then the library shows a placeholder, never "no spaces". */
const spacesLoaded = ref(false);
// The first session check after the page loads. The entry screen waits out the defer while it runs, so a
// signed-in reader whose answer comes quickly never sees it flash before the wiki ("Ověřit znovu" is not this).
const connecting = ref(true);
const pending = ref(false);
const error = ref("");
const message = ref("");
const messagePageId = ref<string>();
const setupStep = ref(1);
const setup = reactive({
  workspaceName: "",
  username: "",
  displayName: "",
  password: "",
  bootstrapToken: "",
});
const spaces = ref<Space[]>([]);
const pagesBySpace = ref<Record<string, TreePage[]>>({});
const treeVersions = ref<Record<string, number>>({});
/** „Řadit abecedně“ of each space's root, as its tree last said. */
const rootByTitle = ref<Record<string, boolean>>({});
const treeDrag =ref<{ spaceId: string; id: string; version: number; type?: TreeNodeType }>();
const rootDrop = ref<string>();
/** The open node of the tree: the page, or the board (whose space is the selected one). */
const treeItemId = computed(() => page.value?.id ?? boardId.value);
const treeSpaceId = computed(() => page.value?.spaceId ?? (boardId.value ? selectedSpace.value : undefined));
const ancestors = computed(() => treeItemId.value && treeSpaceId.value ? pageAncestors(pagesBySpace.value[treeSpaceId.value] ?? [], treeItemId.value) : []);
/** The open board's node in its space's tree, while the tree is here. */
const boardNode = computed(() => boardId.value && treeSpaceId.value ? pagesBySpace.value[treeSpaceId.value]?.find(item => item.id === boardId.value) : undefined);
const boardTitle = computed(() => boardFrame.value?.title ?? boardNode.value?.title ?? "");
/** Every tree without its boards, for what takes pages only: parents, links, counts, the page list's own facts. */
const spacePages = computed<Record<string, TreePage[]>>(() => Object.fromEntries(Object.entries(pagesBySpace.value).map(([id, items]) => [id, items.every(isPageNode) ? items : items.filter(isPageNode)])));
/** The space's pages and boards, its list's rows: a reference node is a placement in the tree, not content of the space. */
const spaceContents = computed<Record<string, TreePage[]>>(() => Object.fromEntries(Object.entries(pagesBySpace.value).map(([id, items]) => [id, items.some(isReferenceNode) ? items.filter(item => !isReferenceNode(item)) : items])));
/** What Obsah counted of each space it has shown (contacts too, which the tree never holds): its tab says the same number. */
const contentCounts = ref<Record<string, number>>({});
/** Nepropojené zmínky per space (P15): „12“, „50+“ past the first answer; the tab shows only to a writer and only with something to link. */
const unlinkedCounts = ref<Record<string, string>>({});
const selectedSpace = ref<string>();
type SpaceDetailTab = "pages" | "about" | "contacts" | "approval" | "boards" | "deployments" | "waiting" | "unlinked";
const spaceTab = ref<SpaceDetailTab>("pages");
// Another space starts at Stránky. At once, so that `selectSpace` can name the tab right after the space.
watch(selectedSpace, () => { spaceTab.value = "pages"; }, { flush: "sync" });
/** The tab the overview shows. Ke schválení is the wiki administrator's. The boards address is the Stránky list filtered to the
 * space's boards: boards have no section of their own (catalog §2). */
/** The tabs of module Projector: Přehled nasazení and Na co čekáme. */
const projectorTabs: readonly SpaceDetailTab[] = ["deployments", "waiting"];
const isProjectorTab = (tab: SpaceDetailTab): tab is "deployments" | "waiting" => projectorTabs.includes(tab);
const spaceView = computed<SpaceDetailTab>(() => (spaceTab.value === "approval" && !user.value?.isWikiAdmin) || spaceTab.value === "boards"
  || (isProjectorTab(spaceTab.value) && !projectorEnabled.value) ? "pages" : spaceTab.value);
// Stránky / O prostoru / Ke schválení switch like the parts of Knihovna: one sliding underline, and the views cross-fade in a
// stage that eases its height. The view that fades out is a still picture: nothing in it takes focus or a click.
const spaceTabsRow = ref<HTMLElement>();
// On a phone the tabs scroll sideways: the chosen one comes into the row, and the row fades at an edge where more waits (W2-8).
watch(() => [spaceView.value, selectedSpace.value], () => {
  const row = spaceTabsRow.value, tab = row?.querySelector<HTMLElement>('[role="tab"][aria-selected="true"]');
  if (row && tab) revealInRow(row, tab);
}, { flush: "post" });
const spaceTabIndicator = useTabIndicator(spaceTabsRow, '[role="tab"][aria-selected="true"]', () => [spaceView.value, selectedSpace.value, pagesBySpace.value[selectedSpace.value ?? ""]?.length, unlinkedCounts.value[selectedSpace.value ?? ""]]);
const spaceStage = ref<HTMLElement>();
const spaceStageHeight = useStageHeight(spaceStage);
function retireSpaceView(element: Element) {
  spaceStageHeight.onBeforeLeave();
  element.setAttribute("inert", "");
  element.setAttribute("aria-hidden", "true");
}
// The sidebar lists the pinned spaces (and the open one); each shows its tree while expanded.
const openedSpaces = ref(new Set<string>());
const collapsedPages = ref(new Set<string>());
/** Spaces whose sidebar tree failed to load; they wait for Zkusit znovu instead of asking on every change. */
const treeFailures = ref(new Set<string>());
/** Spaces whose pin change is on its way to the server. */
const pinPendingIds = ref(new Set<string>());
const page = ref<Page>();
/** „Kdo má přístup“ of the open page (`#/pages/<id>/access…`): the part shown in place of the document. */
const pageAccessTab = ref<PageAccessTab>();
const documentValue = shallowRef<MarkdownRichValue>();
const editor = ref<InstanceType<typeof DocumentEditorComponent>>();
/** The page editor has something to undo (its canUndo); the „Zpět“ wedge shows it to a writer on an unlocked page. */
const pageCanUndo = ref(false);
const title = ref("");
/**
 * The live editor is attached for writing (Atlas 3.6-A `editorActive`): on an intent to write (a click into the text, a key in the
 * focused page, unlocking, a link that asks to edit) until Esc, a click elsewhere, leaving the page or a long rest. Whether the
 * page may be written at all is `editable` below; this only says whether the editor is open for it now.
 */
const editorActive = ref(false);
/** Changes of the live editor no checkpoint has taken yet (autosave, 3.6-B). */
const dirty = ref(false);
// An OpenAPI YAML draft in the page's detail workspace that is not yet applied to the page draft.
// Runtime-only (decision 7): it guards leaving/saving like `dirty` but is never saved on its own.
const detailDirty = ref(false);
// An unsaved page source draft („Zdroj“ tab). Runtime-only like `detailDirty`; saved only through its own Apply.
const sourceDirty = ref(false);
const editorReady = ref(false);
const requestedHeading = ref<string>();
/** A block the address asks for (`?block=`, a deployment board's card, U4-2): revealed once the page has rendered it. */
const requestedBlock = ref<string>();
async function editorBecameReady() {
  editorReady.value = true; await nextTick();
  // Editing to begin goes to the heading with the caret (focusEditorWhenEditable); reading only scrolls to it.
  if (requestedHeading.value && !(focusEditorPending && editorActive.value)) editor.value?.navigateToHeading(requestedHeading.value);
  if (requestedBlock.value) editor.value?.revealBlock(requestedBlock.value);
  void focusEditorWhenEditable();
}
// An intent to write and a newly created page put the caret into the document as soon as it takes it: where the reader clicked
// or typed (DocumentEditor keeps that place), else at the first block on screen, and the editor does not scroll.
let focusEditorPending = false;
// The caret's own transaction publishes a collaboration status, which must not retry at once: while the document could not take
// the caret yet, that retried in a microtask loop that starved the tasks that let it (UX TS3-5).
let focusingEditor = false;
async function focusEditorWhenEditable() {
  if (!focusEditorPending || !editorActive.value || !editorReady.value) return;
  await nextTick();
  // Several statuses may have asked meanwhile: the first that placed the caret ends it, the others must not move it again.
  if (!editorActive.value || !focusEditorPending) return;
  // Where the reader is (W2-4): the heading the address asks for, else the first block on screen.
  if (requestedHeading.value && editor.value?.navigateToHeading(requestedHeading.value)) { focusEditorPending = false; return; }
  // Done only once the caret is in an editable document: the shared session's editor reports ready and takes focus() before it
  // turns editable, and a new page then lost its caret (keyboard pass). The next collaboration status tries again.
  focusingEditor = true;
  try {
    if (editor.value?.focus("visible") && document.activeElement?.closest('[contenteditable="true"]')) focusEditorPending = false;
  } finally { focusingEditor = false; }
}
const editorMountVersion = ref(0);
/** The page being opened, as the tree names it (3.6-G N4): shown in place of the previous page until its own content arrives. */
const arriving = shallowRef<{ id: string; title: string }>();
/** The page whose title `arriving` already showed: it takes its place as it is, without the arrival's fade from nothing (UX open blink).
 *  Read once by the page's arrival (the page-id watcher), which clears it. */
let arrivedShown: string | undefined;
/** When `arriving` began: its skeleton shows only after the defer token, so the editor's skeleton takes over at once only after that. */
let arrivingSince = 0;
/** The arrival's skeleton was on screen: the editor's own skeleton replaces it without fading in a second time (LoadingSkeleton `instant`). */
const arrivalSkeletonShown = ref(false);
/** The page took the place of its arrival (`arrivedShown`): its editor stands as it is too, without the reveal wave's rise (page-open-stability). */
const arrivalQuiet = ref(false);
/**
 * The page's own content has drawn and the browser rests: its comments load now, not beside the content (3.6-G B2). The connections
 * stay with the content: the graph under the article and the dock share one read (pageConnections.ts).
 */
const afterContent = ref(false);
watch(editorReady, ready => {
  afterContent.value = false;
  if (ready) whenIdle(() => { if (editorReady.value) afterContent.value = true; }, 1000);
});
const historyOpen = ref(false);
const historyInitialRevision = shallowRef<PageRevisionSummary>();
const pdfExportOpen = ref(false), pdfExportDirty = ref(false), pdfExportBusy = ref(false);
// The manual PDF export asks what its cover states; the choice is this browser's and is offered again next time.
const pdfDownloadOpen = ref(false), pdfCoverDetails = ref<PdfCoverDetail[]>(rememberedCoverDetails());
function downloadPdf() {
  pdfDownloadOpen.value = false;
  rememberCoverDetails(pdfCoverDetails.value);
  void exportContent("pages", "pdf");
}
// Čtenost stránky from the "…" menu, for everyone who can read the page (secret releases only for vault managers).
const readershipOpen = ref(false);
/** „Klávesové zkratky“ (? and Ctrl+/, keyboard pass). */
const keyboardHelpOpen = ref(false);
const collaborationSaveReady = ref(false);
const collaborationStatus = shallowRef<CollaborationStatus | null>(null);
const collaborators = shallowRef<CollaborationPeer[]>([]);
// The edited page in its view session: boards showing it see who edits it; colleagues at it through another view join the avatars.
const pageElsewhere = usePageViewPresence(() => (editorActive.value ? page.value?.id : undefined));
const pagePeers = computed<readonly CollaborationPeer[]>(previous => keepPeers(previous, [...collaborators.value, ...pageElsewhere.value.filter(peer => !collaborators.value.some(known => known.id === peer.id))]));
const browserOnline = ref(navigator.onLine);
const publishingPageId = ref<string | null>(null);
const publishing = computed(() => publishingPageId.value !== null && publishingPageId.value === page.value?.id);
function wentOffline() { browserOnline.value = false; }
function wentOnline() { browserOnline.value = true; }
/** The live editor's session holds the document (a checkpoint can be taken), or no live editor is attached. */
/** Publikovat is offered while the page still saves (it waits for it); not offline or before the editor stands. */
const canPublishSoon = computed(() => browserOnline.value && editorReady.value);
const canSaveEditor = computed(() => browserOnline.value && editorReady.value && (!collaborationStatus.value?.enabled || collaborationSaveReady.value));
/** The version a reader is shown: the publication they read, or a live page's saved state; a writer's is the working one. */
const shownVersion = computed(() => !page.value ? 0 : pageCanWrite.value || page.value.live ? page.value.version : page.value.publishedVersion ?? page.value.version);
/** A writer's page's publication (mode, when it was published), read for a published page; undefined until it arrives. */
const publication = shallowRef<PagePublication>();
/** Ověření stránky (5.2): the chip's and the dock's state, the chip's popover and the panel's Čtenost. */
const { verification, open: verificationOpen, readers: pageReaders } = usePageVerification(() => page.value);
const verificationChip = ref<InstanceType<typeof PageVerification>>();
/** A writer's page published live (Publikace Živě, `PagePublication.effective`): readers already read its saved state. */
const publishedLive = computed(() => publication.value?.effective === "Live");
/** Neither a reader nor a live page's writer has anything waiting for Publikovat (a reader never sees unpublished state). */
const nothingToPublish = computed(() => !pageCanWrite.value || publishedLive.value);
const documentStatus = computed(() => documentNotice({
  browserOnline: browserOnline.value,
  // A writer's and a live page's reader's editor is the shared session's: its notice speaks for it.
  collaboration: editable.value || page.value?.live ? collaborationStatus.value : null,
  saveError: error.value || null,
  version: page.value?.version ?? 0,
  publishedVersion: page.value?.publishedVersion ?? null,
  slowJoin: slowJoin.value,
  nothingToPublish: nothingToPublish.value,
}));
// Strings and booleans, unlike the notice and the status objects, stay the same between keystrokes: the shell does not render again.
const documentStatusTone = computed(() => documentStatus.value.tone);
const collaborationEnabled = computed(() => !!collaborationStatus.value?.enabled);
const documentStatusText = computed(() => {
  if (editorActive.value && collaborationStatus.value?.connection === 'failed' && !error.value) return t.common.editingUnavailable;
  // An empty status (offline/reconnecting: the editor owns that notice) is settled too,
  // so a just-finished publication still reaches the reader.
  const settled = documentStatus.value.text === '' || [t.common.publishedShort, t.common.draftShort, t.common.unpublishedChanges].includes(documentStatus.value.text);
  const text = settled && message.value && messagePageId.value === page.value?.id
    ? message.value : documentStatus.value.text;
  // A writer's draft is said once, in the top bar (PagePublicationState): the chip by the title steps aside.
  return barPublication.value === 'draft' && (text === t.common.draftShort || text === t.common.unpublishedChanges) ? '' : text;
});
// A transient confirmation (publish) gets a check icon and an accent tint, then
// clears itself after 2s — unlike the persistent revision states it can briefly cover.
const transientConfirmations: ReadonlySet<string> = new Set([
  t.toast.pageWasPublished,
]);
const documentStatusTransient = computed(() => transientConfirmations.has(documentStatusText.value));
// The revision state as a chip with its tone (refresh 2.5): published, a draft, unpublished changes; the rest is plain text.
const documentStatusState = computed(() => ({ [t.common.publishedShort]: "published", [t.common.draftShort]: "draft", [t.common.unpublishedChanges]: "changes" } as Record<string, string>)[documentStatusText.value]);
function updateCollaborationStatus(status: CollaborationStatus) {
  collaborationStatus.value = status;
  if (!status.enabled || status.connection !== "online") collaborators.value = [];
  // The lock only stops typing (3.6-F): a locked writer's session still checkpoints and publishes.
  collaborationSaveReady.value = status.enabled && (status.canSave || !!status.checkpointReady);
  // The shared session can make the editor editable after it said ready: an intent to write and a new page still want the caret
  // (keyboard pass). Only while the reader has not gone elsewhere meanwhile (the title, a menu): then the caret is theirs to place.
  const active = document.activeElement;
  if (focusEditorPending && !focusingEditor && (!active || active === document.body || active === workspace.value || !!documentBody.value?.contains(active))) void focusEditorWhenEditable();
}
const sidebarOpen = ref(false);
const workspacePanel = useWorkspacePanel();
const sidebar = ref<HTMLElement>();
const workspace = ref<HTMLElement>();
const readingContent = ref<HTMLElement>();
/** The space overview, whose head carries Nová stránka: the bar's copy steps aside (an attribute, not `:has()` over the workspace, which restyled the page on every edit). */
const spaceOverview = ref<HTMLElement>();
const workspaceBar = ref<HTMLElement>();
const workspaceWidth = ref(window.innerWidth - 272);
const workspaceBarHeight = ref(52);
const viewportWidth = ref(window.innerWidth);
/** A phone (≤ 760 px): no dock and no top-bar discussion; the page's discussion is the section under the content. */
const phoneLayout = computed(() => viewportWidth.value <= 760);
const sidebarWidth = ref(272);
const sidebarMaximum = computed(() => Math.max(220, Math.min(480, viewportWidth.value - 640)));
const effectiveSidebarWidth = computed(() => Math.min(sidebarWidth.value, sidebarMaximum.value));
const readingSpaceWidth = ref(window.innerWidth - 256);
let workspaceObserver: ResizeObserver | undefined;
watch([workspace, readingContent, workspaceBar], ([element, content, bar]) => {
  workspaceObserver?.disconnect();
  if (!element) return;
  let frame = 0;
  // The refs restyle the observed elements themselves: written in the callback that is "ResizeObserver loop completed"; a frame later it is not.
  workspaceObserver = new ResizeObserver(() => {
    frame ||= requestAnimationFrame(() => {
      frame = 0;
      workspaceWidth.value = element.clientWidth;
      readingSpaceWidth.value = content?.clientWidth || element.clientWidth;
      workspaceBarHeight.value = bar?.getBoundingClientRect().height || 52;
    });
  });
  workspaceObserver.observe(element);
  if (content) workspaceObserver.observe(content);
  if (bar) workspaceObserver.observe(bar);
});
function viewportResized() { viewportWidth.value = window.innerWidth; }
function savedSidebarWidth(value: string | null) {
  const number = Number(value);
  return value && Number.isFinite(number) ? Math.round(Math.max(220, Math.min(480, number))) : 272;
}
// Below this width the navigation is a drawer (shell.css); phones and tablets in portrait.
const desktopQuery = window.matchMedia('(min-width: 1025px)');
/** The rail is beside the content (not a drawer): settings and the profile then leave through the rail's own way back. */
const desktopLayout = ref(desktopQuery.matches);
function sidebarViewportChanged(event: MediaQueryListEvent) {
  desktopLayout.value = event.matches;
  if (event.matches && sidebarOpen.value) {
    sidebarOpen.value = false;
    void nextTick(() => (sidebarCollapsed.value
      ? workspace.value?.querySelector<HTMLButtonElement>('.sidebar-expand')
      : sidebar.value?.querySelector<HTMLButtonElement>('.sidebar-collapse'))?.focus());
  }
  if (!event.matches && !sidebarOpen.value && sidebar.value?.contains(document.activeElement)) {
    void nextTick(() => hostElement(mobileMenuTrigger.value)?.focus());
  }
}
const mobileMenuTrigger = ref<ComponentPublicInstance>();
const sidebarCollapsed = ref(false);
const readingWidth = ref<'comfortable' | 'wide'>('wide');
/** The reading width stored in this browser; `readingWidth` differs from it only while Vzhled previews a change. */
const savedReadingWidth = ref<'comfortable' | 'wide'>('wide');
const copyPageLink = ref<InstanceType<typeof CopyPageLink>>();
/** The Publikovat control of the page header; the palette and the chip „Nepublikované změny“ run it too. */
const publishControl = ref<{ publish: () => Promise<void> }>();
const documentOpen = computed(() => !!page.value && !!documentValue.value && !pageAccessTab.value && !libraryTab.value && !administrationOpen.value && !profileOpen.value);
// Browser preferences belong to the signed-in account; unavailable storage must
// never prevent opening or editing a document.
watch(() => user.value?.id, id => {
  readingWidth.value = savedReadingWidth.value = 'wide'; sidebarCollapsed.value = false; sidebarWidth.value = 272;
  if (!id) return;
  readingWidth.value = savedReadingWidth.value = readStored(`atlas:reading:${id}:width`) === 'comfortable' ? 'comfortable' : 'wide';
  sidebarCollapsed.value = readStored(`atlas:reading:${id}:sidebar`) === 'collapsed';
  sidebarWidth.value = savedSidebarWidth(readStored(`atlas:reading:${id}:sidebar-width`));
});
function saveReadingPreferences() {
  if (!user.value) return;
  writeStored(`atlas:reading:${user.value.id}:width`, savedReadingWidth.value);
  writeStored(`atlas:reading:${user.value.id}:sidebar`, sidebarCollapsed.value ? 'collapsed' : 'open');
  writeStored(`atlas:reading:${user.value.id}:sidebar-width`, String(sidebarWidth.value));
}
function toggleReadingWidth() { readingWidth.value = savedReadingWidth.value = readingWidth.value === 'wide' ? 'comfortable' : 'wide'; saveReadingPreferences(); }
async function toggleSidebar() {
  // Folding hides the button that was pressed; the other toggle takes the focus over.
  const fromToggle = !!(document.activeElement as HTMLElement | null)?.closest?.('.desktop-sidebar-toggle');
  sidebarCollapsed.value = !sidebarCollapsed.value; saveReadingPreferences();
  if (!fromToggle) return;
  await nextTick();
  document.querySelector<HTMLButtonElement>(sidebarCollapsed.value ? '.atlas .sidebar-expand' : '.atlas .sidebar-collapse')?.focus();
}
async function openMobileSidebar() {
  sidebarOpen.value = true;
  // The drawer's first control: the wiki brand, or the way back while settings hold the rail.
  await nextTick(); sidebar.value?.querySelector<HTMLElement>('.brand, .settings-back')?.focus();
}
async function closeMobileSidebar() { sidebarOpen.value = false; await nextTick(); hostElement(mobileMenuTrigger.value)?.focus(); }
function sidebarKeys(event: KeyboardEvent) {
  if (!sidebarOpen.value || event.isComposing || event.defaultPrevented) return;
  if (event.key === 'Escape') { event.preventDefault(); closeMobileSidebar(); }
  if (event.key !== 'Tab') return;
  const items = [...(sidebar.value?.querySelectorAll<HTMLElement>('button:not(:disabled), a[href], input, select') ?? [])].filter(item => item.getClientRects().length > 0);
  const first = items[0], last = items.at(-1);
  if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last?.focus(); }
  else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus(); }
}
const administrationOpen = ref(false);
const settingsDirty = ref(false);
/** The open settings surface (global or one space); undefined while none is open. */
const settingsLocation = ref<SettingsLocation>();
const settingsSpaceName = computed(() => {
  const target = settingsLocation.value;
  return target?.scope === "space" ? spaces.value.find(item => item.id === target.spaceId)?.name : undefined;
});
function spaceMetadataChanged(updated: { id: string; name: string; icon: string; color: string; description: string; pinPolicy?: SpacePinPolicy; cover?: Space["cover"]; verificationIntervalMonths?: number | null }) {
  const previous = spaces.value.find(item => item.id === updated.id);
  const { id, name, icon, color, description } = updated;
  // The card image (refresh 2.5) is saved on its own; Knihovna shows it as soon as settings report it.
  spaces.value = spaces.value.map(item => item.id === id ? { ...item, name, icon, color, description, pinPolicy: updated.pinPolicy ?? item.pinPolicy, ...(updated.cover !== undefined ? { cover: updated.cover } : {}),
    // Switching verification on in Obecné brings Stav obsahu into the space's menus at once.
    ...(updated.verificationIntervalMonths !== undefined ? { verificationOn: !!updated.verificationIntervalMonths } : {}) } : item);
  // The effective pin combines the new rule with the account's own choice, which only the server knows.
  if (previous && updated.pinPolicy && updated.pinPolicy !== previous.pinPolicy) void refreshAccess();
}
/** Obecné moved the space to the trash: it leaves every list, and its settings, now of nothing, give way to Knihovna. */
function spaceTrashed(spaceId: string) {
  spaces.value = spaces.value.filter(item => item.id !== spaceId);
  delete pagesBySpace.value[spaceId];
  settingsDirty.value = false;
  openLibrary("spaces");
  // Moving a space to the trash and restoring it are both the wiki admin's, so whoever trashed it may take it back at once; the trash page is the later way.
  toaster.success(t.toast.spaceInTrash, { undo: () => void runUndo(async () => {
    await (await import("./spaceSettings")).spaceTrashApi.restore(spaceId);
    await refreshImportedSpaces();
    toaster.success(t.toast.spaceBack);
  }) });
}
function openSpaceNewTemplate(spaceId: string) {
  const space = spaces.value.find(item => item.id === spaceId);
  if (space) void openTemplateWorkspace(space, "new-template");
}
/** Nenalezené dotazy offer to write the page nobody found: the new-page flow opens with the query as its title. */
function openSpaceNewPage(spaceId: string, options?: { title?: string }) {
  const space = spaces.value.find(item => item.id === spaceId);
  if (space) void openTemplateWorkspace(space, "new-page", false, "", options?.title ?? "");
}
/** Šablony stránek are a section of Nastavení prostoru. */
function openSpaceTemplates(spaceId: string) {
  openSettings(spaceSettingsHash(spaceId, "templates"));
}

const profileTab = ref<ProfileTab>("account");
const profileOpen = ref(false);
const profileDirty = ref(false);
const profileBusy = ref(false);
/** Where the profile's way back leads; the rail says it in words (settingsPlaces.ts). */
const profileReturnRoute = ref(buildShellHash({ view: "home" }));
/** Where Správa wiki's way back leads: the address it was opened from (the gear, the link in space settings), for one return; Knihovna otherwise. The rail says a return to space settings in words. */
const wikiReturnRoute = ref(buildShellHash({ view: "home" }));
const appearance = reactive<Appearance>({
  layout: "connected",
  accent: "ocean",
  mode: "system",
  motion: "system",
});
/** Initials of a name: the first letters of its first two words (Marek Musil → MM), else its first two letters. */
function initialsOf(value: string) {
  const words = value.trim().split(/\s+/).filter(Boolean);
  return (words.length > 1 ? words.slice(0, 2).map(word => word[0]).join("") : value.trim().slice(0, 2)).toLocaleUpperCase("cs");
}
/** Takes over a stored appearance; an older server sends no motion, which means "follow the device". */
function adoptAppearance(value: Appearance) {
  Object.assign(appearance, { ...value, motion: value.motion ?? "system" });
}
const systemDark = ref(
  window.matchMedia("(prefers-color-scheme: dark)").matches,
);
const dark = computed(
  () =>
    appearance.mode === "dark" ||
    (appearance.mode === "system" && systemDark.value),
);
const currentSpace = computed(() =>
  spaces.value.find(
    (space) => space.id === (page.value?.spaceId ?? selectedSpace.value),
  ),
);
/** Every space in the order of the pickers: pinned by the administrator, the account's own pins, then the rest. */
const spacesPinnedFirst = computed(() => pinnedFirst(spaces.value));
/** The sidebar's spaces: pinned by the administrator first, then the account's own pins, each by name. */
const pinnedSpaces = computed(() => spacesPinnedFirst.value.filter(isSpacePinned));
/** The space the reader is in, when it is not pinned: listed below the pinned ones as the open space. */
const sidebarSpaces = computed(() => currentSpace.value && !isSpacePinned(currentSpace.value)
  ? [...pinnedSpaces.value, currentSpace.value] : pinnedSpaces.value);
const expandedSidebarSpaceIds = computed(() => sidebarSpaces.value.filter(space => openedSpaces.value.has(space.id)).map(space => space.id));
/** Start-up at an address that names another view: Knihovna must not render, and fetch its chunks, before that view arrives. */
const startRoute = parseShellHash(location.hash);
const startedElsewhere = !(startRoute.view === "home" || (startRoute.view === "library" && startRoute.tab === "spaces"));
/** `#/`: no page, space, settings or other view is open: Domů, the home dashboard (4.0). */
const homeOpen = computed(() => !(loading.value && startedElsewhere) && !page.value && !selectedSpace.value && !libraryTab.value && !administrationOpen.value
  && !showSpaceForm.value && !templateView.value && !unavailablePage.value && !unavailableSpace.value && !whatsNewOpen.value && !boardId.value);
const libraryView = computed<LibraryView | undefined>(() => libraryTab.value);
/** Knihovna is current on every library list that has no sidebar entry of its own (Prostory, Poslední publikace). */
const libraryHomeCurrent = computed(() => !!libraryView.value && !profileOpen.value && !sidebarLibraryTabs.some(tab => tab.id === libraryView.value));
function spaceCanWrite(space: Space) { return !!user.value?.isWikiAdmin || space.role !== "Reader"; }
/** Space settings (among them the pin policy for everyone) are the space Admin's or the wiki administrator's. */
function spaceCanManage(space: Space) { return canOpenSettings({ scope: "space", spaceId: space.id, hash: spaceSettingsHash(space.id), redirect: false }); }
const canWrite = computed(
  () =>
    !!user.value?.isWikiAdmin ||
    (!!currentSpace.value && currentSpace.value.role !== "Reader" && (!page.value?.isTemplate || currentSpace.value.role === "Admin")),
);
/**
 * Who may write the open page now and so use every page action (history, move, rename, trash, automatic PDF): the page's own right
 * from the server alone. It already weighs the space role, a page grant without one (Writer on one page) and a template's Admin rule;
 * a page in the trash is never served. Going offline does not take it: the live editor keeps typed changes until it reconnects.
 */
const pageCanWrite = computed(() => !!page.value?.canEdit);
/**
 * The lock of editing (3.6-F): a writer who only wants to read locks the page. It starts locked on a touch screen (O7) and with the
 * profile's „Stránky otevírat: jen pro čtení“; Upravit or the lock unlock the open page only (O8): another page opens as the profile says.
 */
const touchScreen = typeof window.matchMedia === "function" && window.matchMedia("(hover: none) and (pointer: coarse)").matches;
const pageLocked = ref(false);
watch(() => page.value?.id, () => { pageLocked.value = touchScreen || !!user.value?.appearance.openLocked; }, { flush: "sync" });
/** The one input into editing: the page can be written and the reader has not locked it. */
const editable = computed(() => pageCanWrite.value && !pageLocked.value);
// A join is slow after a second: every open joins, and a usual join never flashes a notice. Then the status chip says it, in the
// header's own line, never above or over the document (UX open blink). A live page's reader joins quietly.
const SLOW_JOIN_MS = 1000;
const slowJoin = ref(false);
let slowJoinTimer: ReturnType<typeof setTimeout> | undefined;
watch(() => pageCanWrite.value && !!collaborationStatus.value?.enabled && collaborationStatus.value.connection === "connecting", connecting => {
  clearTimeout(slowJoinTimer);
  slowJoin.value = false;
  if (connecting) slowJoinTimer = setTimeout(() => { slowJoin.value = true; }, SLOW_JOIN_MS);
});
onUnmounted(() => clearTimeout(slowJoinTimer));
/** Raised by an attempt to write into a locked page; the lock pulses (EditLockToggle drops a pulse asked for while one runs). */
const lockPulse = ref(0);
const countUnlinked = (spaceId: string, count: number, more: boolean) => { unlinkedCounts.value[spaceId] = count ? `${formatNumber(count)}${more ? "+" : ""}` : ""; };
watch(() => (canWrite.value ? selectedSpace.value : undefined), spaceId => {
  if (spaceId) void unlinkedApi.list(spaceId).then(page => countUnlinked(spaceId, page.items.length, !!page.nextCursor), () => countUnlinked(spaceId, 0, false));
}, { immediate: true });
const canCreateSpace = computed(() => !!user.value?.isWikiAdmin);
/**
 * Page source editing: space Admin or WikiAdmin, as the Zdroj view of editing (DocumentEditor offers it only there).
 * Going offline does not revoke it (the open draft stays; a save then fails and keeps the draft).
 */
const canEditSource = computed(() => !!page.value
  && (!!user.value?.isWikiAdmin || currentSpace.value?.role === "Admin"));
const appearanceDirty = computed(() => !!user.value && (appearance.layout !== user.value.appearance.layout || appearance.accent !== user.value.appearance.accent || appearance.mode !== user.value.appearance.mode
  || appearance.motion !== (user.value.appearance.motion ?? "system") || !!appearance.openLocked !== !!user.value.appearance.openLocked
  || (appearance.dragPages !== false) !== (user.value.appearance.dragPages !== false) || (appearance.language ?? "auto") !== (user.value.appearance.language ?? "auto")
  || (appearance.collaboratorColor ?? "auto") !== (user.value.appearance.collaboratorColor ?? "auto")
  || readingWidth.value !== savedReadingWidth.value));
const searchIsOpen = ref(false);
/** The same search as a dialog or as the whole page (Ctrl+Shift+Enter, `searchView=page` in its address; 2026-10-28). */
const searchView = ref<"dialog" | "page">("dialog");
const filtersOpen = ref(false);
const templateView = ref<PageCreationKind>();
const templateDirty = ref(false);
const templateBusy = ref(false);
const initialPageParent = ref("");
const initialPageTitle = ref("");
const canManageTemplates = computed(() => !!currentSpace.value && (!!user.value?.isWikiAdmin || currentSpace.value.role === "Admin"));
const searchField = ref<ComponentPublicInstance>();
const profilePending = ref(false);
const profileError = ref("");
const query = ref("");
const commandMode = computed(() => query.value.trimStart().startsWith('>'));
const askMode = computed(() => aiCapabilities.value.answer && query.value.trimStart().startsWith('?'));
const autoSearchAnswerAllowed = ref(true);
const searchQuestion = computed(() => askMode.value ? query.value.trimStart().slice(1).trim() : query.value.trim());
type WikiCommand = { id: string; label: string; detail: string; icon: string; keywords?: string[]; run: () => unknown };
const commands = computed<WikiCommand[]>(() => {
  if (!user.value || pending.value || profileBusy.value || profilePending.value) return [];
  const items: WikiCommand[] = [];
  if (documentOpen.value) {
    items.push({ id: 'copy', label: t.cmd.copyPageLink, detail: title.value, icon: 'link', run: () => copyPageLink.value?.copy() });
    for (const action of selectionAtSearch.value) items.push({ id: action.id, label: action.label, detail: t.cmd.selectedText, icon: action.icon?.replace(/^svg:/u, '') ?? 'search', run: action.run });
    if (pageHasRequirements.value && boardsEnabled.value && deploymentBoardOffered.value) items.push({ id: 'deployment-board', label: t.cmd.deploymentBoard, detail: t.cmd.openOrCompose, icon: 'layout-dashboard', keywords: ['tabule', 'moduly', 'závislosti', 'poskládat'], run: openBoardOfDeployment });
    if (personalState.value && !favoritePending.value) items.push({ id: 'favorite', label: personalState.value.isFavorite ? t.cmd.removeFavorite : t.cmd.addFavorite, detail: t.cmd.currentPage, icon: 'star', run: toggleFavorite });
    if (pageCanWrite.value) {
      items.push({ id: 'lock', label: pageLocked.value ? t.cmd.edit : t.cmd.lockEditing, detail: 'E', icon: pageLocked.value ? 'pencil' : 'lock', keywords: ['upravit', 'jen pro čtení', 'zámek'], run: toggleLock });
      items.push({ id: 'save-version', label: t.common.saveVersion, detail: 'Ctrl S', icon: 'check', keywords: ['verze', 'historie', 'pojmenovat'], run: openSaveVersion });
      items.push({ id: 'history', label: t.common.pageHistory, detail: t.cmd.versionsRestore, icon: 'updates', run: openPageHistory });
      items.push({ id: 'organize', label: t.cmd.organize, detail: t.cmd.organizeDetail, icon: 'organize', run: () => { organizationOpen.value = 'page'; } });
      items.push({ id: 'trash', label: t.cmd.pageToTrash, detail: title.value, icon: 'trash', keywords: ['smazat', 'odstranit'], run: () => { organizationOpen.value = 'pageTrash'; } });
    }
    // The header's Publikovat, through the control itself: the same preparation, checks and findings panel (keyboard pass).
    if (unpublishedChanges.value && canSaveEditor.value)
      items.push({ id: 'publish', label: t.cmd.publishChanges, detail: title.value, icon: 'published', keywords: ['publikovat', 'zveřejnit'], run: () => publishControl.value?.publish() });
    if (canDiscard.value)
      items.push({ id: 'discard', label: t.cmd.discardChanges, detail: title.value, icon: 'history', run: openDiscard });
    if (page.value) items.push({ id: 'access', label: t.common.whoHasAccess, detail: t.cmd.accessDetail, icon: 'users', keywords: ['sdílet', 'sdílení', 'oprávnění', 'práva'], run: () => openPageAccess(page.value!.id) });
    items.push({ id: 'readership', label: t.common.readership, detail: title.value, icon: 'bar-chart-3', run: () => { readershipOpen.value = true; } });
    items.push({ id: 'width', label: readingWidth.value === 'wide' ? t.cmd.comfortableWidth : t.cmd.wideDocument, detail: readingWidth.value === 'wide' ? t.cmd.comfortableLine : t.cmd.moreRoom, icon: 'page', run: toggleReadingWidth });
  }
  if (boardOpen.value) {
    items.push({ id: 'copy-board', label: t.cmd.copyBoardLink, detail: boardTitle.value, icon: 'link', run: () => copyBoardLink.value?.copy() });
    if (personalState.value && !favoritePending.value) items.push({ id: 'favorite', label: personalState.value.isFavorite ? t.cmd.removeFavorite : t.cmd.addFavorite, detail: t.cmd.currentBoard, icon: 'star', run: toggleFavorite });
    if (boardFrame.value?.canEdit) items.push({ id: 'trash-board', label: t.cmd.boardToTrash, detail: boardTitle.value, icon: 'trash', keywords: ['smazat', 'odstranit'], run: () => { boardTrashOpen.value = true; boardTrashError.value = ''; } });
  }
  if (currentSpace.value && canWrite.value && !profileOpen.value && !administrationOpen.value) {
    items.push({ id: 'create', label: t.common.newPage, detail: currentSpace.value.name, icon: 'plus', run: showCreatePage });
    if (boardsEnabled.value) items.push({ id: 'create-board', label: t.common.newBoard, detail: currentSpace.value.name, icon: 'layout-dashboard', run: showCreateBoard });
  }
  items.push(...assistantCommands(assistantReachable.value, assistantContext.value, openAssistant, aiCapabilities.value.agent, aiCapabilities.value.arrangePages, canAsk.value));
  items.push(
    { id: 'library', label: t.common.library, detail: t.cmd.librarySpacesDetail, icon: 'book', run: () => openLibrary('spaces') },
    { id: 'favorites', label: t.cmd.favoritePages, detail: t.common.library, icon: 'star', run: () => openLibrary('favorites') },
    { id: 'recent', label: t.cmd.recentPages, detail: t.common.library, icon: 'updates', run: () => openLibrary('recent') },
    { id: 'drafts', label: t.common.myDrafts, detail: t.common.library, icon: 'drafts', run: () => openLibrary('drafts') },
    { id: 'contacts', label: t.cmd.contacts, detail: t.common.library, icon: 'users', keywords: ['adresář', 'lidé'], run: () => openLibrary('contacts') },
    { id: 'notifications', label: t.common.notifications, detail: t.common.library, icon: 'notifications', keywords: ['zmínky', 'inbox', 'upozornění'], run: () => openLibrary('notifications') },
    { id: 'sidebar', label: sidebarCollapsed.value ? t.cmd.showSidebar : t.cmd.collapseSidebar, detail: t.cmd.desktopLayout, icon: 'menu', run: toggleSidebar },
    { id: 'profile', label: t.cmd.profileAppearance, detail: t.cmd.profileDetail, icon: 'account', run: () => openProfile() },
    { id: 'shortcuts', label: t.common.keyboardShortcuts, detail: t.cmd.shortcutsDetail, icon: 'circle-help', keywords: ['nápověda', 'klávesnice'], run: () => { keyboardHelpOpen.value = true; } },
    { id: 'whats-new', label: t.cmd.whatsNew, detail: t.cmd.whatsNewDetail, icon: 'scroll-text', keywords: ['novinky', 'verze', 'nápověda', 'změny'], run: () => openWhatsNew() },
  );
  if (currentSpace.value && spaceCanManage(currentSpace.value) && !profileOpen.value && !administrationOpen.value) items.push({ id: 'page-trash', label: t.cmd.pageTrash, detail: currentSpace.value.name, icon: 'trash', keywords: ['obnovit'], run: () => { organizationOpen.value = 'trash'; } });
  if (user.value.isWikiAdmin) items.push({ id: 'space-trash', label: t.cmd.spaceTrash, detail: t.common.wikiAdmin, icon: 'trash', keywords: ['obnovit'], run: () => openSettings(`${globalSettingsBase}/spaces/trash`) });
  if (user.value.isWikiAdmin) items.push({ id: 'settings', label: t.common.wikiAdmin, detail: t.common.settings, icon: 'settings', keywords: ['nastavení'], run: openAdministration });
  if (currentSpace.value && spaceCanManage(currentSpace.value)) items.push({ id: 'space-settings', label: t.common.spaceSettings, detail: currentSpace.value.name, icon: 'settings', run: () => openSettings(spaceSettingsHash(currentSpace.value!.id)) });
  // Stav obsahu is every editor's of the space (5.2 §6), its one section of the space settings.
  if (currentSpace.value?.verificationOn && spaceCanWrite(currentSpace.value)) { const space = currentSpace.value; items.push({ id: 'content-health', label: t.cmd.contentHealth, detail: space.name, icon: 'shield-check', keywords: ['overeni', 'verification'], run: () => spaceMenu(space, 'content-health') }); }
  if (currentSpace.value && projectorEnabled.value) {
    const space = currentSpace.value;
    items.push(
      { id: 'deployments', label: t.common.deploymentOverview, detail: space.name, icon: 'list-checks', keywords: ['projector'], run: () => openSpaceTab(space, 'deployments') },
      { id: 'waiting', label: t.common.whatWeWaitFor, detail: space.name, icon: 'updates', keywords: ['projector', 'čekáme'], run: () => openSpaceTab(space, 'waiting') },
    );
  }
  // Every space by name: „> prostor provoz“ goes there without the sidebar.
  // Two spaces of one name differ by their size and last change, and by their description (else their order) when even those match.
  const spaceDetail = (space: Space) => t.cmd.spaceDetail(space.pageCount, space.lastChangedAt ? formatShortDate(space.lastChangedAt) : undefined);
  const nameCount = new Map<string, number>();
  for (const space of spaces.value) nameCount.set(space.name, (nameCount.get(space.name) ?? 0) + 1);
  const twins = tellTwins(spaces.value, space => `${space.name}|${spaceDetail(space)}`, space => space.description);
  spaces.value.forEach((space, index) => {
    const detail = nameCount.get(space.name)! > 1 ? [spaceDetail(space), twins[index]].filter(Boolean).join(' · ') : t.nav.space;
    items.push({ id: `space:${space.id}`, label: t.cmd.goToSpace(space.name), detail, icon: 'book', run: () => openSpaceTab(space, 'pages') });
  });
  if (canCreateSpace.value) items.push({ id: 'new-space', label: t.common.newSpace, detail: t.cmd.createSpace, icon: 'plus', keywords: ['vytvořit', 'založit'], run: () => openSpaceCreation() });
  return matchCommands(query.value.trimStart().slice(1), items);
});
/** A space at one of its tabs (Stránky, Přehled nasazení, Na co čekáme), as from its sidebar entry and then the tab. */
function openSpaceTab(space: Space, tab: SpaceDetailTab) { return selectSpace(space, false, tab); }
async function runCommand(id: string) {
  const command = commands.value.find(item => item.id === id);
  if (!command) return;
  // The folding search stays modal until AppDialog emits `closed` and then restores focus to its opener: a command run
  // earlier cannot take focus (the copy fallback's field), and the restoration would take it back (or dismiss it).
  commandAfterSearchClose = command.run;
  closeSearch();
  sidebarOpen.value = false;
}
async function setSearchMode(mode: 'pages' | 'ask' | 'commands') {
  searchResultChosen.value = false;
  await switchSearchInputMode(fieldElement(searchField.value), query.value, mode, value => {
    query.value = value;
    autoSearchAnswerAllowed.value = false;
  });
}
const results = ref<SearchResult[]>([]);
/** Board hits (module Boards) of the first page of a full pass: their own ranking beside the pages', listed as Tabule. */
const searchResources = ref<{ type: string; id: string; spaceId: string; spaceName: string; title: string; snippet: string;
  /** A comment's object and place (its own group „Komentáře“, comments spec §4). */
  target?: { objectId: string; typeId: string; anchor: string | null } }[]>([]);
const { capabilities: aiCapabilities } = useAiCapabilities();
/** The assistant panel opens with answers from the wiki or with the agent alone (the agent needs no index). */
const assistantReachable = computed(() => aiCapabilities.value.assistant || aiCapabilities.value.agent);
watch(searchQuestion, () => { autoSearchAnswerAllowed.value = true; }, { flush: "sync" });
const semanticOn = computed(() => aiCapabilities.value.semanticSearch);
// Assistant (phase 3 W10): the logic is in src/assistant; here only what the screen knows and the entries it renders.
const assistant = useAssistant();
/** A question can be answered now (P11's one rule): the entries that ask (over the page, continuing an answer) follow it; the plain panel opener stays on assistantReachable. */
const canAsk = computed(() => aiIntentAvailable('ask', { editing: false, canAnswer: assistant.canAnswer.value, transform: false }));
bindAssistantPanel();
const assistantContext = computed(() => assistantContextFor({
  documentOpen: documentOpen.value, editing: editorActive.value, page: page.value, space: profileOpen.value || administrationOpen.value ? null : currentSpace.value,
  canWrite: !!currentSpace.value && spaceCanWrite(currentSpace.value),
}));
const assistantPageReading = computed(() => !!assistantContext.value.pageId);
watch(assistantContext, (context) => assistant.setContext(context), { immediate: true });
startBoardAi();
assistant.setNavigator((source) => {
  void openPage(source.pageId, false, false, source.headingId ?? undefined).then(() => revealCitedHeading(source.headingId));
  if (window.innerWidth < 760) assistant.close(); // the panel covers the screen there: the page must be visible
});
assistant.setEditor((id) => {
  void openPage(id, true);
  if (window.innerWidth < 760) assistant.close();
});
function launchAssistant(onPage = false) { sidebarOpen.value = false; openAssistant(assistantContext.value, onPage); }
const searchSemantic = ref(false);
/** The meaning search runs in the full pass after the instant text results: the field says so with the AI status (XS). */
const meaningPending = computed(() => searchIsOpen.value && searching.value && semanticOn.value && !commandMode.value && searchQuestion.value.length >= 3);
const didYouMean = ref("");
/** No page had every word of the query: the server shows partial matches, and its next pages continue them (T1). */
const searchRelaxed = ref(false);
const arrivals = createArrivalTracker();
const suggestedPages = ref<{ id: string }[]>([]);
const showSuggestions = computed(() => searchIsOpen.value && !query.value.trim() && !searchFiltered.value && !commandMode.value);
const searchGroups = computed(() => {
  const pageItems = (heading: boolean) => results.value.filter(item => isHeadingRow(item, semanticOn.value, query.value) === heading).map(result => {
    const passage = rowPassage(result, semanticOn.value);
    // An article row opens its page at the top unless a passage leads into a section.
    const headingId = passage ? passage.headingId : heading ? result.headingId : null;
    return { board: false,
    key: `page-${result.id}-${result.isDraft}-${result.headingId ?? ''}`, result: { ...result, headingId }, widget: undefined as SearchWidget | undefined, live: false,
    passage, semantic: isSemanticRow(result, semanticOn.value) || undefined, mark: verificationMark(result.verificationState),
    title: heading ? result.headingTitle || result.title : result.title,
    detail: heading ? `${result.title} · ${result.spaceName}` : result.spaceName,
    href: pageLink(result.id, headingId ?? undefined), icon: heading ? 'hash' : 'page', color: heading ? 'green' : 'blue',
  }; });
  const widgetItems = (kind: SearchWidget['kind']) => results.value.flatMap(result => (result.widgets ?? [])
    .map((widget, index) => ({ widget, index })).filter(item => item.widget.kind === kind)
    .map(({ widget, index }) => ({ board: false, passage: undefined, semantic: undefined, key: kind === 'youtrack' && widget.url ? `issue-${widget.url}` : `widget-${result.id}-${kind}-${index}`, widget, live: false,
      result: { ...result, headingId: widget.headingId }, mark: verificationMark(result.verificationState), title: widget.title,
      detail: widget.detail, href: kind === 'youtrack' && widget.url ? widget.url : pageLink(result.id, widget.headingId ?? undefined),
      icon: kind === 'contact' ? 'users' : 'check', color: kind === 'contact' ? 'green' : 'blue',
    })));
  const boards = searchResources.value.filter(hit => hit.type === 'board').map(hit => ({ board: true, passage: undefined, semantic: undefined, key: `board-${hit.id}`, live: false,
    widget: undefined as SearchWidget | undefined, result: { id: hit.id, spaceId: hit.spaceId, spaceName: hit.spaceName, title: hit.title, snippet: hit.snippet } as SearchResult,
    title: hit.title, detail: hit.spaceName, href: boardLink(hit.id), icon: 'layout-dashboard', color: 'blue',
  }));
  // A comment leads to its object (and a page's heading); its title is the object's.
  const comments = searchResources.value.filter(hit => hit.type === 'comment' && hit.target).map(hit => {
    const target = hit.target!, onBoard = target.typeId === 'atlas.board';
    return { board: onBoard, passage: undefined, semantic: undefined, key: `comment-${hit.id}`, live: false, widget: undefined as SearchWidget | undefined,
      result: { id: target.objectId, spaceId: hit.spaceId, spaceName: hit.spaceName, title: hit.title, snippet: hit.snippet, headingId: onBoard ? undefined : target.anchor ?? undefined } as SearchResult,
      title: hit.title, detail: t.cmd.commentIn(hit.spaceName), href: onBoard ? boardLink(target.objectId) : pageLink(target.objectId, target.anchor ?? undefined), icon: 'quote', color: 'green',
    };
  });
  // Only issues placed on pages the reader can read (the server's widget index), never a free search of YouTrack; always after the content.
  const known = new Set<string>();
  const issues = widgetItems('youtrack').filter(item => { if (known.has(item.key)) return false; known.add(item.key); return true; });
  // UX SB4-5: spaces are one more group under the same title-first rule, so a space named as the query leads and Enter opens it.
  const spaceItems = searchSpaces.value.map(space => ({ board: false, passage: undefined, semantic: undefined, key: `space-${space.id}`, live: false,
    widget: undefined as SearchWidget | undefined, result: { id: space.id, spaceId: space.id, spaceName: space.name, title: space.name, snippet: '' } as SearchResult,
    title: space.name, detail: t.cmd.spaceOpenOverview, href: spaceLink(space.id), icon: space.icon, color: space.color,
  }));
  return orderSearchGroups([
    { title: t.common.pages, kind: 'pages', items: pageItems(false) },
    { title: t.common.headings, kind: 'headings', rankFloor: 1, items: pageItems(true) },
    { title: t.search.boards, kind: 'boards', items: boards },
    { title: t.common.comments, kind: 'comments', items: comments },
    { title: t.cmd.contacts, kind: 'contacts', items: widgetItems('contact') },
    { title: 'YouTrack', kind: 'youtrack', rankFloor: 3, items: issues },
    { title: t.common.spaces, kind: 'spaces', items: spaceItems },
  ].filter(group => group.items.length), searchQuestion.value).map((group, groupIndex, groups) => ({ ...group,
    items: group.items.map((item, index) => ({ ...item, index: groups.slice(0, groupIndex).reduce((sum, g) => sum + g.items.length, 0) + index })),
  }));
});
const searchResultKeys = computed(() => searchGroups.value.flatMap(group => group.items.map(item => item.key)));
const groupedResultCount = computed(() => searchGroups.value.reduce((sum, group) => sum + group.items.length, 0));
const searchResultCount = computed(() => showSuggestions.value ? suggestedPages.value.length : groupedResultCount.value);
const searchResultId = (index: number) => `${showSuggestions.value ? 'suggested' : 'search'}-result-${index}`;
const hasMore = ref(false);
const searchTag = ref<PageTag>();
const searchSpace = ref("");
const searchContent = ref("all");
const searchChangedWithin = ref("any");
const searchSort = ref("relevance");
const searchScopeLabel = computed(() => searchContent.value === "drafts" ? t.search.scopeDrafts : searchContent.value === "published" ? t.search.scopePublished : t.common.all);
const searchScopeHint = computed(() => searchContent.value === "drafts"
  ? t.search.hintDrafts
  : searchContent.value === "published"
    ? t.search.hintPublished
    : t.search.hintAll);
const searchSortLatestLabel = computed(() => searchContent.value === "drafts" ? t.search.sortDrafts : searchContent.value === "published" ? t.search.sortPublished : t.search.lastChange);
const searchSpaceOptions = computed(() => [{ value: "", label: t.search.allSpaces }, ...spacesPinnedFirst.value.map(space => ({ value: space.id, label: space.name }))]);
const searchChangedOptions = computed(() => [{ value: "any", label: t.search.anytime }, { value: "day", label: t.search.last24 }, { value: "week", label: t.search.last7 }, { value: "month", label: t.search.last30 }]);
const searchContentOptions = computed(() => [{ value: "all", label: t.common.all }, { value: "published", label: t.search.scopePublished }, { value: "drafts", label: t.search.scopeDrafts }]);
const searchSortOptions = computed(() => [{ value: "relevance", label: t.search.byRelevance }, { value: "published", label: searchSortLatestLabel.value }]);
const searchFiltered = computed(() => !!searchTag.value || !!searchSpace.value || searchContent.value !== "all" || searchChangedWithin.value !== "any" || searchSort.value !== "relevance");
const activeFilterCount = computed(() => Number(!!searchTag.value) + Number(!!searchSpace.value) + Number(searchContent.value !== "all") + Number(searchChangedWithin.value !== "any") + Number(searchSort.value !== "relevance"));
const searchSpaces = ref<{ id: string; name: string; icon: string; color: string }[]>([]);
const searchNextOffset = ref<number | null>(null);
const loadingMore = ref(false);
const searching = ref(false);
const searchOpening = ref(false);
const searchError = ref("");
const searchAttempted = ref(false);
// The shown results belong to an earlier query; they stay in place, dimmed, until the new ones arrive.
const searchStale = ref(false);
const searchIndex = ref(0);
const searchResultChosen = ref(false);
const searchAnswerCard = ref<InstanceType<typeof SearchAnswerCard>>();
const searchAnswer = useSearchAnswer({
  question: searchQuestion, spaceId: searchSpace, contextSpaceId: computed(() => currentSpace.value?.id ?? ""), open: computed(() => searchIsOpen.value && !commandMode.value),
  enabled: computed(() => aiCapabilities.value.answer), askMode, autoAllowed: autoSearchAnswerAllowed,
});
// The mode switch is the entry point; an empty search should not devote half its space to an empty answer.
const searchAnswerPaneOpen = computed(() => aiCapabilities.value.answer && !commandMode.value
  && (askMode.value || searchAnswer.visible.value || searchAnswer.state.value === "canceled"));
const highlightedSource = ref<number | null>(null);
const highlightedPage = computed(() => searchAnswer.sources.value.find(source => source.number === highlightedSource.value)?.pageId);
function answerSourceOrigin(number: number) {
  const id = searchAnswer.sources.value.find(source => source.number === number)?.pageId;
  return [...document.querySelectorAll<HTMLElement>('.search-result[data-page-id]')].find(row => row.dataset.pageId === id) ?? null;
}
let focusAssistantAfterSearchClose = false;
/** The view the palette opened over (SH-17). */
let routeAtSearch = "";
let commandAfterSearchClose: (() => unknown) | undefined;
type CaretEditor = { state: { selection: { from: number; to: number } }; commands: { setTextSelection(range: { from: number; to: number }): boolean; focus(): boolean } };
/** The editor text and its selection the palette opened over (SE3-5). */
let caretAtSearch: { element: Element; editor: CaretEditor; from: number; to: number } | undefined;
/** „Kde se to ještě zmiňuje“ and „Kopírovat odkaz na sekci“ for the text selected when the palette opened (DocumentEditor.selectionActions). */
const selectionAtSearch = shallowRef<{ id: string; label: string; icon?: string; run(): unknown }[]>([]);
function editorCaret() {
  const element = document.activeElement?.closest('.tiptap');
  const editor = (element as { editor?: CaretEditor } | null)?.editor;
  return element && editor ? { element, editor, from: editor.state.selection.from, to: editor.state.selection.to } : undefined;
}
async function searchDialogClosed() {
  const command = commandAfterSearchClose;
  commandAfterSearchClose = undefined;
  if (command) {
    await nextTick();
    workspace.value?.focus({ preventScroll: true });
    void command();
    landAfterNavigation();
    return;
  }
  const caret = caretAtSearch;
  caretAtSearch = undefined;
  if (!focusAssistantAfterSearchClose) {
    // A result opened, or Esc: the opened view's heading, or the page under the palette, never <body> (keyboard pass).
    // The dialog gives focus back to its opener; after a result that is a row of the old view (a tree link), not where the reader is now (SH-17).
    if (currentRoute() !== routeAtSearch && document.activeElement instanceof HTMLElement) document.activeElement.blur();
    // Back in the editor, the caret where it was (SE3-5): focusing the text alone puts it at the start.
    else if (caret && document.activeElement?.closest('.tiptap') === caret.element) { caret.editor.commands.setTextSelection(caret); caret.editor.commands.focus(); }
    landAfterNavigation();
    return;
  }
  focusAssistantAfterSearchClose = false;
  await nextTick();
  if (!searchIsOpen.value && assistant.open.value)
    document.querySelector<HTMLElement>('[data-assistant-composer] textarea')?.focus({ preventScroll: true });
}
async function continueSearchAnswer() {
  const id = searchAnswer.conversationId.value;
  const origin = searchAnswerCard.value?.element();
  if (!id || !canAsk.value) return;
  if (!(await assistant.continueFrom(id))) return;
  await import('./components/assistant/AssistantPanel.vue');
  await nextTick();
  const target = document.querySelector<HTMLElement>('[data-assistant-panel] [data-ai-answer]');
  if (origin && target) await moveAnswerToPanel(origin, target);
  // The folding search still owns the native modal layer until AppDialog closes.
  focusAssistantAfterSearchClose = true;
  rememberSearchView(); searchUrl(false, true); stopSearch();
}
async function continuePageSummary(id: string) { if (canAsk.value && await assistant.continueFrom(id)) { await nextTick(); document.querySelector<HTMLElement>('[data-assistant-composer] textarea')?.focus({preventScroll:true}); } }
const readonlySelection=shallowRef<ReadonlySelection|null>(null);
watch([()=>page.value?.id,editorActive],()=>{readonlySelection.value=null;});
// The selection toolbar (refresh 2.5, P11): Zeptat se attaches the selection to the next question, Vysvětlit and Shrnout ask at once.
async function selectionAction(action:SelectionAction,text:string){
  const sent=aiText(text);
  if(editorActive.value||!page.value||!assistant.canAnswer.value||!sent)return;
  const section=readonlySelection.value?.section;
  readonlySelection.value=null;
  await askAssistant(action,sent,section?`Sekce „${section}“`:undefined);
}
async function findSelection(text:string){if(!page.value||!text.trim()||detectSecrets(text).length)return;readonlySelection.value=null;await openSearch();query.value=text.trim().replace(/^>/u,"");}
function showMyConversations() { if (assistant.openFor(assistantContext.value)) assistant.showList(); }
watch(searchResultKeys, (keys, previous) => {
  const selected = searchResultChosen.value ? previous[searchIndex.value] : undefined;
  if (selected && keys.includes(selected)) searchIndex.value = keys.indexOf(selected);
  // An empty list in between (a new query on its way) keeps the move the arrows made; the answer's rows clamp it (F13).
  else if (keys.length) searchIndex.value = Math.min(searchIndex.value, keys.length - 1);
}, { flush: 'sync' });
// Nový prostor is a full-screen workspace view at #/new-space.
const showSpaceForm = ref(false);
// A page link the account cannot open (missing, in the trash or not shared with it) gets its own
// clear state at its own address, instead of leaving the previous page on screen.
const unavailablePage = ref<string>();
/** A space the address names but the reader cannot open (gone, in the trash or closed): said as for a page (UX SB4-7). */
const unavailableSpace = ref<string>();
let navigationVersion = 0;
/** Bumped by every sign-in and sign-out: a late answer of an earlier session's start-up is dropped (review 3.5.1, finding 3). */
let sessionGeneration = 0;
const openingPage = ref(false);
let searchTimer: ReturnType<typeof setTimeout> | undefined;
let searchEnrichmentTimer: ReturnType<typeof setTimeout> | undefined;
let searchAbort: AbortController | undefined;
let searchVersion = 0;
let searchViewToRestore: { url: string; index: number; scrollTop: number } | undefined;

const toaster = useToast();
let messageTimer: ReturnType<typeof setTimeout> | undefined;
/**
 * The page header's own status slot keeps the short revision state of the open page („Stránka byla publikována.“)
 * beside its revision chip and gone when another page opens; whether that becomes a toast
 * too is the owner's call. Every other confirmation is a toast (`toaster`, P6 „Jeden systém oznámení“), with „Zpět“ where it can be undone.
 */
function confirmDocument(text: string) {
  message.value = text;
  messagePageId.value = page.value?.id;
  clearTimeout(messageTimer);
  if (transientConfirmations.has(text))
    messageTimer = setTimeout(() => { if (message.value === text) message.value = ""; }, FEEDBACK_HOLD_MS);
}
function navigateLink(event: MouseEvent, navigate: () => unknown) {
  if (!isPlainNavigationClick(event)) return;
  event.preventDefault();
  void navigate();
}
/** The Zpět of a toast: run(), but a refusal is said by a toast too (run() would show it as the open page's save error in its header). */
function runUndo(action: () => Promise<void>) {
  return run(async () => { try { await action(); } catch (failure) { toaster.error(errorMessage(failure)); } });
}
async function run(action: () => Promise<void>) {
  if (pending.value) return;
  pending.value = true;
  error.value = "";
  message.value = "";
  try {
    await action();
  } catch (failure) {
    error.value = errorMessage(failure);
  } finally {
    pending.value = false;
  }
}
async function initialize() {
  loading.value = true;
  error.value = "";
  try {
    if (publicToken) { await loadPublicShare(); return; }
    // None of these depends on another's answer; an unauthenticated or unready answer is simply unused.
    const setupRequest = api<SetupStatus>("/setup/status");
    const sessionRequest = api<User>("/auth/me");
    sessionRequest.catch(() => {});
    const linked = parseShellHash(location.hash);
    if (linked.view === "page" || linked.view === "pageAccess") prefetchPage(linked.pageId);
    if (linked.view === "space") prefetchTree(linked.spaceId);
    preloadRoutedView(linked);
    status.value = await setupRequest;
    if (status.value.databaseReady && !status.value.requiresSetup) {
      try {
        user.value = await sessionRequest;
        await authenticated();
      } catch (failure) {
        if (!(failure instanceof ApiError && failure.status === 401))
          throw failure;
      }
    }
  } catch (failure) {
    error.value = errorMessage(failure);
  } finally {
    pagePrefetches.clear();
    treePrefetches.clear();
    dropEarlyReads();
    loading.value = false;
    spacesLoaded.value = true;
    connecting.value = false;
  }
}
async function authenticated() {
  const generation = ++sessionGeneration;
  const current = () => generation === sessionGeneration;
  useAiCapabilities().start();
  clearCsrf();
  adoptAppearance(user.value!.appearance);
  // The account chose another language than this browser showed (a new browser, a change elsewhere): once, in that language.
  if (rememberLanguage(user.value!.appearance.language)) { location.reload(); return; }
  const firstReads = Promise.all([
    user.value!.isWikiAdmin ? api<{ pending: boolean }>("/admin/setup") : Promise.resolve({ pending: false }),
    api<Space[]>("/spaces"),
    // Before the first route: a board address or the Tabule tab needs to know whether the module runs.
    modules.start(),
  ]);
  // The warning is a badge on the settings button, filled in when its answer arrives; a page does not wait for it, and it
  // is asked after the reads the first render needs. It never rejects.
  const backupCheck = refreshBackupWarning();
  const [setup, visibleSpaces] = await firstReads;
  if (!current()) return;
  moduleSetup.value = setup.pending;
  spaces.value = visibleSpaces;
  spacesLoaded.value = true;
  restoreTreeExpansion(user.value!.id);
  // The settings overview reads the warning for its first paint (a missing one means "in order"), so a settings address waits.
  if (parseShellHash(location.hash).view === "settings") await backupCheck;
  if (!current()) return;
  // The sidebar trees of expanded spaces load through their watcher, beside the routed page.
  await historyNavigation();
  if (!current()) return;
  announceRelease(user.value!);
  warmUpPageView();
  const callbackUrl = new URL(location.href);
  if (callbackUrl.searchParams.has("authError")) {
    error.value =
      t.toast.externalFailed;
    callbackUrl.searchParams.delete("authError");
    history.replaceState(
      null,
      "",
      callbackUrl.pathname + callbackUrl.search + callbackUrl.hash,
    );
  }
}
function loginComplete(account: User) {
  // The shell shows as soon as the account is known, so the load behind it must not hold the action
  // lock: a click on the profile right after signing in used to be dropped until the library loaded.
  // A navigation started meanwhile supersedes the routed load (navigationVersion), as on any other page.
  // The load starts inside the update, when the account is known, exactly as without the cross-fade.
  let loading: Promise<void> = Promise.resolve();
  return crossFade(() => {
    user.value = account;
    error.value = "";
    message.value = "";
    loading = authenticated().catch((failure) => {
      if (user.value === account) { error.value = errorMessage(failure); spacesLoaded.value = true; }
    });
  }).then(() => loading);
}
async function refreshImportedSpaces() {
  const owner = user.value;
  if (!owner) return;
  try {
    const updated = await api<Space[]>("/spaces");
    if (user.value === owner) { spaces.value = updated; libraryRefresh.value++; }
  } catch (failure) { if (user.value === owner) error.value = errorMessage(failure); }
}
async function finishModuleSetup() {
  await refreshImportedSpaces(); moduleSetup.value = false;
}
async function refreshSession() {
  const owner = user.value;
  if (!owner) return;
  try {
    clearCsrf();
    const refreshed = await api<User>("/auth/me");
    if (user.value === owner) user.value = refreshed;
  } catch (failure) {
    if (user.value !== owner) return;
    // The server has already ended this session (e.g. after a password change): sign out
    // locally through the same reset as logout instead of keeping the old account's UI.
    if (failure instanceof ApiError && failure.status === 401) {
      await crossFade(() => {
        resetSessionState();
        error.value = t.toast.signedOut;
      });
    } else error.value = errorMessage(failure);
  }
}
// Any request refused as signed out (the session expired or was ended elsewhere) checks the session the same way (UX SB4-8).
onSessionEnded(() => void refreshSession());
onUnmounted(() => onSessionEnded(undefined));
function openAdministration() {
  openSettings(globalSettingsHome);
}
/** The rail shows settings navigation while a settings surface or the profile is open (refresh 2.5). */
const settingsMode = computed(() => !!user.value && (profileOpen.value || administrationOpen.value));
// The wiki navigation the rail replaces meanwhile, as a component only so that KeepAlive can set it aside: the trees
// of the pinned spaces are most of the document, and rebuilding them took a quarter to a third of the way back to an
// article (settings → article: 116–129 → 90–94 ms, 674–713 → 451–472 ms on a CPU four times slower).
const SidebarWikiNavigation = defineComponent({ name: "SidebarWikiNavigation", setup: (_, { slots }) => () => slots.default?.() });
// Settings of a space the account does not know have no overview to return to: that surface is Správa wiki's place.
const settingsPlace = computed<SettingsPlace>(() => profileOpen.value ? "profile" : settingsSpace.value ? "space" : "wiki");
const settingsSpace = computed(() => {
  const location = settingsLocation.value;
  return location?.scope === "space" ? spaces.value.find(item => item.id === location.spaceId) : undefined;
});
const settingsBackLabel = computed(() => settingsReturnLabel(settingsPlace.value, settingsPlace.value === "wiki" ? wikiReturnRoute.value : profileReturnRoute.value));
/** The spaces the switcher of space settings moves between: those the account manages. */
const switcherSpaces = computed(() => managedSpaces({ spaces: spaces.value, isWikiAdmin: !!user.value?.isWikiAdmin }));
/** The same section of another space is another settings surface, so `openSettings` asks about a draft before it moves. */
function switchSettingsSpace(spaceId: string) {
  openSettings(sameSectionInSpace(currentRoute(), spaceId));
}
function leaveSettingsSurface() {
  if (profileOpen.value) { void cancelProfile(); return; }
  const space = settingsSpace.value;
  if (space) { void selectSpace(space); return; }
  // Správa wiki returns where the administrator came from. A draft is asked about before the address moves (Zahodit
  // comes back here); the view that address names then opens as a history navigation.
  if (!guardNavigation(false, leaveSettingsSurface)) return;
  // The remembered address serves this one return: a later arrival by address must not lead back to it.
  const target = wikiReturnRoute.value;
  wikiReturnRoute.value = buildShellHash({ view: "home" });
  history.pushState(null, "", target);
  void historyNavigation();
}
function canOpenSettings(target: SettingsLocation) {
  const owner = user.value;
  if (!owner) return false;
  if (owner.isWikiAdmin) return true;
  if (target.scope !== "space") return false;
  // Stav obsahu is also the space writers' (the server asks for an editor): they get that one section of the space settings.
  const contentHealth = /\/settings\/content-health(?:[/?]|$)/.test(target.hash);
  return spaces.value.some(item => item.id === target.spaceId && !item.itemAccessOnly && (item.role === "Admin" || (contentHealth && item.role === "Writer")));
}
/**
 * Opens global or space settings at a hash. Moving within the open surface is the surface's own
 * business (it asks about unsaved changes itself); arriving from elsewhere goes through the usual
 * leave guard. Old links (`#/settings`, the former tab names) are replaced by their new section.
 * `remember`: a control opens it, so Správa wiki returns to where that control was pressed.
 */
function openSettings(hash: string, fromHistory = false, remember = !fromHistory) {
  const target = resolveSettingsHash(hash);
  if (!target || !canOpenSettings(target)) {
    // A settings link the account may not open lands on the home page, not on a blank surface, and says why (AD5-2).
    if (fromHistory) {
      history.replaceState(null, "", buildShellHash({ view: "home" })); home(true);
      if (target && user.value) toaster.error(target.scope === "space" ? t.toast.spaceSettingsAdminOnly : t.toast.wikiAdminOnly);
    }
    return false;
  }
  const same = administrationOpen.value && sameSettingsSurface(settingsLocation.value, target);
  if (!same) {
    if (!guardNavigation(fromHistory, () => openSettings(hash, false, remember))) return false;
    // Opened by a control (the gear, "Správa wiki" in space settings), Správa wiki remembers where. Reached by its address
    // (the browser's way back, also out of space settings, and the profile's way back into it), it keeps what it remembered.
    if (target.scope === "global" && remember) wikiReturnRoute.value = currentRoute();
    navigationVersion++;
    openingPage.value = false;
    libraryTab.value = undefined;
    page.value = undefined;
    documentValue.value = undefined;
    dirty.value = false;
    editorActive.value = false;
    selectedSpace.value = target.scope === "space" ? target.spaceId : undefined;
    if (target.scope === "space") expandSpace(target.spaceId);
    sidebarOpen.value = false;
    settingsDirty.value = false;
    administrationOpen.value = true;
  }
  settingsLocation.value = target;
  if (target.redirect) {
    history.replaceState(null, "", target.hash);
    // The mounted surface follows the address through hashchange; a replaced entry does not fire it.
    if (same) window.dispatchEvent(new HashChangeEvent("hashchange"));
  } else if (!fromHistory && location.hash !== target.hash) {
    // Within the open surface a real hash change lets its router move (and ask about unsaved changes).
    if (same) location.hash = target.hash;
    else history.pushState(null, "", target.hash);
  }
  return true;
}
async function refreshAccess() {
  const owner = user.value;
  if (!owner) return;
  try {
    const updated = await api<Space[]>("/spaces");
    if (user.value !== owner) return;
    spaces.value = updated;
    // Access changes invalidate the visible tree contents even when the space itself remains reachable.
    treeAccessVersion++;
    treeLoads.clear();
    pagesBySpace.value = {};
    treeVersions.value = {};
    treeFailures.value = new Set();
    libraryRefresh.value++;
    // What was read ahead under the old access is not trusted any more.
    pagePrefetches.clear(); forgetPageReferences(); forgetPageConnections(); contactApi.forget();
    if (selectedSpace.value && !updated.some(space => space.id === selectedSpace.value)) selectedSpace.value = undefined;
    for (const id of expandedSidebarSpaceIds.value) loadSidebarTree(id);
    // Someone who just gave up administering the space leaves its settings.
    const open = settingsLocation.value;
    if (administrationOpen.value && open?.scope === "space" && !canOpenSettings(open)) {
      settingsDirty.value = false;
      home();
    }
    if (
      page.value &&
      !spaces.value.some((space) => space.id === page.value?.spaceId)
    ) {
      if (!dirty.value && profileOpen.value) { page.value = undefined; documentValue.value = undefined; editorActive.value = false; }
      else if (!dirty.value) home();
      else
        error.value =
          t.toast.accessRemoved;
    }
  } catch (failure) {
    if (user.value === owner) error.value = errorMessage(failure);
  }
}
async function refreshProfileAccess() {
  await refreshSession();
  await refreshAccess();
}
function completeSetup() {
  return run(async () => {
    user.value = await post<User>("/setup/complete", setup);
    setup.password = "";
    setup.bootstrapToken = "";
    status.value!.requiresSetup = false;
    // The name was just chosen in the wizard: the brand and the title show it without a reload.
    status.value!.workspaceName = setup.workspaceName.trim() || undefined;
    await authenticated();
    toaster.success(t.toast.wikiReady);
  });
}
/** A component's own unconfirmed input on the page (an OpenAPI YAML draft, the page source draft): the component asks about it. */
const documentDraft = () => detailDirty.value || sourceDirty.value;
/** Something is still being saved or set up: the reader stays until it is done. */
const leaveBlocked = () => moduleSetup.value || profilePending.value || profileBusy.value || templateBusy.value || pdfExportBusy.value;
const unsaved = () => settingsDirty.value || pdfExportDirty.value || profileDirty.value || appearanceDirty.value || templateDirty.value;
/**
 * Whether the reader may go on now. The page asks nothing (3.6-B, decision 7): its last changes go as a checkpoint on the way, and
 * until then they live in the page's shared session. Only a component with an unconfirmed input asks, for itself; other unsaved work
 * (settings, a creation flow, the profile) asks with the shared UnsavedChangesDialog (Zahodit / Zůstat), never window.confirm. Once
 * answered, `resume` continues where the reader was going (`leaveRequest`).
 */
function confirmLeave(fromHistory: boolean, resume?: () => void) {
  if (leaveBlocked()) {
    // Nothing to answer yet: the reader stays, and a step through the browser's history is put back.
    if (fromHistory) { searchUrl(false); history.pushState(null, "", currentRoute()); }
    return false;
  }
  if (documentDraft()) { askComponentDraft(fromHistory, resume); return false; }
  if (unsaved()) { askLeave(fromHistory, resume); return false; }
  leaveCheckpoint();
  return true;
}
/** The component holding an unconfirmed input asks about it itself (3.6-0, rule 2); once it is applied or dropped the reader goes on. */
function askComponentDraft(fromHistory: boolean, resume?: () => void) {
  const target = location.href;
  if (fromHistory) { searchUrl(false); history.pushState(null, "", currentRoute()); }
  const go = resume ?? (() => { history.pushState(null, "", target); void historyNavigation(); });
  void resolveComponentDrafts().then(resolved => { if (resolved) goOn({ resume: go }); });
}
/** Each component with an unconfirmed input asks about it; true once none is left (applied or dropped). */
async function resolveComponentDrafts() {
  if (detailDirty.value && !((await editor.value?.resolveDetailDraft()) ?? true)) return false;
  if (sourceDirty.value && !((await editor.value?.resolveSourceDraft()) ?? true)) return false;
  return true;
}
/**
 * Everything that belongs to the signed-in account, in one place. Logout and a session the
 * server has already ended both come through here, so the next account in this tab starts
 * clean: owner-checked requests in flight are invalidated, search timers stop, and no page,
 * dialog, draft flag, search, personal state or administrator warning outlives its account.
 * Watchers on `page`/`user` also clear some of this; this function must not rely on them.
 */
function resetSessionState() {
  sessionGeneration++;
  // Drafts typed outside the page's editor (a comment, a note) belong to the account, never to the next one in the tab.
  forgetUnsavedWork();
  useAiCapabilities().stop();
  modules.stop();
  assistant.reset(); searchAnswer.resetSession(); workspacePanel.reset();
  // Invalidate every late response that still compares against a version counter.
  navigationVersion++; historyVersion++; backupHealthVersion++; personalVersion++;
  personalAbort?.abort(); personalAbort = undefined;
  profileLeaveGuard.value?.resolve(false); profileLeaveGuard.value = null;
  // Search (stopSearch also stops the search timers).
  stopSearch();
  searchUrl(false);
  query.value = "";
  resetSearchFilters();
  filtersOpen.value = false;
  results.value = []; searchSpaces.value = []; searchResources.value = []; suggestedPages.value = [];
  searchNextOffset.value = null; hasMore.value = false; searchIndex.value = 0;
  searchError.value = ""; searchAttempted.value = false;
  searchViewToRestore = undefined;
  // Profile (also clears the page-scoped message and its timer) and administration.
  resetProfile();
  profileTab.value = "account"; profileBusy.value = false; profileReturnRoute.value = buildShellHash({ view: "home" }); wikiReturnRoute.value = buildShellHash({ view: "home" });
  toaster.dismissAll();
  administrationOpen.value = false; settingsDirty.value = false; moduleSetup.value = false; settingsLocation.value = undefined;
  pageAccessTab.value = undefined;
  whatsNewOpen.value = false;
  boardId.value = undefined;
  showSpaceForm.value = false;
  resetTemplateWorkspace();
  backupWarning.value = null;
  // Navigation and the open document.
  openingPage.value = false;
  libraryTab.value = undefined;
  selectedSpace.value = undefined; sidebarOpen.value = false;
  page.value = undefined; documentValue.value = undefined;
  title.value = ""; requestedHeading.value = undefined; requestedBlock.value = undefined;
  editorActive.value = false; dirty.value = false; detailDirty.value = false; sourceDirty.value = false; editorReady.value = false;
  editorActions.value = []; historyOpen.value = false; pdfExportOpen.value = false; pdfDownloadOpen.value = false; readershipOpen.value = false; organizationOpen.value = undefined;
  collaborationSaveReady.value = false; collaborationStatus.value = null; collaborators.value = [];
  personalState.value = undefined; personalError.value = ""; favoritePending.value = false;
  // Spaces and the tree.
  spaces.value = []; spacesLoaded.value = false;
  pagesBySpace.value = {};
  // A shared tree load or a start-up page request of the previous account must not serve the next one.
  treeLoads.clear(); pagePrefetches.clear(); forgetPageReferences(); forgetPageConnections(); contactApi.forget();
  treeVersions.value = {}; treeDrag.value = undefined; rootDrop.value = undefined;
  openedSpaces.value = new Set(); collapsedPages.value = new Set(); treeFailures.value = new Set(); pinPendingIds.value = new Set();
  // The account itself.
  clearCsrf();
  user.value = undefined;
  location.hash = "";
  Object.assign(appearance, {
    layout: "connected",
    accent: "ocean",
    mode: "system",
    motion: "system",
  });
}
async function logout() {
  if (!confirmLeave(false, () => void logout())) return;
  await run(async () => {
    await post("/auth/logout");
    await crossFade(resetSessionState);
  });
}
async function loadTree(spaceId: string) {
  const owner = user.value;
  if (!owner) return;
  const accessVersion = treeAccessVersion;
  let tree;
  try {
    tree = await (takePrefetchedTree(spaceId) ?? organizationApi.tree(spaceId));
  } catch (failure) {
    if (user.value === owner && accessVersion === treeAccessVersion && failure instanceof ApiError && (failure.status === 403 || failure.status === 404)) {
      spaces.value = spaces.value.filter(space => space.id !== spaceId);
      delete pagesBySpace.value[spaceId];
      delete treeVersions.value[spaceId];
      if (selectedSpace.value === spaceId) selectedSpace.value = undefined;
    }
    throw failure;
  }
  if (user.value !== owner || accessVersion !== treeAccessVersion) return;
  if (treeVersions.value[spaceId] !== undefined && tree.version < treeVersions.value[spaceId]!) return;
  treeVersions.value[spaceId] = tree.version;
  rootByTitle.value[spaceId] = !!tree.rootByTitle;
  treeLoadedAt.set(spaceId, Date.now());
  const items = tree.items.map(item => ({ ...item, spaceId }));
  // An unchanged answer keeps the rendered rows; replacing them would patch every row of the tree.
  const current = pagesBySpace.value[spaceId];
  if (!current || JSON.stringify(current) !== JSON.stringify(items)) pagesBySpace.value[spaceId] = items;
  if (page.value?.spaceId === spaceId) revealPageInTree(page.value.id, spaceId);
}
// Navigation only needs a tree to exist, so concurrent navigations share one request. Mutations call
// loadTree, which always asks again: an answer started before the mutation must not stand for it.
const treeLoads = new Map<string, Promise<void>>();
let treeAccessVersion = 0;
function ensureTree(spaceId: string) {
  let load = treeLoads.get(spaceId);
  if (!load) {
    load = loadTree(spaceId).finally(() => { if (treeLoads.get(spaceId) === load) treeLoads.delete(spaceId); });
    treeLoads.set(spaceId, load);
  }
  return load;
}
/**
 * A page read ahead (3.6-G N2): the page the URL names, together with the session check during start-up, and a page pointed at, focused
 * or touched before it is opened. Kept 10 s, one intent at a time (the next one cancels a read still on its way), forgotten when access
 * changes. Every read brings the page's bundle (`?bundle=true`: personal state, tags, contacts, linked pages and the live session) with the
 * session's Yjs state the editor builds from beside it (readPageBundle, 3.6); a page opened without a read ahead records the visit in that
 * read (`visit`).
 */
const PAGE_PREFETCH_TTL_MS = 10_000;
const pagePrefetches = new PagePrefetchMap<{ at: number; request: Promise<Page>; abort?: AbortController }>(PAGE_PREFETCH_TTL_MS);
function prefetchPage(id: string, abort?: AbortController) {
  const request = readPageBundle(id, "", abort?.signal);
  request.catch(() => { if (pagePrefetches.get(id)?.request === request) pagePrefetches.delete(id); });
  pagePrefetches.set(id, { at: Date.now(), request, ...(abort ? { abort } : {}) });
}
let intentPrefetch: string | undefined;
function prefetchOnIntent(id: string) {
  const known = pagePrefetches.get(id);
  if (id === page.value?.id || (known && Date.now() - known.at < PAGE_PREFETCH_TTL_MS)) return;
  if (intentPrefetch && intentPrefetch !== id) { pagePrefetches.get(intentPrefetch)?.abort?.abort(); pagePrefetches.delete(intentPrefetch); }
  intentPrefetch = id;
  prefetchPage(id, new AbortController());
  void pagePrefetches.get(id)!.request.finally(() => { if (intentPrefetch === id) intentPrefetch = undefined; }).catch(() => {});
}
/** The page whose read recorded its visit (`visit`): the shell does not post it again. */
let visitedByRead: string | undefined;
/** When each space's tree was read last: switching pages reads it again only after a while (3.6-G N7). */
const treeLoadedAt = new Map<string, number>();
// shortcut: no live signal of tree changes reaches the shell, so a colleague's move shows after this or with the next own change; use one when it exists.
const TREE_FRESH_MS = 30_000;
// So is the tree of a space the URL names: its page list needs nothing else, so it does not wait for the session and the spaces.
const treePrefetches = new Map<string, ReturnType<typeof organizationApi.tree>>();
function prefetchTree(spaceId: string) {
  const request = organizationApi.tree(spaceId);
  request.catch(() => {});
  treePrefetches.set(spaceId, request);
}
function takePrefetchedTree(spaceId: string) {
  const prefetched = treePrefetches.get(spaceId);
  treePrefetches.delete(spaceId);
  return prefetched;
}
function fetchPage(id: string) {
  const prefetched = pagePrefetches.get(id);
  pagePrefetches.delete(id);
  if (intentPrefetch === id) intentPrefetch = undefined;
  if (prefetched && Date.now() - prefetched.at < PAGE_PREFETCH_TTL_MS) return prefetched.request;
  visitedByRead = id;
  return readPageBundle(id, "&visit=true");
}
/** What came with the page: its contacts and linked pages ask nothing more for a while. */
function takeBundle(next: Page) {
  if (next.bundle?.contacts) contactApi.seed(next.bundle.contacts.items);
  if (next.bundle?.pages) seedPageReferences(next.bundle.pages.items);
}

async function refreshPublishedSpace(spaceId: string) {
  libraryRefresh.value++;
  // The tab keeps its count until Obsah counts the refreshed space again (its list reloads with the tree): it never blinks.
  try { await loadTree(spaceId); }
  catch (reason) { toaster.error(errorMessage(reason)); }
}

function pageDecorationSaved(decoration: PageDecoration | null) {
  const activePage = page.value;
  if (!activePage) return;
  activePage.decoration = decoration;
  const item = pagesBySpace.value[activePage.spaceId]?.find(item => item.id === activePage.id);
  if (item) item.decoration = decoration;
}

function restoreTreeExpansion(accountId: string) {
  const restored = readTreeExpansion(browserStorage(), accountId);
  openedSpaces.value = restored.openedSpaces;
  collapsedPages.value = restored.collapsedPages;
}
function persistTreeExpansion() {
  if (user.value) writeTreeExpansion(browserStorage(), user.value.id, { openedSpaces: openedSpaces.value, collapsedPages: collapsedPages.value });
}
/** Going to a space opens its tree in the sidebar; it stays open until the reader closes it. */
function expandSpace(spaceId: string) {
  if (openedSpaces.value.has(spaceId)) return;
  openedSpaces.value = new Set(openedSpaces.value).add(spaceId);
  persistTreeExpansion();
}
function toggleSpaceExpanded(spaceId: string) {
  const next = new Set(openedSpaces.value);
  if (next.has(spaceId)) next.delete(spaceId); else next.add(spaceId);
  openedSpaces.value = next;
  persistTreeExpansion();
}
// A space that enters or leaves the rail's list (pinned, unpinned, the open space of another place) opens and closes its
// place: the item is revealed where it lands and folds away, the list's gap with it, on the tokens of every unfolding.
// It used to appear and vanish in one frame, moving every row below by a whole row. A leaving item is out of use at
// once. The list's first render is not animated.
// The item keeps its own height, which may still grow while it enters (the open space's tree loads): its place opens
// through the margin under it and it shows through a clip of its own box. An animated height would hold it at the
// height it had when it arrived and let go of it in one frame.
function sidebarSpaceFrames(item: HTMLElement): Keyframe[] {
  const gap = Number.parseFloat(getComputedStyle(item.parentElement ?? item).rowGap) || 0;
  return [
    { marginTop: "0px", marginBottom: `${-(item.getBoundingClientRect().height + gap)}px`, clipPath: "inset(0 0 100% 0)", opacity: 0 },
    { clipPath: "inset(0 0 0 0)", opacity: 1 },
  ];
}
function sidebarSpaceEnters(el: Element, done: () => void) {
  const item = el as HTMLElement;
  const animation = animateAtlas(item, sidebarSpaceFrames(item), { duration: motionMs(item, "--atlas-motion-slow"), fill: "none" });
  if (animation) animation.finished.then(done, done); else done();
}
function sidebarSpaceLeaves(el: Element, done: () => void) {
  const item = el as HTMLElement;
  const frames = sidebarSpaceFrames(item).reverse();
  item.setAttribute("inert", "");
  item.setAttribute("aria-hidden", "true");
  const animation = animateAtlas(item, frames, { duration: literalMs(item, PROTOTYPE_MS.collapse), fill: "forwards" });
  if (animation) animation.finished.then(done, done); else done();
}
function loadSidebarTree(spaceId: string) {
  const owner = user.value;
  ensureTree(spaceId).catch(() => {
    if (user.value === owner) treeFailures.value = new Set(treeFailures.value).add(spaceId);
  });
}
/**
 * The reader presses the arrow of a space whose tree has not loaded: ask for it now, a moment ahead of the click.
 * A failure is not recorded here; the unfolding asks again and reports its own.
 */
function loadSidebarTreeAhead(spaceId: string) {
  if (!pagesBySpace.value[spaceId] && !treeFailures.value.has(spaceId)) ensureTree(spaceId).catch(() => {});
}
function retrySidebarTree(spaceId: string) {
  const next = new Set(treeFailures.value);
  next.delete(spaceId);
  treeFailures.value = next;
  loadSidebarTree(spaceId);
}
// Only expanded spaces load their trees, each once: expanding again reuses it (navigation and changes revalidate).
// Routed navigation loads its own space's tree; the other expanded ones wait until the start-up view is on screen.
watch([expandedSidebarSpaceIds, loading], ([ids, starting]) => {
  if (starting) return;
  for (const id of ids) if (!pagesBySpace.value[id] && !treeFailures.value.has(id)) loadSidebarTree(id);
});
function setPagesCollapsed(spaceId: string, ids: readonly string[], collapse: boolean) {
  const next = new Set(collapsedPages.value);
  for (const id of ids) if (collapse) next.add(id); else next.delete(id);
  // The open page stays reachable.
  collapsedPages.value = collapse && page.value?.spaceId === spaceId
    ? revealTreeAncestors(pagesBySpace.value[spaceId] ?? [], page.value.id, next) : next;
  persistTreeExpansion();
}
/**
 * Where something new in `space` goes: under the open page, beside the open board (its parent), else at the root. Beside a catalog
 * page, not under it (U3-12): what is made from a catalog is a deployment or a page of its own, never a part of the catalog.
 */
function creationParent(space: Space) {
  if (page.value?.spaceId === space.id && !page.value.isTemplate && catalogPage.value) return page.value.parentId ?? "";
  if (page.value) return page.value.spaceId === space.id && !page.value.isTemplate ? page.value.id : "";
  return boardId.value && treeSpaceId.value === space.id ? boardNode.value?.parentId ?? "" : "";
}
function createPageInSpace(space: Space) {
  void openTemplateWorkspace(space, "new-page", false, creationParent(space));
}
function createBoardInSpace(space: Space) {
  if (boardsEnabled.value) void openTemplateWorkspace(space, "new-board", false, creationParent(space));
}
/** The sidebar row of a space is the current place: its overview is open. */
function spaceOverviewOpen(space: Space) {
  return currentSpace.value?.id === space.id && !page.value && !boardId.value && !templateView.value && !libraryTab.value && !administrationOpen.value && !profileOpen.value;
}
// The templates O prostoru lists are read while that tab is on screen. Coming back to it from a template or from the
// settings of the same space reads them again: they may have changed there.
const { items: spaceTemplates, loading: spaceTemplatesLoading, error: spaceTemplatesError, reload: reloadSpaceTemplates } =
  useSpaceTemplates(() => spaceView.value === "about" && currentSpace.value && spaceOverviewOpen(currentSpace.value) ? currentSpace.value.id : undefined);
function setSpacePinnedLocally(spaceId: string, pinned: boolean) {
  spaces.value = spaces.value.map(item => item.id === spaceId ? { ...item, pinned } : item);
}
/**
 * The account's own pin, shown at once and taken back when the server refuses it. A space the
 * administrator pinned for everyone answers 409 `space_pin_required`, a space the account no longer
 * sees 404: both reload the list of spaces, since this copy of it is out of date.
 */
async function pinSpace(space: Space, pinned: boolean) {
  const owner = user.value;
  if (!owner || pinPendingIds.value.has(space.id) || (!pinned && space.pinPolicy === "Required")) return;
  const previous = space.pinned;
  // A space leaving the sidebar takes the focused menu with it; focus moves to Knihovna, which lists all spaces.
  const leavesSidebar = !pinned && currentSpace.value?.id !== space.id;
  pinPendingIds.value = new Set(pinPendingIds.value).add(space.id);
  // A space that stays in the rail changes its place in the list (pinned ones come first, the open one last). The
  // list scrolls by as much as its row moved, so the row stays under the reader's pointer instead of leaving the view.
  const row = leavesSidebar ? null : sidebar.value?.querySelector<HTMLElement>(`[data-sidebar-space="${CSS.escape(space.id)}"] .sidebar-space-row`);
  const rowTop = row?.getBoundingClientRect().top;
  setSpacePinnedLocally(space.id, pinned);
  if (row && rowTop !== undefined) void nextTick(() => {
    const list = row.closest<HTMLElement>(".spaces-scroll");
    if (list && row.isConnected) list.scrollTop += row.getBoundingClientRect().top - rowTop;
  });
  if (leavesSidebar) void nextTick(() => requestAnimationFrame(() => {
    if (!document.activeElement || document.activeElement === document.body) sidebar.value?.querySelector<HTMLElement>(".sidebar-library")?.focus();
  }));
  try {
    const saved = await setSpacePinned(space.id, pinned);
    if (user.value === owner) setSpacePinnedLocally(space.id, saved.pinned);
  } catch (failure) {
    if (user.value !== owner) return;
    setSpacePinnedLocally(space.id, previous);
    if (failure instanceof ApiError && failure.code === SPACE_PIN_REQUIRED) {
      toaster.error(t.toast.pinnedByAdmin);
      void refreshAccess();
    } else if (failure instanceof ApiError && failure.status === 404) {
      toaster.error(t.toast.spaceUnavailable);
      void refreshAccess();
    } else toaster.error(errorMessage(failure));
  } finally {
    if (user.value === owner) {
      const next = new Set(pinPendingIds.value);
      next.delete(space.id);
      pinPendingIds.value = next;
    }
  }
}
function togglePageCollapsed(pageId: string) {
  const next = new Set(collapsedPages.value);
  if (next.has(pageId)) next.delete(pageId); else next.add(pageId);
  collapsedPages.value = next;
  persistTreeExpansion();
}
function revealPageInTree(pageId: string, spaceId: string) {
  const next = revealTreeAncestors(pagesBySpace.value[spaceId] ?? [], pageId, collapsedPages.value);
  if (next.size !== collapsedPages.value.size || [...next].some(id => !collapsedPages.value.has(id))) {
    collapsedPages.value = next;
    persistTreeExpansion();
  }
  void nextTick(scrollCurrentTreeRowIntoView);
}
// Scrolls only the sidebar list. scrollIntoView would also scroll the page, and this runs on every
// tree refresh: on a tablet, whose viewport resizes while it scrolls, the article jumped under the reader.
function scrollCurrentTreeRowIntoView() {
  const row = document.querySelector<HTMLElement>('.tree-page[aria-current="page"]');
  const list = row?.closest<HTMLElement>('.spaces-scroll');
  if (!row || !list) return;
  const place = () => {
    // Clear of the 20 px in which the list fades towards what waits beyond its edge (refresh.css §2).
    const margin = 28, rowBox = row.getBoundingClientRect(), listBox = list.getBoundingClientRect();
    if (rowBox.top < listBox.top + margin) list.scrollTop -= listBox.top + margin - rowBox.top;
    else if (rowBox.bottom > listBox.bottom - margin) list.scrollTop += rowBox.bottom - (listBox.bottom - margin);
  };
  // Read once a frame has painted, never in the middle of one: a read at once or in an animation frame laid out the whole page while
  // it changed (a board opening, 100 ms each on 1430 items).
  afterPaint(() => { if (row.isConnected) place(); });
  // A tree or a branch that still unfolds clips the list to its animated height: the list cannot scroll past the clip,
  // so the row of a page opened in a folded space stayed out of view. It is placed again with every frame until the
  // unfolding ends.
  const unfolding = () => typeof list.getAnimations === "function" && typeof KeyframeEffect === "function"
    && list.getAnimations({ subtree: true }).some(animation => animation.playState === "running"
      && animation.effect instanceof KeyframeEffect && animation.effect.getKeyframes().some(frame => "height" in frame));
  const follow = () => { if (row.isConnected && unfolding()) afterPaint(() => { place(); follow(); }); };
  follow();
}
function startTreeDrag(spaceId: string, source: TreePage, event: DragEvent) {
  if (pending.value || moduleSetup.value || !source.canEdit || treeVersions.value[spaceId] === undefined) { event.preventDefault(); return; }
  // A page's own unsaved changes save by themselves and a move in the tree leaves its content alone (3.6); a settings form does not.
  if (settingsDirty.value) {
    event.preventDefault(); toaster.error(t.toast.saveBeforeMove); return;
  }
  treeDrag.value = { spaceId, id: source.id, version: treeVersions.value[spaceId]!, type: source.type };
}
function endTreeDrag() { treeDrag.value = undefined; rootDrop.value = undefined; }
function rootDragOver(spaceId: string, event: DragEvent) {
  const space = spaces.value.find(item => item.id === spaceId);
  // From outside this space's tree: a reference node at the end of its root (spec 2026-10-27 §5.4).
  if (treeDrag.value?.spaceId !== spaceId && event.dataTransfer && carriesReference(event.dataTransfer.types)) {
    if (pending.value || !space || !spaceCanWrite(space)) return;
    event.preventDefault(); rootDrop.value = spaceId; event.dataTransfer.dropEffect = "copy";
    return;
  }
  if (!treeDrag.value || treeDrag.value.spaceId !== spaceId || pending.value || !resolveTreeMove(pagesBySpace.value[spaceId] ?? [], treeDrag.value.id, { targetId: null, position: "inside" })) return;
  event.preventDefault(); rootDrop.value = spaceId;
  if (event.dataTransfer) event.dataTransfer.dropEffect = "move";
}
function dropTreeRoot(spaceId: string, event: DragEvent) {
  const root: TreeDrop = { targetId: null, position: "inside" };
  if (treeDrag.value?.spaceId === spaceId) return dropTree(spaceId, root);
  const target = event.dataTransfer ? readReferenceDrop(event.dataTransfer) : undefined;
  endTreeDrag();
  if (target) void insertTreeReference(spaceId, root, target);
}
/**
 * A reference node dropped into the tree (spec 2026-10-27 §13.1, 3): it appears at once where it was dropped and glides in, the
 * server's node takes its place; refused, it leaves again and a toast says why.
 */
async function insertTreeReference(spaceId: string, destination: TreeDrop, target: ReferenceDrop) {
  const space = spaces.value.find(item => item.id === spaceId);
  const place = resolveTreeInsert(pagesBySpace.value[spaceId] ?? [], destination);
  if (!space || !spaceCanWrite(space) || !place || pending.value) return;
  // A page of this very tree, dragged by someone who may not move it, meant a move: say why rather than add a reference to it.
  const own = !target.anchor && target.typeId === "atlas.page" ? pagesBySpace.value[spaceId]?.find(item => item.id === target.targetId && isPageNode(item)) : undefined;
  if (own && !own.canEdit) { toaster.error(t.toast.pageMoveRefused(plainTitle(own.title))); return; }
  const pendingId = `pending-${crypto.randomUUID()}`;
  const optimistic: TreePage = { id: pendingId, spaceId, parentId: place.parentId, title: target.title ?? "…", version: 0, isPublished: true, canEdit: false,
    type: "reference", targetId: target.targetId, targetTypeId: target.typeId, anchor: target.anchor ?? null, readable: true };
  const at = (items: TreePage[]) => { const index = place.beforePageId ? items.findIndex(item => item.id === place.beforePageId) : -1; return index < 0 ? items.length : index; };
  const items = [...(pagesBySpace.value[spaceId] ?? [])];
  items.splice(at(items), 0, optimistic);
  pagesBySpace.value[spaceId] = items;
  if (place.parentId) revealPageInTree(place.parentId, spaceId);
  try {
    const node = await treeReferenceApi.add(spaceId, { targetId: target.targetId, anchor: target.anchor ?? null, parentId: place.parentId, beforeId: place.beforePageId });
    pagesBySpace.value[spaceId] = (pagesBySpace.value[spaceId] ?? []).map(item => item.id === pendingId ? { ...node, title: node.title ?? "", spaceId } : item);
    await loadTree(spaceId).catch(() => {});
  } catch (failure) {
    pagesBySpace.value[spaceId] = (pagesBySpace.value[spaceId] ?? []).filter(item => item.id !== pendingId);
    toaster.error(failure instanceof ApiError && failure.status === 404 ? t.toast.cannotAddToTree : errorMessage(failure));
  }
}
/** What the row menu or a row's key asks for (PageTree's TreeAction); the existing flows do it, with their confirmations. */
async function treeAction(spaceId: string, action: TreeAction, node: TreePage) {
  const space = spaces.value.find(item => item.id === spaceId);
  if (!space) return;
  switch (action) {
    case "open": openTreeNode(spaceId, node); return;
    case "openAttached": if (node.attachedBoardId) openBoard(node.attachedBoardId, false, spaceId); return;
    case "copyLink": {
      const href = contentItemLink(node);
      if (!href) return;
      try { await navigator.clipboard.writeText(new URL(href, location.href).href); toaster.success(t.toast.linkCopied); }
      catch { toaster.error(t.toast.linkCopyFailed); }
      return;
    }
    case "newPage": void openTemplateWorkspace(space, "new-page", false, node.id); return;
    case "newBoard": if (boardsEnabled.value) void openTemplateWorkspace(space, "new-board", false, node.id); return;
    case "sortByTitle": await sortTreeByTitle(spaceId, node.id, !node.childrenByTitle); return;
    case "remove": {
      const { parentId, beforePageId } = treePlaceOf(pagesBySpace.value[spaceId] ?? [], node.id);
      await run(async () => {
        await treeReferenceApi.remove(node.id);
        await loadTree(spaceId);
        // Zpět puts the same link back where it stood.
        toaster.success(t.toast.linkRemovedFromTree, node.targetId ? { undo: () => void runUndo(async () => {
          await treeReferenceApi.add(spaceId, { targetId: node.targetId!, anchor: node.anchor ?? null, parentId, beforeId: beforePageId });
          await loadTree(spaceId);
        }) } : undefined);
      });
      return;
    }
    case "move":
    case "trash":
      // The page's or board's own organization flow (impact, confirmation, toast), opened on it.
      if (isBoardNode(node)) {
        openBoard(node.id, false, spaceId);
        if (boardId.value !== node.id) return;
        if (action === "move") organizationOpen.value = "board"; else { boardTrashOpen.value = true; boardTrashError.value = ""; }
        return;
      }
      await openPage(node.id);
      if (page.value?.id === node.id) organizationOpen.value = action === "move" ? "page" : "pageTrash";
      return;
  }
}
/** F2 or a double click on a row: the title as a working revision, like „Přesunout…“ (publishing it is the page's own step). Zpět names it back. */
/** A rename does not take the app's one-operation lock (run): the next one in the tree must not be dropped while this one saves (P17). Saves of one page go one after another. */
const renaming = new Map<string, Promise<void>>();
function renameTreePage(spaceId: string, node: TreePage, title: string, undoing = false) {
  // The row shows the new title at once (P17); the server's answer replaces it, a refusal puts the old one back and says so in a toast.
  const show = (value: string) => { pagesBySpace.value[spaceId] = (pagesBySpace.value[spaceId] ?? []).map(item => item.id === node.id ? { ...item, title: value } : item); };
  show(title);
  const save = async () => {
    // The version the earlier save left, not the one this rename started from.
    const version = pagesBySpace.value[spaceId]?.find(item => item.id === node.id)?.version ?? node.version;
    let renamed: Page;
    try { renamed = await organizationApi.rename(node.id, title, version); }
    catch (failure) { show(node.title); toaster.error(errorMessage(failure)); return; }
    try {
      if (page.value?.id === renamed.id) await pageRenamed(renamed);
      else await loadTree(spaceId);
    } catch (failure) { toaster.error(errorMessage(failure)); }
    // The server writes the name back as one more working revision, so a published page stays among those with unpublished changes.
    if (undoing) { toaster.success(t.toast.titleBackInDraft); return; }
    toaster.success(t.toast.titleChangedInDraft, { undo: () => {
      const current = pagesBySpace.value[spaceId]?.find(item => item.id === node.id);
      if (current) void renameTreePage(spaceId, current, node.title, true);
    } });
  };
  const queued: Promise<void> = (renaming.get(node.id) ?? Promise.resolve()).then(save).finally(() => { if (renaming.get(node.id) === queued) renaming.delete(node.id); });
  renaming.set(node.id, queued);
  return queued;
}
const sidebarSpaceViews = new Map<string, { openFilter(typed?: string): void }>();
function treeFilterShortcut() {
  const spaceId = treeSpaceId.value ?? currentSpace.value?.id;
  const view = spaceId ? sidebarSpaceViews.get(spaceId) : undefined;
  if (!view) return false;
  sidebarOpen.value = true;
  view.openFilter();
  return true;
}
async function searchWholeWiki(text: string) {
  await openSearch();
  query.value = text;
}
function dropTree(spaceId: string, destination: TreeDrop) {
  // A keyboard move (Alt+arrows on a tree row) names its source; a drag has named it at its start.
  const moved = destination.sourceId ? pagesBySpace.value[spaceId]?.find(item => item.id === destination.sourceId) : undefined;
  const source = treeDrag.value ?? (moved && treeVersions.value[spaceId] !== undefined ? { spaceId, id: moved.id, version: treeVersions.value[spaceId]!, type: moved.type } : undefined);
  const byKeyboard = !!destination.sourceId;
  endTreeDrag();
  if (!source || source.spaceId !== spaceId || pending.value || moduleSetup.value) return;
  if (settingsDirty.value) { toaster.error(t.toast.saveBeforeMove); return; }
  const space = spaces.value.find(item => item.id === spaceId);
  if (!space || (!user.value?.isWikiAdmin && space.role === "Reader")) return;
  const move = resolveTreeMove(pagesBySpace.value[spaceId] ?? [], source.id, destination);
  if (!move) return;
  const previous = treePlaceOf(pagesBySpace.value[spaceId] ?? [], source.id);
  const back = () => void runUndo(() => commitTreeMove(spaceId, { ...source, version: treeVersions.value[spaceId] ?? source.version }, previous, false));
  return run(() => commitTreeMove(spaceId, source, move, byKeyboard, back));
}
/** Moves a tree node (a drop, a key, or the Zpět of one) and says so; `undo` is what Zpět of the toast does, none for a move that is one. */
async function commitTreeMove(spaceId: string, source: { id: string; version: number; type?: TreeNodeType }, move: { parentId: string | null; beforePageId: string | null }, byKeyboard: boolean, undo?: () => void) {
  let titleOrderCancelled: boolean | undefined;
  try {
    ({ titleOrderCancelled } = await organizationApi.move(source.id, move.parentId, move.beforePageId, source.version, source.type));
  } catch (failure) {
    if (failure instanceof ApiError && failure.status === 409) {
      await loadTree(spaceId);
      throw new Error(t.toast.structureChanged);
    }
    throw failure;
  }
  // Nested into a folded page: it unfolds, so the moved row stays in view.
  if (move.parentId && collapsedPages.value.has(move.parentId)) setPagesCollapsed(spaceId, [move.parentId], false);
  await loadTree(spaceId);
  const active = pagesBySpace.value[spaceId]?.find(item => item.id === page.value?.id);
  if (page.value?.spaceId === spaceId && active) page.value = { ...page.value, parentId: active.parentId };
  libraryRefresh.value++;
  // One toast for a run of Alt+arrow steps: each step replaces the last, so they do not pile up (Zpět undoes the step shown).
  if (titleOrderCancelled) announceTitleOrderCancelled(spaceId, move.parentId);
  else toaster.success(!undo ? t.toast.moveUndone : source.type === "board" ? t.toast.boardMoved : t.toast.pageMoved, { id: "tree-move", ...(undo ? { undo } : {}) });
  // The keyboard stays on the row it moved, wherever it now stands: the tree has rendered it by the next tick.
  if (byKeyboard) { await nextTick(); document.querySelector<HTMLElement>(`.tree-page[data-tree-item="${CSS.escape(source.id)}"]`)?.focus(); }
}
/**
 * „Řadit abecedně“ of a level (a page's children, or `null` the space root): ticked, the level stays sorted by title (the server
 * orders it); unticked, it keeps the order it showed, so Zpět ticks it again and the order is exactly as it was.
 */
function sortTreeByTitle(spaceId: string, parentId: string | null, byTitle: boolean) {
  return run(async () => {
    await organizationApi.sortByTitle(spaceId, parentId, byTitle);
    await loadTree(spaceId);
    if (byTitle) toaster.success(t.toast.sortedAlpha, { id: "tree-sort" });
    else toaster.success(t.toast.sortAlphaOff, { id: "tree-sort", undo: () => void sortTreeByTitle(spaceId, parentId, true) });
  });
}
/** A hand move within a level sorted by title ended that: Zpět sorts it again, which puts the moved node back in its place too. */
function announceTitleOrderCancelled(spaceId: string, parentId: string | null) {
  toaster.success(t.toast.sortAlphaCanceled, { id: "tree-move", undo: () => void runUndo(async () => {
    await organizationApi.sortByTitle(spaceId, parentId, true);
    await loadTree(spaceId);
  }) });
}
/** „Přesouvat stránky tažením“ from the tree's menu: saved with the account's appearance at once, as the profile's switch saves it. */
async function setTreeDrag(on: boolean) {
  const owner = user.value;
  if (!owner) return;
  try {
    const saved = await put<Appearance>("/profile/appearance", { ...owner.appearance, dragPages: on });
    if (user.value !== owner) return;
    owner.appearance = { ...owner.appearance, dragPages: saved.dragPages };
    appearance.dragPages = saved.dragPages;
    toaster.success(on ? t.toast.dragOn : t.toast.dragOff);
  } catch (failure) { toaster.error(errorMessage(failure)); }
}
/** A row of the tree opens what it is: a page, or a board in the same frame; a reference node its target (a page at its heading or
 * block, a board, any other object's detail in the dock). */
function openTreeNode(spaceId: string, node: TreePage) {
  if (isReferenceNode(node)) {
    if (node.readable === false || !node.targetId) { toaster.error(t.toast.noAccessToTarget); return; }
    if (node.targetTypeId === "atlas.board") openBoard(node.targetId);
    else if (node.targetTypeId === "atlas.page") void openPage(node.targetId, false, false, node.anchor ?? undefined, undefined, node.anchor ?? undefined); // a heading or a requirement block: the editor tells them apart
    else void import("./objects/objectDetail").then(({ showObjectDetail }) => showObjectDetail({ id: node.targetId!, typeId: node.targetTypeId ?? "", title: node.title, spaceId: node.spaceId },
      { onOpen: (id, typeId) => { if (typeId === "atlas.board") openBoard(id); else if (typeId === "atlas.page") void openPage(id); } }));
    return;
  }
  if (isBoardNode(node)) openBoard(node.id, false, spaceId);
  else void openPage(node.id);
}
/** An item of Knihovna, the profile's visits or a space's list. */
function openContent(id: string, type?: TreeNodeType, spaceId?: string) {
  if (type === "board") openBoard(id, false, spaceId);
  else void openPage(id);
}
function currentRoute() {
  if (profileOpen.value) return buildShellHash({ view: "profile", tab: profileTab.value });
  if (administrationOpen.value) {
    // Inside the surface the address moves without the shell; it is current as long as it belongs there.
    const here = resolveSettingsHash(location.hash);
    return here && sameSettingsSurface(here, settingsLocation.value) ? location.hash : (settingsLocation.value?.hash ?? globalSettingsHome);
  }
  if (whatsNewOpen.value) return buildShellHash({ view: "whatsNew" });
  if (boardId.value) return buildShellHash({ view: "board", boardId: boardId.value });
  if (libraryTab.value) return buildShellHash({ view: "library", tab: libraryTab.value });
  if (templateView.value && selectedSpace.value) return buildShellHash({ view: "pageCreation", spaceId: selectedSpace.value, kind: templateView.value });
  if (showSpaceForm.value) return buildShellHash({ view: "spaceCreation" });
  if (unavailablePage.value) return buildShellHash({ view: "page", pageId: unavailablePage.value });
  if (unavailableSpace.value) return buildShellHash({ view: "space", spaceId: unavailableSpace.value });
  return buildShellHash(page.value
    ? pageAccessTab.value ? { view: "pageAccess", pageId: page.value.id, tab: pageAccessTab.value } : { view: "page", pageId: page.value.id, heading: requestedHeading.value, block: requestedBlock.value }
    : selectedSpace.value
      ? { view: "space", spaceId: selectedSpace.value, tab: spaceAddressTab(spaceTab.value) }
      : { view: "home" });
}
/**
 * The open leave question: Zahodit drops every draft (settings, a creation flow, the profile) and continues where the reader was
 * going (`resume`, or the history entry).
 */
const leaveRequest = ref<{ resume: () => void } | null>(null);
/** Runs once the leave question has closed (UnsavedChangesDialog `closed`); a new question drops one still waiting. */
let afterLeaveQuestion: (() => void) | undefined;
function askLeave(fromHistory: boolean, resume?: () => void) {
  afterLeaveQuestion = undefined;
  const target = location.href;
  if (fromHistory) { searchUrl(false); history.pushState(null, "", currentRoute()); }
  leaveRequest.value = { resume: resume ?? (() => { history.pushState(null, "", target); void historyNavigation(); }) };
}
function discardAndLeave() {
  const next = leaveRequest.value;
  leaveRequest.value = null;
  settingsDirty.value = false;
  templateDirty.value = false;
  if (pdfExportDirty.value) { pdfExportDirty.value = false; pdfExportOpen.value = false; }
  resetProfile();
  goOn(next);
}

/** The reader has decided to go: a search they went from closes as an answered navigation does. */
function goOn(next: { resume: () => void } | null) {
  closeSearch();
  next?.resume();
}
function guardNavigation(fromHistory: boolean, resume?: () => void) {
  if (!confirmLeave(fromHistory, resume)) return false;
  resetProfile(); resetTemplateWorkspace(); whatsNewOpen.value = false; boardId.value = undefined;
  return true;
}
function goBack() {
  if (history.length > 1) history.back(); else home();
}
function home(fromHistory = false) {
  if (!guardNavigation(fromHistory, () => home())) return;
  navigationVersion++;
  openingPage.value = false;
  libraryTab.value = undefined;
  administrationOpen.value = false;
  page.value = undefined;
  documentValue.value = undefined;
  dirty.value = false;
  editorActive.value = false;
  selectedSpace.value = undefined;
  sidebarOpen.value = false;
  const address = buildShellHash({ view: "home" });
  if (!fromHistory && location.hash !== address) history.pushState(null, "", address);
}
/** The tabs with an address of their own: O prostoru and Tabule. */
const spaceAddressTab = (tab: SpaceDetailTab) => (tab === "about" || tab === "contacts" || tab === "boards" || isProjectorTab(tab) ? tab : undefined);
/** Opens a space's overview at one of its tabs. Only O prostoru and Tabule have an address of their own; the others show at the space's. */
async function selectSpace(space: Space, fromHistory = false, tab: SpaceDetailTab = "pages") {
  if (!guardNavigation(fromHistory, () => void selectSpace(space, false, tab))) return false;
  navigationVersion++;
  openingPage.value = false;
  libraryTab.value = undefined;
  administrationOpen.value = false;
  page.value = undefined;
  documentValue.value = undefined;
  selectedSpace.value = space.id;
  spaceTab.value = tab;
  dirty.value = false;
  editorActive.value = false;
  expandSpace(space.id);
  sidebarOpen.value = false;
  error.value = "";
  const address = buildShellHash({ view: "space", spaceId: space.id, tab: spaceAddressTab(tab) });
  if (!fromHistory && (searchIsOpen.value || location.hash !== address))
    history.pushState(null, "", address);
  try {
    await ensureTree(space.id);
  } catch (failure) {
    error.value = errorMessage(failure);
  }
  return true;
}
/** Moves between the tabs of the open space. Its tree is already here: only the tab and, for O prostoru, the address change. */
function showSpaceTab(tab: SpaceDetailTab) {
  spaceTab.value = tab;
  const address = currentRoute();
  if (location.hash !== address) history.pushState(null, "", address);
}
// „Návrhy při psaní“ (plan §12 H.4): loaded only with the wiki switch on or AI possible, in idle time on a writable page, when the editor warms up and when it attaches.
const editorAssist = shallowRef<AtlasAssistFactory | null>(null);
/** Assistance is possible (the same gate as prefetchEditorAssist): only then a live editor keeps a place for a late chunk. */
const editorAssistExpected = computed(() => editorAssistEnabled(user.value) || aiCapabilities.value.editorTransform || aiCapabilities.value.editorOutline || assistantReachable.value);
function prefetchEditorAssist() {
  void publishControlLoader.prepare();
  const owner = user.value;
  // The assistant alone is enough: Zeptat se / Vysvětlit / Shrnout come from the same module while editing (P11).
  void loadEditorAssist(owner, { writingAllowed: aiCapabilities.value.editorTransform || aiCapabilities.value.editorOutline || assistantReachable.value }).then(factory => { if (user.value === owner) editorAssist.value = factory; });
}
// Editing also starts with no hover first (a key in the page, unlocking, a new page, a tap): on a page the reader can write, the
// module is fetched in idle time and the live editor that opened before it attaches it in place (liveEditorPlugins.ts), so the
// first session has its AI rows too (P11). loadEditorAssist fetches nothing while no assistance and no AI is possible.
// Publikovat swaps from its loading stand-in to the loaded control once its chunk is here: done when a writer's page opens, so the
// swap never remounts a control in the middle of a publication (it used to happen on pointing at Upravit).
watch(pageCanWrite, writable => { if (writable) void publishControlLoader.prepare(); }, { immediate: true });
watch([() => !!page.value && editable.value && !editorAssist.value, aiCapabilities], ([possible]) => {
  if (!possible) return;
  whenIdle(prefetchEditorAssist);
});
/** Editing ends by itself after this long without a key or a click in the page: its last changes go as a checkpoint. */
const EDITING_REST_MS = 10 * 60_000;
let editingRest: ReturnType<typeof setTimeout> | undefined;
/** The caret is going into the text (a new page, unlocking, Zdroj, the assistant): it lands at the first block on screen. */
function beginEditing() {
  if (!editable.value) return;
  prefetchEditorAssist();
  editorActive.value = true;
  focusEditorPending = true;
  stillEditing();
  void focusEditorWhenEditable();
}
function stillEditing() {
  clearTimeout(editingRest);
  if (editorActive.value) editingRest = setTimeout(() => void endEditing(), EDITING_REST_MS);
}
/** The caret entered the page's text (a click, Tab, a heading link): the writer is editing. */
function caretEntered(event: FocusEvent) {
  if (!editable.value || !(event.target instanceof Element) || !event.target.closest('.ProseMirror[contenteditable="true"]')) return;
  editorActive.value = true;
  stillEditing();
}
/** Esc, a click elsewhere, locking or a long rest: the caret leaves the text and its last changes are checkpointed at once. */
async function endEditing() {
  if (!editorActive.value) return;
  clearTimeout(editingRest);
  focusEditorPending = false;
  editorActive.value = false;
  const active = document.activeElement;
  if (active instanceof HTMLElement && documentBody.value?.contains(active)) active.blur();
  try { await autosave.flush(); } catch { /* the save state says so; the changes stay in the shared session */ }
}
watch(() => page.value?.id, () => { clearTimeout(editingRest); });
/** The lock (3.6-F) only flips the editor's `editable`: unlocking puts the caret into the text, locking finishes the checkpoint. */
async function toggleLock() {
  if (!pageCanWrite.value) return;
  if (pageLocked.value) { pageLocked.value = false; beginEditing(); return; }
  // Locking closes the source: its unsaved draft asks first and the page stays unlocked while the reader keeps editing it.
  if (sourceDirty.value && !((await editor.value?.resolveSourceDraft()) ?? true)) return;
  pageLocked.value = true;
  void endEditing();
}
/** DocumentEditor: a key, a paste or a drop into the read-only text. A writer's locked page pulses its lock (3.6-F). */
function editIntent() {
  if (pageLocked.value && pageCanWrite.value) lockPulse.value++;
}
/** Esc in the page ends editing, unless something in it (a menu, a picker, a dialog) took the key for itself. */
function documentKeys(event: KeyboardEvent) {
  stillEditing();
  if (event.key !== "Escape" || event.defaultPrevented || event.isComposing || !editorActive.value) return;
  // Only what is on screen: the tag field and the editor keep their listboxes in the page, hidden.
  if ([...document.querySelectorAll('[role="menu"], [role="listbox"], dialog[open], [data-slot="popover-content"]')].some(open => open.getClientRects().length)) return;
  void endEditing();
}
/** Focus leaving the page (the title, the sidebar, another window): its last changes go as a checkpoint at once. */
function documentFocusOut(event: FocusEvent) {
  if (dirty.value && !(event.relatedTarget instanceof Node && documentBody.value?.contains(event.relatedTarget))) void autosave.flush().catch(() => {});
}
/** A press elsewhere in the app (the sidebar, the dock, the comments) ends editing; the page's header, its toolbar, the top bar and overlays do not. */
function pressElsewhere(event: PointerEvent) {
  if (!editorActive.value || !(event.target instanceof Element)) return;
  const target = event.target;
  if (!target.closest(".atlas") || target.closest(".document-body, .document-header, .workspace-bar, .page-mobile-bar")) return;
  void endEditing();
}
// The editor's toolbar actions in the page menu, grouped by the action's own group (U2-8, C3-4). The page link brings no icon of
// its own; it gets the editor's page drawing.
const fallbackActionIcons: Record<string, string> = { "pages.search": "file-text" };
const withIcon = (action: EditorPluginAction) => (action.icon ? action : { ...action, icon: fallbackActionIcons[action.id] ?? null, get enabled() { return action.enabled; } });
const requirementOrder = ["requirements.insert", "requirements.catalog", "requirements.summary"];
const byRequirementOrder = (a: EditorPluginAction, b: EditorPluginAction) => requirementOrder.indexOf(a.id) - requirementOrder.indexOf(b.id);
// Hledat a nahradit, neviditelné znaky (the editor's tools, which have no group): they change how the page is worked on, not
// what it holds (the Stránka group).
const isTool = (action: EditorPluginAction) => action.id.startsWith("editor-tools.");
const toolActions = computed(() => editorActions.value.filter(isTool));
/** Module Projector's group: its blocks, and in reading too the deployment and the catalog of the page. */
const PROJECTOR_GROUP = t.nav.projectorGroup;
/** A page of requirements has a deployment board to open (or, for a writer, to compose): its menu entry shows in reading too. */
const pageHasRequirements = computed(() => projectorEnabled.value && holdsRequirements(page.value?.contentJson));
/** A reader only opens the deployment board, so the entry shows once it is made: the page's own board (its tree mark) or a 3.5.0 board under it. */
const deploymentBoardOffered = computed(() => {
  const current = page.value;
  return !!current && (canWrite.value || (pagesBySpace.value[current.spaceId] ?? []).some(item => item.id === current.id ? !!item.attachedBoardId : item.type === "board" && item.parentId === current.id));
});
async function openBoardOfDeployment() {
  const current = page.value;
  if (!current) return;
  const { openDeploymentBoard } = await import("./projector/composeDeploymentBoard");
  const document = (JSON.parse(current.contentJson) as { document?: DeploymentDocument }).document ?? {};
  await openDeploymentBoard({ pageId: current.id, spaceId: current.spaceId, document, title: current.title }, canWrite.value && boardsEnabled.value);
}
/**
 * The editor's actions by group: a group Atlas's plugin names as a label (capitalised, „Nasazení“) is a group of its own; the
 * editor's groups are lowercase identifiers (`content`, `integrations`) of what inserts something and share „Vložit“.
 */
const actionGroups = computed(() => {
  const groups = new Map<string, EditorPluginAction[]>([[t.common.insert, []]]);
  for (const action of editorActive.value ? editorActions.value : []) {
    if (isTool(action)) continue;
    const label = action.group && /^\p{Lu}/u.test(action.group) ? action.group : t.common.insert;
    if (!groups.has(label)) groups.set(label, []);
    groups.get(label)!.push(withIcon(action));
  }
  if (projectorEnabled.value && (canWrite.value || pageHasRequirements.value) && !groups.has(PROJECTOR_GROUP)) groups.set(PROJECTOR_GROUP, []);
  return [...groups].filter(([label, actions]) => actions.length || label === PROJECTOR_GROUP)
    .map(([label, actions]) => ({ label, actions: label === PROJECTOR_GROUP ? [...actions].sort(byRequirementOrder) : actions }));
});

// Module Projector on the page: the catalog flag and founding a deployment (projector/useProjectorPage.ts).
const { catalogPage, setCatalog, toggleCatalogPage, deployment, askDeployment, createDeployment, openCreated } = useProjectorPage(page, projectorEnabled,
  { openPage: id => openPage(id), refreshTree: spaceId => loadTree(spaceId), toast: toaster });
/** The last deployment dialog state: kept after it closes so the dialogs can leave with their content (SH2-15). */
const deploymentSeen = ref<typeof deployment.value>();
watch(deployment, current => { if (current) deploymentSeen.value = current; });
/** The deployment's name is prefilled: the first focus selects it, so typing replaces it (U3-4). */
function selectPrefilled(event: FocusEvent) {
  const field = event.target as HTMLInputElement;
  if (field.dataset.selected) return;
  field.dataset.selected = "true";
  field.select();
}

/** „Uložit verzi“ (saveVersion below): its dialog, the optional description and the request. */
const saveVersionOpen = ref(false), saveVersionLabel = ref(""), saveVersionBusy = ref(false), saveVersionError = ref("");
// Lazy dialogs mount on first use and then stay, so they can leave with motion (SH-2) without loading their chunk at start.
const lazyDialogs = reactive({ version: false, pdf: false, help: false });
watchEffect(() => { if (saveVersionOpen.value) lazyDialogs.version = true; if (pdfDownloadOpen.value) lazyDialogs.pdf = true; if (keyboardHelpOpen.value) lazyDialogs.help = true; });
/** The access view takes the document's place: the editor closes (its last changes went as a checkpoint on the way, confirmLeave). */
function leaveEditorForAccess() {
  editorActive.value = false;
  editorReady.value = false;
}
/**
 * `access`: open the page's „Kdo má přístup“ part instead of the document (`#/pages/<id>/access…`). `startEditing`: the caller asks
 * to write (a new page, the assistant, an older link with that intent): the editor attaches at once where the page can be written.
 */
async function openPage(id: string, startEditing = false, fromHistory = false, heading?: string, access?: PageAccessTab, block?: string) {
  const route = buildShellHash(access ? { view: "pageAccess", pageId: id, tab: access } : { view: "page", pageId: id, heading, block });
  if (id === page.value?.id) {
    if (!(await leaveProfile(fromHistory))) return;
    // Leaving the editor for the access view asks about unsaved changes, like leaving the page.
    if (access && !pageAccessTab.value) {
      if (!guardNavigation(fromHistory, () => void openPage(id, false, false, undefined, access))) return;
      leaveEditorForAccess();
    }
    pageAccessTab.value = access;
    rememberSearchView();
    if (startEditing) beginEditing();
    requestedHeading.value = heading;
    requestedBlock.value = block;
    if (!fromHistory && (searchIsOpen.value || location.hash !== route)) history.pushState(null, "", route);
    if (heading) { await nextTick(); editor.value?.navigateToHeading(heading); }
    if (block) { await nextTick(); editor.value?.revealBlock(block); }
    expandSpace(page.value.spaceId);
    revealPageInTree(id, page.value.spaceId);
    closeSearch();
    sidebarOpen.value = false;
    return;
  }
  if (!guardNavigation(fromHistory, () => void openPage(id, startEditing, false, heading, access, block))) return;
  const version = ++navigationVersion;
  openingPage.value = true;
  // The tree knows the page's title (and lists only pages the reader may open): it stands at once while the page loads (3.6-G N4).
  const listed = access ? undefined : Object.values(pagesBySpace.value).flat().find(item => item.id === id && item.type !== "board");
  // A page that arrives at once (read ahead) swaps without a frame of the placeholder, and the page on screen stays until its successor is
  // ready: the title and skeleton stand in only once the read outlasts the defer token. Opened from a page (or the tree), it is a calm swap.
  const calm = !!listed || !!page.value;
  arriving.value = undefined;
  const arrivalTimer = listed ? setTimeout(() => { if (version !== navigationVersion) return; arrivingSince = performance.now(); arriving.value = { id, title: listed.title }; }, motionMs(document.documentElement, "--atlas-motion-defer")) : undefined;
  void loadDocumentEditor().catch(() => {});
  void loadPageChrome();
  const fromSearch = searchIsOpen.value;
  if (fromSearch) rememberSearchView();
  const searchSession = searchVersion;
  if (fromSearch) {
    searchOpening.value = true;
    searchError.value = "";
  }
  error.value = "";
  try {
    const next = await fetchPage(id);
    const rich = readDocument(next.contentJson);
    if (version !== navigationVersion) return;
    takeBundle(next);
    if (
      fromSearch &&
      (!searchIsOpen.value || searchVersion !== searchSession)
    )
      return;
    arrivedShown = calm ? id : undefined;
    arrivalQuiet.value = calm;
    arrivalSkeletonShown.value = !!arriving.value && performance.now() - arrivingSince >= motionMs(document.documentElement, "--atlas-motion-defer");
    page.value = next;
    // The page is here: the arrival's placeholder goes in the same frame, not after the tree (`finally`), or it stands over the page for that wait.
    clearTimeout(arrivalTimer); arriving.value = undefined;
    // A page opened to write in (one just created) opens unlocked, whatever „Stránky otevírat“ says.
    if (startEditing) pageLocked.value = false;
    requestedHeading.value = heading;
    requestedBlock.value = block;
    libraryTab.value = undefined;
    administrationOpen.value = false;
    documentValue.value = rich;
    title.value = next.title;
    selectedSpace.value = next.spaceId;
    dirty.value = false;
    pageAccessTab.value = access;
    // An existing page with text lands a writer's caret at the start of its text: a slip there is an ordinary edit that Ctrl+Z takes
    // back, in the title it renamed the page. A page without a title or text lands in the title (DocumentTitle `landing`), a reader on the h1.
    const blocks = rich.document.content as readonly { type?: string; content?: readonly unknown[] }[] | undefined;
    const landInText = !!next.title.trim() && !!blocks?.some(node => node.type !== "paragraph" || !!node.content?.length);
    editorActive.value = !access && (startEditing || landInText) && editable.value;
    focusEditorPending = editorActive.value;
    editorReady.value = false;
    expandSpace(next.spaceId);
    sidebarOpen.value = false;
    if (!fromHistory && location.hash !== route)
      history.pushState(null, "", route);
    closeSearch();
    // The document is on screen; the tree only has to exist. A loaded one revalidates in the background.
    if (pagesBySpace.value[next.spaceId]) {
      revealPageInTree(next.id, next.spaceId);
      if (Date.now() - (treeLoadedAt.get(next.spaceId) ?? 0) > TREE_FRESH_MS) void ensureTree(next.spaceId).catch(() => {});
    } else await ensureTree(next.spaceId);
  } catch (failure) {
    const searchStillActive =
      fromSearch && searchIsOpen.value && searchVersion === searchSession;
    if (
      version === navigationVersion &&
      (!fromSearch || searchStillActive || page.value?.id === id)
    ) {
      if (searchStillActive) searchError.value = errorMessage(failure);
      else if (failure instanceof ApiError && (failure.status === 404 || failure.status === 403)) {
        page.value = undefined;
        documentValue.value = undefined;
        editorActive.value = false;
        dirty.value = false;
        pageAccessTab.value = undefined;
        libraryTab.value = undefined;
        administrationOpen.value = false;
        unavailablePage.value = id;
        closeSearch();
        if (!fromHistory && location.hash !== route) history.pushState(null, "", route);
        return;
      }
      else error.value = errorMessage(failure);
      if (fromHistory) history.replaceState(null, "", currentRoute());
    }
  } finally {
    clearTimeout(arrivalTimer);
    if (version === navigationVersion) { searchOpening.value = false; openingPage.value = false; arriving.value = undefined; }
  }
}
/** From the export overview in Úložiště: the page, then its automatic PDF export once the page change has settled. */
async function openPageFromSettings(id: string, pdfExport?: boolean) {
  await openPage(id);
  await nextTick();
  if (pdfExport && page.value?.id === id && pageCanWrite.value) pdfExportOpen.value = true;
}
function openPageAccess(id: string, tab: PageAccessTab = "access", fromHistory = false) {
  return openPage(id, false, fromHistory, undefined, tab);
}
function openLibrary(tab: LibraryView, fromHistory = false, q = "") {
  if (!guardNavigation(fromHistory, () => openLibrary(tab, false, q))) return;
  navigationVersion++;
  openingPage.value = false;
  administrationOpen.value = false;
  page.value = undefined;
  documentValue.value = undefined;
  selectedSpace.value = undefined;
  editorActive.value = false;
  dirty.value = false;
  sidebarOpen.value = false;
  libraryTab.value = tab;
  libraryQuery.value = q;
  closeSearch();
  const address = buildShellHash(q ? { view: "library", tab, q } : { view: "library", tab });
  if (!fromHistory && location.hash !== address) history.pushState(null, "", address);
}
/** Whose favourite and visit the shell keeps: the open page's, or the open board's (the same marks, interaction spec §6). */
const personalTarget = computed<{ id: string; type: TreeNodeType } | undefined>(() => page.value ? { id: page.value.id, type: "page" }
  : boardId.value && boardsEnabled.value ? { id: boardId.value, type: "board" } : undefined);
async function loadPersonalState(id: string) {
  const version = ++personalVersion;
  const type = personalTarget.value?.type;
  personalAbort?.abort();
  personalAbort = new AbortController();
  personalState.value = undefined;
  personalError.value = "";
  favoritePending.value = false;
  try {
    const state = await personalLibraryApi.state(id, personalAbort.signal, type);
    if (version === personalVersion && personalTarget.value?.id === id) personalState.value = state;
  } catch (failure) {
    if (version === personalVersion && personalTarget.value?.id === id) personalError.value = errorMessage(failure);
  }
}
async function toggleFavorite() {
  const target = personalTarget.value;
  if (!target || !personalState.value || favoritePending.value) return;
  const id = target.id, version = personalVersion;
  const desired = !personalState.value.isFavorite;
  favoritePending.value = true; personalError.value = "";
  try {
    const state = await personalLibraryApi.favorite(id, desired, target.type);
    if (version === personalVersion && personalTarget.value?.id === id) {
      personalState.value = state;
      personalRefresh.value++;
      if (state.isFavorite) void nextTick(popFavoriteStar);
    }
  } catch (failure) {
    if (version === personalVersion && personalTarget.value?.id === id) personalError.value = errorMessage(failure);
  } finally { if (version === personalVersion) favoritePending.value = false; }
}
/** The star the reader just chose (top bar, phone bar) settles with a small pop; a star that arrives with the page does not. */
function popFavoriteStar() {
  for (const star of document.querySelectorAll<SVGElement>(".favorite-button svg, .page-mobile-action[aria-pressed=true] svg"))
    animateAtlas(star, [{ transform: "scale(.8)" }, { transform: "scale(1.12)", offset: 0.6 }, { transform: "none" }], { duration: motionMs(star, "--atlas-motion-pop") });
}
watch(() => personalTarget.value?.id, (id) => {
  personalVersion++; personalAbort?.abort(); personalState.value = undefined;
  personalError.value = ""; favoritePending.value = false;
  if (!id || !user.value) return;
  const accountId = user.value.id, type = personalTarget.value?.type;
  // The page's bundle carries its state (with the visit already recorded when the opening read it): nothing more to ask.
  const bundled = type === "page" && page.value?.id === id ? page.value.bundle?.personalState : undefined;
  if (bundled) personalState.value = bundled; else void loadPersonalState(id);
  noteVisit(accountId, id);
  if (type === "page" && visitedByRead === id) { visitedByRead = undefined; personalRefresh.value++; return; }
  // Visits never block reading. A late response cannot replace the current page's state.
  void personalLibraryApi.visit(id, type).then(() => {
    if (user.value?.id === accountId && personalTarget.value?.id === id) personalRefresh.value++;
  }).catch(() => { /* A failed visit does not affect document access or the favorite state. */ });
});
async function exportContent(kind: "pages" | "spaces", format: "zip" | "pdf" = "zip") {
  return run(async () => {
    const id = kind === "pages" ? page.value?.id : currentSpace.value?.id;
    if (!id) return;
    const activePageId = page.value?.id;
    // The export takes the saved revision: the last changes go as a checkpoint first.
    if (dirty.value) {
      await autosave.flush();
      if (page.value?.id !== activePageId) throw new Error(t.toast.exportPageChanged);
      if (dirty.value) throw new Error(t.toast.exportMoreChanges);
    }
    const name = (kind === "pages" ? page.value?.title : currentSpace.value?.name) ?? "";
    // The download starts when the server has rendered it; until then the click is answered by a toast (replaced by the success one).
    const toastId = "content-export";
    toaster.progress(format === "pdf" ? t.toast.pdfPreparing : t.toast.zipPreparing, toastId);
    try { await downloadContentExport(kind, id, format, { name, coverDetails: format === "pdf" ? pdfCoverDetails.value : undefined }); }
    catch (failure) { toaster.dismiss(toastId); throw failure; }
    toaster.success(format === "pdf" ? t.toast.pdfReady : t.toast.zipReady, { id: toastId });
  });
}
/** One answer for the space menu wherever it opens (SpaceMenuItems: the breadcrumb's and the sidebar's). The trash and the export are of the open space, so another one opens first. */
async function spaceMenu(space: Space, action: "settings" | "templates" | "access" | "trash" | "export" | "content-health") {
  if (action === "settings") return openSettings(spaceSettingsHash(space.id));
  if (action === "content-health") return openSettings(spaceSettingsHash(space.id, "content-health"));
  if (action === "templates") return openSpaceTemplates(space.id);
  if (action === "access") return openSettings(spaceSettingsHash(space.id, "access"));
  if (currentSpace.value?.id !== space.id && !(await selectSpace(space))) return;
  if (action === "trash") organizationOpen.value = "trash"; else void exportContent("spaces");
}
function openWhatsNew(fromHistory = false) {
  if (!guardNavigation(fromHistory, () => openWhatsNew())) return;
  navigationVersion++;
  openingPage.value = false;
  libraryTab.value = undefined;
  administrationOpen.value = false;
  page.value = undefined;
  documentValue.value = undefined;
  dirty.value = false;
  editorActive.value = false;
  selectedSpace.value = undefined;
  sidebarOpen.value = false;
  whatsNewOpen.value = true;
  // The first opening after an update welcomes with the splash (unseen releases), whichever way it opened; it settles the notice.
  whatsNewSince.value = releaseSince;
  releaseSince = undefined;
  closeReleaseNotice();
  closeSearch();
  const address = buildShellHash({ view: "whatsNew" });
  if (!fromHistory && location.hash !== address) history.pushState(null, "", address);
}
/** After sign-in: a release this account has not seen is announced once (whatsNew/notice.ts); a new account takes it silently. */
function announceRelease(account: User) {
  const release = releaseOf(status.value?.version);
  const notice = whatsNewNotice(status.value?.version, account.releaseNotesSeen);
  if (!release || notice === "none") return;
  // The account that saw it, not one signed in since (a sign-out dismisses the toast too). A lost write only shows the notice again.
  const seen = () => { if (user.value?.id === account.id) markReleaseNotesSeen(release).catch(() => {}); };
  if (notice === "adopt") return seen();
  releaseSince = account.releaseNotesSeen ?? undefined;
  // The named version's notes (codename, mark; a patch has none of its own) load with the notice only, as the page's own chunk.
  const show = (note?: ReleaseNote) => {
    if (user.value?.id === account.id) closeReleaseNotice = showWhatsNew(release, { open: () => openWhatsNew(), seen }, note, releaseSince);
  };
  import("./whatsNew/releaseNotes").then(({ releaseNotes }) => show(releaseNotes.find(note => note.version === `${releaseLine(release)}.0`)), () => show());
}
/** `spaceId`: where the board lives, when the caller knows (the tree, Knihovna, the search): the frame then shows its place at once. */
function openBoard(id: string, fromHistory = false, spaceId?: string) {
  if (!boardsEnabled.value) { if (fromHistory) home(true); return; }
  const address = buildShellHash({ view: "board", boardId: id });
  if (id === boardId.value) {
    closeSearch(); sidebarOpen.value = false;
    if (!fromHistory && location.hash !== address) history.pushState(null, "", address);
    return;
  }
  if (!guardNavigation(fromHistory, () => openBoard(id, false, spaceId))) return;
  navigationVersion++;
  openingPage.value = false;
  libraryTab.value = undefined;
  administrationOpen.value = false;
  page.value = undefined;
  documentValue.value = undefined;
  dirty.value = false;
  editorActive.value = false;
  // Without a known space (a reference node, a relation, a link on a board) the current one stays until the board's own space
  // comes with its load (boardMeta), so the sidebar does not lose its space on the way.
  const known = spaces.value.some(space => space.id === spaceId);
  if (known || spaceId) selectedSpace.value = known ? spaceId : undefined;
  sidebarOpen.value = false;
  boardId.value = id;
  closeSearch();
  if (!fromHistory && location.hash !== address) history.pushState(null, "", address);
  if (known) placeBoard(id, spaceId!);
}
/** The board's place in the sidebar: its space unfolds and its row is revealed and current, as a page's. */
function placeBoard(id: string, spaceId: string) {
  expandSpace(spaceId);
  const reveal = () => { if (boardId.value === id) revealPageInTree(id, spaceId); };
  // A loaded tree revalidates in the background as a page's does, once it is older than TREE_FRESH_MS: „Tabule nasazení“ read it
  // twice in a row (the page's board is looked up in it, then opened).
  if (pagesBySpace.value[spaceId]) { reveal(); if (Date.now() - (treeLoadedAt.get(spaceId) ?? 0) > TREE_FRESH_MS) void ensureTree(spaceId).catch(() => {}); }
  else void ensureTree(spaceId).then(reveal, () => {});
}
/** The board view has loaded the board (or a colleague renamed it): the frame and the tree follow. */
function boardMeta(meta: { spaceId: string; title: string; canEdit: boolean }) {
  const id = boardId.value;
  if (!id) return;
  const first = !boardFrame.value;
  boardFrame.value = meta;
  if ((first || selectedSpace.value !== meta.spaceId) && spaces.value.some(space => space.id === meta.spaceId)) {
    selectedSpace.value = meta.spaceId;
    placeBoard(id, meta.spaceId);
  }
  const node = pagesBySpace.value[meta.spaceId]?.find(item => item.id === id);
  if (node && node.title !== meta.title) node.title = meta.title;
}
async function trashBoard() {
  const id = boardId.value, space = currentSpace.value;
  if (!id || !space || boardTrashBusy.value) return;
  boardTrashBusy.value = true; boardTrashError.value = "";
  try {
    await boardApi.remove(id);
    if (boardId.value !== id) return;
    boardTrashOpen.value = false;
    libraryRefresh.value++;
    await selectSpace(space);
    try { await loadTree(space.id); } catch { /* The space overview reports a tree it cannot load. */ }
    toaster.success(t.toast.boardInTrash);
  } catch (failure) {
    boardTrashError.value = errorMessage(failure);
  } finally { boardTrashBusy.value = false; }
}
function openSpaceCreation(fromHistory = false) {
  // By its address (#/new-space) without the right: Knihovna, and why, as a settings address does (AD5-2).
  if (!canCreateSpace.value) { if (fromHistory) { history.replaceState(null, "", buildShellHash({ view: "home" })); home(true); toaster.error(t.toast.newSpaceAdminOnly); } return; }
  if (!guardNavigation(fromHistory, () => openSpaceCreation())) return;
  navigationVersion++;
  openingPage.value = false;
  libraryTab.value = undefined;
  administrationOpen.value = false;
  page.value = undefined;
  documentValue.value = undefined;
  dirty.value = false;
  editorActive.value = false;
  selectedSpace.value = undefined;
  sidebarOpen.value = false;
  error.value = "";
  showSpaceForm.value = true;
  const address = buildShellHash({ view: "spaceCreation" });
  if (!fromHistory && location.hash !== address) history.pushState(null, "", address);
}
/**
 * A cancelled Nový prostor returns to Knihovna, with focus back on the sidebar's Nový prostor. A named
 * draft asks first (UnsavedChangesDialog); focus returns once Zahodit has really closed the flow.
 */
function cancelSpaceCreation() {
  const focusNewSpace = () => sidebar.value?.querySelector<HTMLElement>(`.sidebar-spaces-heading [aria-label="${t.common.newSpace}"]`)?.focus();
  const leave = (afterQuestion = false) => {
    home();
    if (showSpaceForm.value) return;
    // After Zahodit the question is still closing, and its modal <dialog> keeps the page inert until it has; Zahodit was the
    // reader's last step, so focus goes to Nový prostor then.
    if (afterQuestion) afterLeaveQuestion = focusNewSpace;
    else void nextTick(() => { if (!document.activeElement || document.activeElement === document.body) focusNewSpace(); });
  };
  if (confirmLeave(false, () => leave(true))) leave();
}
function spaceCreated(response: Omit<Space, "pinned" | "pageCount" | "lastChangedAt">, warning?: string) {
  // The create response carries no listing facts; a new space is empty and pinned for its creator (as on the server).
  const created: Space = { ...response, pinned: true, pageCount: 0, lastChangedAt: null };
  spaces.value.push(created);
  showSpaceForm.value = false;
  templateDirty.value = false;
  return run(async () => {
    await selectSpace(created);
    if (warning) toaster.warning(warning); else toaster.success(t.toast.spaceCreated);
  });
}
function resetTemplateWorkspace() {
  templateView.value = undefined;
  showSpaceForm.value = false;
  unavailablePage.value = undefined;
  unavailableSpace.value = undefined;
  templateDirty.value = false;
  templateBusy.value = false;
}
function showCreatePage() {
  if (!currentSpace.value || !canWrite.value) return;
  void openTemplateWorkspace(currentSpace.value, "new-page", false, creationParent(currentSpace.value));
}
/**
 * „Nové nasazení“ (module Projector, J2): under the open page (the customer's, at the top of the space), beside it when it is one of a
 * customer's deployments (nested, with requirements) or the catalog (creationParent).
 */
function showCreateDeployment() {
  const space = currentSpace.value;
  if (!space || !canWrite.value) return;
  const beside = page.value?.spaceId === space.id && !!page.value.parentId && holdsRequirements(page.value.contentJson);
  void openTemplateWorkspace(space, "new-deployment", false, beside ? page.value!.parentId ?? "" : creationParent(space));
}
function showCreateBoard() {
  if (currentSpace.value && canWrite.value) createBoardInSpace(currentSpace.value);
}
async function openTemplateWorkspace(space: Space, view: PageCreationKind, fromHistory = false, parentId = "", title = "") {
  const allowed = (view !== "new-board" || boardsEnabled.value) && (view !== "new-deployment" || projectorEnabled.value)
    && (user.value?.isWikiAdmin || (view === "new-template" ? space.role === "Admin" : space.role !== "Reader"));
  if (!allowed) { if (fromHistory) await selectSpace(space, true); return; }
  if (!guardNavigation(fromHistory, () => void openTemplateWorkspace(space, view, false, parentId, title))) return;
  navigationVersion++;
  openingPage.value = false;
  libraryTab.value = undefined;
  administrationOpen.value = false;
  page.value = undefined;
  documentValue.value = undefined;
  dirty.value = false;
  detailDirty.value = false;
  sourceDirty.value = false;
  editorActive.value = false;
  selectedSpace.value = space.id;
  sidebarOpen.value = false;
  error.value = "";
  initialPageParent.value = parentId;
  initialPageTitle.value = title;
  templateView.value = view;
  expandSpace(space.id);
  if (!fromHistory) history.pushState(null, "", buildShellHash({ view: "pageCreation", spaceId: space.id, kind: view }));
  const version = navigationVersion;
  try { await ensureTree(space.id); }
  catch (failure) { if (version === navigationVersion) error.value = errorMessage(failure); }
}
function cancelTemplateWorkspace() {
  if (!currentSpace.value) return;
  // A new template is started from Šablony stránek in Nastavení prostoru; cancelling returns there.
  if (templateView.value === "new-template") openSpaceTemplates(currentSpace.value.id);
  else void selectSpace(currentSpace.value);
}
async function pageCreatedFromTemplate(created: Page) {
  templateBusy.value = false;
  templateDirty.value = false;
  await openPage(created.id, true);
}
function boardCreated(id: string) {
  const space = currentSpace.value;
  templateBusy.value = false;
  templateDirty.value = false;
  if (space) void loadTree(space.id).catch(() => {});
  openBoard(id, false, space?.id);
}
async function togglePageTemplate() {
  if (!page.value || !currentSpace.value || !editor.value || !canManageTemplates.value || pending.value) return;
  await run(async () => {
    const sourceId = page.value!.id;
    if (dirty.value) await autosave.flush();
    if (page.value?.id !== sourceId || dirty.value) throw new Error(t.toast.moreChangesTryAgain);
    const updated = await put<Page>(`/pages/${sourceId}/template`, { isTemplate: !page.value.isTemplate });
    if (page.value?.id !== sourceId) return;
    page.value = updated;
    await loadTree(updated.spaceId);
    toaster.success(updated.isTemplate ? t.toast.pageIsTemplate : t.toast.pageNotTemplate);
  });
}
/** The tree as it stood before the page menu's last change: what Zpět of a move puts back. */
let treeBeforeOrganization: readonly TreePage[] | undefined;
async function beforeOrganizationMutation() {
  if (pending.value) return false;
  const pageChange = organizationOpen.value === "page" || organizationOpen.value === "pageTrash";
  if (pageChange && dirty.value) await autosave.flush();
  treeBeforeOrganization = currentSpace.value && pagesBySpace.value[currentSpace.value.id];
  return true;
}
/** „Přesunout“ in the page menu says what the tree's own move says, with the same Zpět (P6). */
function announceMove(spaceId: string, id: string | undefined, titleOrderCancelled?: boolean) {
  const node = id ? pagesBySpace.value[spaceId]?.find(item => item.id === id) : undefined, before = treeBeforeOrganization;
  if (!node || !before?.some(item => item.id === node.id)) return;
  if (titleOrderCancelled) { announceTitleOrderCancelled(spaceId, node.parentId); return; }
  const previous = treePlaceOf(before, node.id);
  toaster.success(node.type === "board" ? t.toast.boardMoved : t.toast.pageMoved, { id: "tree-move",
    undo: () => void runUndo(() => commitTreeMove(spaceId, { id: node.id, version: treeVersions.value[spaceId] ?? node.version, type: node.type }, previous, false)) });
}
async function organizationChanged(change: OrganizationChanged) {
  if (change.kind === "move-space" && change.sourceSpaceId && change.targetSpaceId) {
    const owner = user.value?.id;
    try {
      await Promise.all([loadTree(change.sourceSpaceId), loadTree(change.targetSpaceId)]);
      if (user.value?.id !== owner) return;
      expandSpace(change.targetSpaceId);
      libraryRefresh.value++;
      if (page.value && change.affectedIds.includes(page.value.id)) {
        const id = page.value.id, resumeEditing = editorActive.value;
        // Transfer resets the live generation. Recreate its adapter after commit;
        // normal same-page navigation deliberately preserves that adapter.
        dirty.value = false;
        page.value = undefined; documentValue.value = undefined;
        editorActive.value = false;
        await nextTick();
        await openPage(id, resumeEditing);
      }
      toaster.success(t.toast.pageMovedAccess);
    } catch (failure) { if (user.value?.id === owner) error.value = errorMessage(failure); }
    return;
  }
  if (!currentSpace.value) return;
  const space = currentSpace.value;
  await loadTree(space.id);
  if (change.kind === "move") announceMove(space.id, organizationOpen.value === "board" ? boardId.value : page.value?.id, change.titleOrderCancelled);
  if (page.value && change.affectedIds.includes(page.value.id)) {
    if (change.kind === "trash") {
      dirty.value = false;
      await selectSpace(space);
      toaster.success(t.toast.pageInTrash);
    } else if (change.kind === "untrash" && pageGone.value === page.value.id) {
      await reopenPage(page.value.id);
    } else {
      const item = pagesBySpace.value[space.id]?.find(item => item.id === page.value!.id);
      if (item) page.value = { ...page.value, parentId: item.parentId };
    }
  }
}
/** „Uložit“ of the page menu's name field: the same toast with Zpět as a rename in the tree (P6). */
async function organizationRenamed(result: Page) {
  const node = pagesBySpace.value[result.spaceId]?.find(item => item.id === result.id), before = node?.title;
  await pageRenamed(result);
  toaster.success(t.toast.titleChangedInDraft, { undo: before === undefined ? undefined : () => {
    const current = pagesBySpace.value[result.spaceId]?.find(item => item.id === result.id);
    if (current) void renameTreePage(result.spaceId, current, before, true);
  } });
}
async function pageRenamed(result: Page) {
  if (page.value?.id !== result.id) return;
  page.value = { ...page.value, title: result.title, version: result.version, publishedVersion: result.publishedVersion };
  title.value = result.title;
  await loadTree(result.spaceId);
}
// Plugin actions the live editor exposes while it takes input (attachment, YouTrack task, find &
// replace, invisible characters); shown in the page menu instead of an editor toolbar row.
const editorActions = ref<EditorPluginAction[]>([]);
// "Aktualizováno …" under the title: the current revision's activeFrom, which the page answer already
// carries. Writers only, as before: readers cannot open the history it leads to.
const lastChangedAt = computed(() => (page.value?.canEdit ? page.value.activeFrom ?? null : null));
/** Domů → Co se změnilo → Historie: the page, then its history at that change (the version the dashboard row names). */
async function openChangeInHistory(change: RecentChange) {
  await openPage(change.pageId);
  if (page.value?.id !== change.pageId) return;
  openPageHistory({ id: change.revisionId, version: change.version, title: change.title, authorId: change.authorId, authorName: change.authorName,
    activeFrom: change.at, activeTo: null, isCurrent: false, isPublished: false, lastPublishedAt: null, channel: change.channel, apiToken: change.apiToken, isAi: change.isAi });
}
function openPageHistory(revision?: PageRevisionSummary | Event) {
  if (!page.value || !pageCanWrite.value || pending.value) return;
  // The history lists the last changes too: they go as a checkpoint now.
  if (dirty.value) void autosave.flush().catch(() => {});
  historyInitialRevision.value = revision && "version" in revision ? revision : undefined;
  historyOpen.value = true;
}
async function beforeHistoryRestore() {
  // Component input in progress asks through its component, as when leaving the page.
  const allowed = !!page.value && pageCanWrite.value && !pending.value && (!documentDraft() || await resolveComponentDrafts());
  if (!allowed) return false;
  // The restored revision replaces the document; a confirmed discard must not leave a "lost block" draft behind.
  if (detailDirty.value) editor.value?.discardDetailDraft();
  if (sourceDirty.value) editor.value?.discardSourceDraft();
  // The restore starts the live document anew and the server drops the restorer's own edits no checkpoint took yet: they go into the
  // history first, so nothing typed is lost (a checkpoint already on its way is waited for).
  try { await autosave.flush(); }
  catch (failure) { throw new Error(t.toast.restoreSaveFailed(errorMessage(failure))); }
  return true;
}
/** Zpět of a restore: the version from before it (the restored version's predecessor, a version of its own) is restored in turn. */
async function undoRestore(restored: Page) {
  if (page.value?.id !== restored.id || !await beforeHistoryRestore().catch(failure => { toaster.error(errorMessage(failure)); return false; })) return;
  await runUndo(async () => {
    const history = await historyApi.list(restored.id, null, new AbortController().signal);
    const before = history.items.find(item => item.version === restored.version)?.previousRevisionId;
    if (!before) throw new Error(t.toast.versionBeforeRestoreMissing);
    await applyRestoredPage(await historyApi.restore(restored.id, before, history.currentVersion), true);
  });
}
// Page dock (refresh 2.5, board 03): the right column beside the article with Obsah · Asistent · Historie · Přístup.
// It is the one workspace panel the assistant uses too; the tabs choose what fills it. A wide screen brings it back
// with each page as the reader last left it (open by default), remembered per account like the reading width.
// Automatic opening reserves a comfortable article next to the dock, even with a widened rail.
const pageDockHasRoom = computed(() => viewportWidth.value >= 1280 && workspaceWidth.value >= 1000);
const pageDockTab = ref<PageSideTab>("outline");
const documentBody = ref<HTMLElement | null>(null);
const pageDockShown = computed(() => workspacePanel.content.value === PageSidePanel);
const pageDockTabs = computed(() => documentOpen.value && page.value ? [
  { id: "outline", label: t.common.contents },
  { id: "search", label: t.common.search },
  ...(assistantReachable.value ? [{ id: "assistant", label: t.common.assistant, icon: "ai" }] : []),
  ...(pageCanWrite.value ? [{ id: "history", label: t.common.history }] : []),
  { id: "comments", label: t.common.discussion },
  { id: "access", label: t.common.access },
] : undefined);
const pageDockActive = computed(() => pageDockShown.value ? pageDockTab.value : assistant.open.value ? "assistant" : undefined);
const pageDockOpen = computed(() => workspacePanel.open.value && !!pageDockTabs.value && (pageDockShown.value || assistant.open.value));
// The discussion is open while the dock shows its Diskuse: then the peek under the avatars hands over to it (never both).
const discussionOpen = computed(() => pageDockOpen.value && pageDockShown.value && pageDockTab.value === "comments");
// The discussion button's count and the peek's bubbles are PageDiscussionButton's (lazy, read after the page's text); the open thread tells it its count.
// On a phone the section under the content is the open thread while it is on screen (the button is hidden there; its count, unread ones
// accented, shows in the bottom bar and the ⋯ menu).
const discussionButton = ref<InstanceType<typeof PageDiscussionButton>>();
const phoneDiscussion = ref<ComponentPublicInstance>();
const phoneDiscussionShown = useElementVisibility(phoneDiscussion);
const discussionThreadOpen = computed(() => discussionOpen.value || (phoneLayout.value && phoneDiscussionShown.value));
/** The dock: Alt C over an open dock remembers the text it came from, as opening the dock does, so Escape returns there. */
const workspaceSidePanel = ref<InstanceType<typeof WorkspaceSidePanel>>();
/** The comment fields' editor services (`[[`, `@`) of the open page, one host for the button, the section and the dock. */
const pageCommentHost = computed(() => (page.value && commentHost.value ? commentHost.value(page.value.id, page.value.spaceId) : undefined));
function pageDockRemembered() {
  return !user.value || readStored(`atlas:reading:${user.value.id}:dock`) !== "closed";
}
function rememberPageDock(open: boolean) {
  if (!user.value) return;
  writeStored(`atlas:reading:${user.value.id}:dock`, open ? "open" : "closed");
}
function showPageDock(tab: PageSideTab, focus = true) {
  pageDockTab.value = tab;
  workspacePanel.show({ label: t.common.sidePanel, content: PageSidePanel, focus });
}
/**
 * The page's Diskuse (the button beside the avatars, a bubble of the peek, Alt C): the dock's tab with the same thread a board has; the text
 * selected in the page becomes the quote of a new comment, and the caret goes into the field (Escape closes the dock and returns to the text).
 * The peek under the avatars hands over to it: its bubbles fly out while the dock slides in. The button and Alt C toggle it.
 */
function selectedInPage() {
  const selection = window.getSelection();
  return selection && !selection.isCollapsed && documentBody.value?.contains(selection.anchorNode) ? selection.toString() : "";
}
async function openDiscussion() {
  if (!documentOpen.value || !pageDockTabs.value) return false;
  // A phone has no second thread in the dock: its Diskuse is the section under the content.
  if (phoneLayout.value) { workspacePanel.hide(); revealPageComments(); return true; }
  const selected = selectedInPage();
  const picked = selected ? quoteMarkdown(selected) : "";
  // The thread's editor services (people, pages) come first, so the first Alt C already has `@`.
  await pageChrome.comments().catch(() => undefined);
  if (picked) requestedQuote.value = picked;
  workspaceSidePanel.value?.rememberOpener();
  showPageDock("comments");
  rememberPageDock(true);
  if (!picked) { await nextTick(); const side = document.querySelector("[data-page-side=comments]"); if (side) void focusWhenPresent(side, '.comment-composer [contenteditable="true"]', 90); }
  return true;
}
/** Diskuse on a phone (Alt C, the bottom bar, the ⋯ menu): the section under the content, the keyboard on its field or „Napsat komentář“. */
function revealPageComments() {
  const section = workspace.value?.querySelector<HTMLElement>(".page-comments");
  if (!section) return;
  section.scrollIntoView({ block: "start" });
  void focusWhenPresent(section, '[data-comment-start], .comment-composer [contenteditable="true"]', 90);
}
function toggleDiscussion() {
  // A board's Diskuse the same way (its button in the top bar, Alt C, the ⋯ menu): BoardView's dock; the view is there only while a board is open.
  if (boardView.value) return boardView.value.toggleDiscussion();
  if (phoneLayout.value) return revealPageComments();
  // Text selected in the page is quoted into the open Diskuse instead of closing it.
  if (!discussionOpen.value || selectedInPage()) return void openDiscussion();
  workspacePanel.hide();
  rememberPageDock(false);
}
function selectPageDockTab(id: string) {
  if (id === "comments") { void openDiscussion(); return; }
  if (id === "assistant") launchAssistant(true);
  else showPageDock(id as PageSideTab);
}
function togglePageDock() {
  if (boardView.value) return boardView.value.toggleDock();
  if (pageDockOpen.value) { workspacePanel.hide(); rememberPageDock(false); }
  else { showPageDock(pageDockTab.value); rememberPageDock(true); }
}
function pageDockClosed() { if (pageDockTabs.value) rememberPageDock(false); }
/** „Číst“ of the phone bar: back to the top of the article, its title focused. */
function readFromTop() {
  const heading = workspace.value?.querySelector<HTMLElement>(".document-header h1, .document-title-input");
  heading?.scrollIntoView({ block: "start" });
  if (heading && !heading.hasAttribute("tabindex")) heading.setAttribute("tabindex", "-1");
  heading?.focus({ preventScroll: true });
}
// The dock opens by itself only on a wide screen, so it also steps aside when the window narrows below it: there it
// squeezes the article, and below 760 px (no dock toggle) it would cover the whole page including the top bar. The
// reader's remembered choice stays as it was, and widening the window brings the dock back.
watch(pageDockHasRoom, hasRoom => {
  if (!hasRoom) { if (pageDockShown.value) workspacePanel.hide(); }
  else if (!workspacePanel.open.value && pageDockTabs.value && pageDockRemembered()) showPageDock(pageDockTab.value, false);
});
watch(() => (documentOpen.value ? page.value?.id : undefined), (id) => {
  const quiet = !!id && id === arrivedShown;
  arrivedShown = undefined;
  if (!id) { if (pageDockShown.value) workspacePanel.hide(); return; }
  if (!quiet) void nextTick(() => articleArrives(workspace.value?.querySelectorAll(".workspace-content > .document-header, .workspace-content > .document-body") ?? []));
  if (!workspacePanel.open.value && pageDockHasRoom.value && pageDockRemembered()) showPageDock(pageDockTab.value, false);
});
// Discard changes: the working version returns to the published one. The confirmation (what goes, the restore of the published revision)
// is DiscardChangesDialog, loaded when first opened; the shell keeps the flag.
const DiscardChangesDialog = lazyComponent(() => import("./components/publishing/DiscardChangesDialog.vue"), { loading: dialogSkeleton(t.dlg.discardTitle, "wide") });
const canDiscard = computed(() => unpublishedChanges.value && pageCanWrite.value && !!publication.value?.publishedRevisionId);
const discardOpen = ref(false);
const discardSeen = ref(false);
function openDiscard() { if (canDiscard.value) discardOpen.value = discardSeen.value = true; }
/** The publish panel opened: the published side of the comparison is on its way before Zahodit změny… is pressed. */
function discardSoon() { if (canDiscard.value && page.value) historyApi.prefetch(page.value.id, publication.value!.publishedRevisionId!); }
async function saveBeforeDiscard() { if (dirty.value) await autosave.flush(); }
function discarded(restored: Page, message: string) { discardOpen.value = false; void applyRestoredPage(restored, false, message); }
async function applyRestoredPage(restored: Page, undone = false, message = t.toast.restored) {
  if (page.value?.id !== restored.id) return;
  try {
    const restoredDocument = readDocument(restored.contentJson);
    page.value = restored;
    title.value = restored.title;
    documentValue.value = restoredDocument;
    dirty.value = false;
    editorActive.value = false;
    editorReady.value = false;
    collaborationSaveReady.value = false;
    editorMountVersion.value++;
    historyOpen.value = false;
    if (undone) toaster.success(t.toast.restoreUndone);
    else toaster.success(message, { undo: () => void undoRestore(restored) });
    await loadTree(restored.spaceId);
  } catch (failure) { error.value = errorMessage(failure); }
}
watch(() => page.value?.id, () => { organizationOpen.value = undefined; historyOpen.value = false; discardOpen.value = false; pdfExportOpen.value = false; pdfDownloadOpen.value = false; readershipOpen.value = false; collaborationSaveReady.value = false; collaborationStatus.value = null; collaborators.value = []; });
/**
 * Opens the „Zdroj“ tab of the live editor (admins). Its base is the live document, so the author's own pending edits go as a
 * checkpoint first; an open OpenAPI YAML draft asks about itself first (one detail at a time).
 */
async function openPageSource() {
  if (!canEditSource.value || pending.value || !page.value || !editor.value) return;
  // The switch stands while the editor is not attached yet: Zdroj attaches it first.
  if (!editorActive.value) { beginEditing(); await nextTick(); }
  if (!editorActive.value || !editor.value) return;
  if (detailDirty.value && !(await editor.value.resolveDetailDraft())) return;
  if (dirty.value) {
    const pageId = page.value.id;
    await run(() => autosave.flush());
    if (page.value?.id !== pageId || !editorActive.value || error.value || dirty.value) return;
  }
  if (!editor.value?.openSource()) toaster.error(t.toast.sourceCannotOpen);
}
/**
 * Host save of the source draft: the author's own live edits made since the source opened are saved first
 * (they are part of the draft's base), then the page is replaced on the server like a history restore.
 */
async function savePageSource(value: MarkdownRichValue, markdown: string, signal: AbortSignal): Promise<PageSourceSaveResult> {
  const current = page.value;
  if (!current || !canEditSource.value) return { ok: false, reason: "error", message: t.toast.sourcePermission };
  if (pending.value) return { ok: false, reason: "error", message: t.search.waitForAction };
  const id = current.id;
  pending.value = true;
  try {
    if (dirty.value) await checkpointDocument(true);
    if (page.value?.id !== id || signal.aborted) return { ok: false, reason: "error" };
    const updated = await pageSourceApi.replace(id, { contentJson: JSON.stringify(value), markdown, expectedVersion: page.value.version }, signal);
    if (page.value?.id === id) await applySavedSource(updated);
    return { ok: true };
  } catch (failure) {
    const result = pageSourceFailure(failure);
    if (result.ok || result.reason !== "conflict" || page.value?.id !== id) return result;
    // The live document already follows the version that won; the next Apply needs its version.
    try {
      const fresh = await api<Page>(`/pages/${encodeURIComponent(id)}`);
      if (page.value?.id === id) page.value = fresh;
    } catch { /* The next Apply reports the conflict again. */ }
    return result;
  } finally { pending.value = false; }
}
/** Same reload as after a history restore: the saved revision and a fresh collaboration generation. */
async function applySavedSource(saved: Page) {
  page.value = saved;
  title.value = saved.title;
  documentValue.value = readDocument(saved.contentJson);
  dirty.value = false;
  editorReady.value = false;
  collaborationSaveReady.value = false;
  editorMountVersion.value++;
  libraryRefresh.value++;
  toaster.success(t.toast.sourceSaved);
  try { await loadTree(saved.spaceId); } catch (failure) { toaster.error(errorMessage(failure)); }
}
/** Imported Markdown named the page while the title was empty. */
function importTitle(value: string) {
  if (!editorActive.value || title.value.trim()) return;
  title.value = titleDraft.value = value;
  documentChanged();
}
watch(dirty, value => { if (value) message.value = ''; });
/**
 * The title in its field (3.6-B): saved on confirmation (Enter or leaving the field), never while typed; an empty one is refused at
 * the field. With the live editor attached it goes with the checkpoint, otherwise as a rename, like the tree's.
 */
const titleDraft = ref("");
const titleError = ref("");
watch(title, saved => { titleDraft.value = saved; titleError.value = ""; }, { immediate: true });
const titleDirty = () => !!page.value && titleDraft.value.trim() !== title.value.trim();
async function commitTitle() {
  const current = page.value, next = titleDraft.value.trim();
  if (!current || !editable.value || next === title.value.trim()) { titleError.value = ""; return; }
  if (!next) { titleError.value = t.toast.titleEmpty; titleDraft.value = title.value; return; }
  titleError.value = "";
  title.value = next;
  if (collaborationStatus.value?.enabled) { documentChanged(); void autosave.flush().catch(() => {}); return; }
  try { await pageRenamed(await organizationApi.rename(current.id, next, current.version)); }
  catch (failure) { if (page.value?.id === current.id) { title.value = titleDraft.value = current.title; titleError.value = errorMessage(failure); } }
}
/** A change of the live document (local or a colleague's): it waits for the next checkpoint. */
function documentChanged() {
  documentEditEpoch.value++;
  dirty.value = true;
  autosave.changed();
}
/** What a checkpoint sends, taken at once: leaving captures it before the editor goes. */
interface Checkpoint { pageId: string; version: number; title: string; edits: number; snapshot: ReturnType<NonNullable<typeof editor.value>["capture"]>; aiEpoch: number; aiAssisted: boolean }
function captureCheckpoint(): Checkpoint | undefined {
  if (!page.value || !editor.value) return undefined;
  return { pageId: page.value.id, version: page.value.version, title: title.value, edits: documentEditEpoch.value,
    snapshot: editor.value.capture({ checkpoint: true }), aiEpoch: aiAppliedEpoch, aiAssisted: pendingAiAssisted.value };
}
/** `timed`: a timed checkpoint asks once; the scheduler's backoff and the server's own checkpoint (3.6) cover a pending answer. */
async function sendCheckpoint(checkpoint: Checkpoint, timed = false, rebased = false): Promise<void> {
  const { pageId, snapshot } = checkpoint;
  // The page may be gone from the screen (leaving): the version an earlier checkpoint of it left is the one to build on.
  const known = savedSnapshot?.pageId === pageId ? savedSnapshot.version : 0;
  const expectedVersion = page.value?.id === pageId ? page.value.version : Math.max(checkpoint.version, known);
  const renamed = page.value?.id === pageId && page.value.title !== checkpoint.title;
  let updated: Page;
  try {
    updated = await putWithCollaborationRetry<Page>(`/pages/${pageId}`, {
      title: checkpoint.title.trim(), ...snapshot, ...(checkpoint.aiAssisted ? { aiAssisted: true } : {}), expectedVersion }, undefined, timed ? 0 : undefined);
  } catch (failure) {
    // A live checkpoint's body is the shared session's whatever the version: refused for a version another writer's rename moved on,
    // it goes once more on the stored version (rebasedCheckpoint).
    if (rebased || !snapshot.collaborationGeneration || page.value?.id !== pageId || !(failure instanceof ApiError && failure.code === "version_conflict")) throw failure;
    return sendCheckpoint(await rebasedCheckpoint(checkpoint), timed, true);
  }
  savedSnapshot = { pageId, version: updated.version, title: checkpoint.title, contentJson: snapshot.contentJson, generation: snapshot.collaborationGeneration };
  if (renamed || page.value?.id !== pageId) void loadTree(updated.spaceId).catch(() => {});
  // Lists and connections follow when editing ends, not with every pause in typing.
  if (!editorActive.value) libraryRefresh.value++;
  if (page.value?.id !== pageId) return;
  page.value = updated;
  if (aiAppliedEpoch === checkpoint.aiEpoch) pendingAiAssisted.value = false;
  // Changes typed while the request was on its way wait for the next checkpoint.
  dirty.value = documentEditEpoch.value !== checkpoint.edits;
  savedOnPage.value = pageId;
}
/**
 * The checkpoint again on the page as stored now (COL2-11: the server takes a live body over another writer's version when the title is the
 * stored one). The stored title replaces this tab's, unless this writer renamed the page too: their title then goes, over the new version,
 * when nobody else renamed it meanwhile, and otherwise the field says whose title won.
 */
async function rebasedCheckpoint(checkpoint: Checkpoint): Promise<Checkpoint> {
  const fresh = await api<Page>(`/pages/${encodeURIComponent(checkpoint.pageId)}`);
  const current = page.value;
  if (current?.id !== checkpoint.pageId) return { ...checkpoint, title: fresh.title };
  const mine = checkpoint.title.trim(), renamedHere = mine !== current.title.trim(), renamedThere = fresh.title !== current.title;
  page.value = { ...current, title: fresh.title, version: fresh.version, parentId: fresh.parentId, isPublished: fresh.isPublished, publishedVersion: fresh.publishedVersion };
  void loadTree(fresh.spaceId).catch(() => {});
  if (renamedHere && !renamedThere) return checkpoint;
  await adoptTitle(fresh.title);
  if (renamedHere) titleError.value = t.toast.titleChangedByOther(fresh.title, mine);
  return { ...checkpoint, title: fresh.title };
}
/** Another writer's title as stored: the field shows it (watch(title) above), unless a title is being typed there. */
async function adoptTitle(next: string) {
  const typing = titleDirty() ? titleDraft.value : undefined;
  title.value = next;
  await nextTick();
  if (typing !== undefined) titleDraft.value = typing;
}
/** One checkpoint of the open page (the scheduler's save). `source`: the „Zdroj“ tab locks the document and checkpoints on sync readiness. */
let forceCheckpoint = false;
async function checkpointDocument(source = false, timed = false) {
  const force = forceCheckpoint;
  forceCheckpoint = false;
  if ((!dirty.value && !force) || !page.value || !editor.value) return;
  const pageId = page.value.id;
  const ready = () => canSaveEditor.value || (source && canEditSource.value && browserOnline.value && editorReady.value && !!collaborationStatus.value?.sourceCheckpointReady);
  if (!ready()) await untilSaveReady();
  if (!ready()) throw new Error(browserOnline.value ? t.toast.waitForSync : t.toast.offlineSaved);
  // The server takes the checkpoint once its projection of the shared log is current (U4-11): waited for, not refused and polled.
  await untilProjected(() => collaborationStatus.value);
  // The wait above can end in the middle of a drag that began after the scheduler looked: the capture serialises the whole page.
  if (timed) await untilQuiet();
  if (page.value?.id !== pageId) return;
  const checkpoint = captureCheckpoint();
  if (checkpoint) await sendCheckpoint(checkpoint, timed);
}
const checkpointError = ref("");
/** The page whose checkpoint went in this view: from then on the save state stays shown (Uloženo). */
const savedOnPage = ref<string>();
const autosave = createCheckpointScheduler(async timed => {
  try { await checkpointDocument(false, timed); checkpointError.value = ""; }
  // The server still projecting the shared log is waiting, not a failure: the scheduler tries again with a backoff and „Ukládám…“
  // stays meanwhile (the server stores the page itself within MarkdownSyncOptions.ServerCheckpointInterval, 30 s, too).
  // A checkpoint of a generation the page left waits the same way, while the editor joins the new one.
  catch (failure) {
    if (!checkpointWaits(failure)) checkpointError.value = errorMessage(failure);
    // Refused: the right to write went (or the page did); the page is read again and says so.
    if (failure instanceof ApiError && failure.status === 403 && page.value) void followPage(page.value.id, true);
    throw failure;
  }
}, { retry: checkpointWaits });
function checkpointWaits(failure: unknown) { return isCollaborationPending(failure) || isCollaborationSuperseded(failure); }
watch(canSaveEditor, ready => { if (ready && dirty.value) autosave.changed(); });
// Back online: what the outage said („Bez připojení…“) is over; the next checkpoint says how saving stands.
watch(() => browserOnline.value && collaborationStatus.value?.connection !== "offline" && collaborationStatus.value?.connection !== "reconnecting", online => {
  if (online) checkpointError.value = "";
});
// A checkpoint stored meanwhile (a colleague's, the server's own) covers what this tab holds: its waiting retry goes now, not seconds later.
watch(() => collaborationStatus.value?.projectionSave, projection => {
  if (projection === "current" && dirty.value && autosave.retries > 0 && !autosave.busy) autosave.soon();
});
watch(() => page.value?.id, () => { autosave.cancel(); checkpointError.value = ""; });
/** Leaving the page or the tab: its last changes go now, captured before the editor goes, after a checkpoint still on its way. */
function leaveCheckpoint() {
  if (!dirty.value || !canSaveEditor.value) return;
  const checkpoint = captureCheckpoint();
  if (!checkpoint) return;
  autosave.cancel();
  dirty.value = false;
  void autosave.flush().catch(() => {}).then(() => sendCheckpoint(checkpoint)).catch(failure => {
    toaster.error(t.toast.checkpointFailed(checkpoint.title, errorMessage(failure)));
  });
}
function hidingTab() { if (document.visibilityState === "hidden" && dirty.value) void autosave.flush().catch(() => {}); }
/**
 * The open page as others change it (PageChanged on its channel; nothing polls). A saved revision under another title (another writer's
 * rename) moves a writer's tree, breadcrumb and title; a publication, its mode, a move or the trash are read again for everyone. A live
 * page's reader gets saved revisions from the shared session. A burst of changes is read once, after it.
 */
const pageGone = ref<string>();
/** The right to write the open page went while it was open: the notice says so and keeps the writer's unsaved text (Markdown) to copy. */
const writeRevoked = ref<{ pageId: string; unsaved?: string; copied?: boolean }>();
watch(() => page.value?.id, id => { if (id && writeRevoked.value?.pageId !== id) writeRevoked.value = undefined; });
async function copyRevokedText() {
  const held = writeRevoked.value;
  if (!held?.unsaved) return;
  try { await navigator.clipboard.writeText(held.unsaved); held.copied = true; toaster.success(t.toast.revokedTextCopied); }
  catch { toaster.error(t.toast.revokedTextCopyFailed); }
}
let pageFollow: { close(): void } | undefined, followTimer: ReturnType<typeof setTimeout> | undefined, followAll = false;
watch(() => page.value?.id, pageId => {
  pageFollow?.close();
  pageFollow = undefined;
  clearTimeout(followTimer);
  followAll = false;
  pageGone.value = undefined;
  if (!pageId) return;
  const topic = pageTopic(pageId, "PageChanged"), live = liveConnection();
  live.route(topic.event, topic.route!);
  const changed = (all: boolean) => {
    followAll ||= all;
    clearTimeout(followTimer);
    followTimer = setTimeout(() => { const whole = followAll; followAll = false; void followPage(pageId, whole); }, 1000);
  };
  let subscriptions = 0;
  pageFollow = live.subscribe(topic.channel, {
    message: (event, payload) => {
      if (event !== topic.event) return;
      const change = payload as { kind?: unknown; title?: unknown } | null;
      // A saved revision names its title: one with the title shown (this tab's own checkpoints, a colleague's text) needs no read.
      if (change?.kind !== "working") changed(true);
      else if (change.title !== page.value?.title) changed(false);
    },
    // After a reconnect a change may have been missed; a revoked channel (the trash, a lost right) is read again too.
    subscribed: () => { if (subscriptions++) changed(true); },
    revoked: () => changed(true),
  });
});
onUnmounted(() => { pageFollow?.close(); clearTimeout(followTimer); });
/**
 * The editor joined the page's new generation by itself (another writer restored a version or replaced the source): a calm word of who
 * did it, read from the newest version, nothing when it was this account (another tab) or no explicit version of someone else.
 * shortcut: a source replace reads as a restore (the history does not tell them apart); add a marker to the revision if that misleads.
 */
async function pageReopened() {
  const pageId = page.value?.id;
  if (!pageId) return;
  try {
    const newest = (await api<{ items: PageRevisionSummary[] }>(`/pages/${encodeURIComponent(pageId)}/history?limit=1`)).items[0];
    if (page.value?.id === pageId && newest?.isExplicit && !newest.label && newest.authorId !== user.value?.id)
      toaster.success(t.toast.restoredBy(newest.authorName));
  } catch { /* only the notice is missing */ }
}
// The shared session lost the page (the trash, a revoked right): the page is read again rather than left at the session's failure.
watch(() => /\[(FORBIDDEN|READ_ONLY)\]/.test(collaborationStatus.value?.error?.message ?? ""), lost => {
  if (lost && page.value) void followPage(page.value.id, true);
});
async function followPage(pageId: string, all: boolean) {
  const current = page.value;
  if (current?.id !== pageId || (!all && !pageCanWrite.value)) return;
  if (!all) {
    await loadTree(current.spaceId).catch(() => {});
    const node = pagesBySpace.value[current.spaceId]?.find(item => item.id === pageId);
    if (node) await followRename(pageId, node);
    return;
  }
  let fresh: Page;
  try { fresh = await api<Page>(`/pages/${encodeURIComponent(pageId)}`); }
  catch (failure) {
    if (failure instanceof ApiError && (failure.status === 404 || failure.status === 403)) pageWentAway(pageId);
    return;
  }
  if (page.value?.id !== pageId) return;
  void loadTree(fresh.spaceId).catch(() => {});
  // Back from the trash, moved to another space (a new live generation) or the right to write came or went: the page opens again.
  if (pageGone.value === pageId || fresh.spaceId !== current.spaceId || fresh.canEdit !== current.canEdit) {
    // Only this tab holds what it typed after the right went (the server refused it): kept for the notice, or it would vanish unsaid.
    const revoked = current.canEdit && !fresh.canEdit && pageGone.value !== pageId && fresh.spaceId === current.spaceId;
    let unsaved: string | undefined;
    if (revoked && (dirty.value || !!checkpointError.value || (collaborationStatus.value?.pendingChanges ?? 0) > 0))
      try { unsaved = editor.value?.capture().markdown; } catch { /* a failed editor holds nothing to copy */ }
    await reopenPage(pageId);
    if (revoked && page.value?.id === pageId) writeRevoked.value = { pageId, unsaved: unsaved || undefined };
    return;
  }
  // A reader shows what they may read: a new publication, or the page going live or back to its publication (Živě ↔ Ručně).
  if (!pageCanWrite.value && (!!fresh.live !== !!current.live || (!fresh.live && fresh.publishedVersion !== current.publishedVersion))) {
    page.value = fresh;
    title.value = fresh.title;
    documentValue.value = readDocument(fresh.contentJson);
    editorReady.value = false;
    editorMountVersion.value++;
    return;
  }
  page.value = { ...page.value, isPublished: fresh.isPublished, publishedVersion: fresh.publishedVersion };
  await followRename(pageId, fresh);
  if (pageCanWrite.value && fresh.publishedVersion !== null) void readPublication(pageId);
}
/** Another writer's rename or move: the title follows unless this tab renamed the page too (its checkpoint then says whose won). */
async function followRename(pageId: string, next: { title: string; version: number; parentId: string | null }) {
  const current = page.value;
  if (current?.id !== pageId) return;
  const renamed = next.title !== current.title && next.version > current.version && title.value === current.title;
  if (!renamed && next.parentId === current.parentId) return;
  page.value = { ...current, parentId: next.parentId, ...(renamed ? { title: next.title, version: next.version } : {}) };
  if (renamed) await adoptTitle(next.title);
}
/** The page went to the trash, or the right to read it went, while open: the editor turns read-only and leaves the session, the writer's
 *  bar and save state go, and the notice above the text says so (a trash refuses while others' live edits are unsaved). */
function pageWentAway(pageId: string) {
  const current = page.value;
  if (current?.id !== pageId) return;
  autosave.cancel();
  dirty.value = false;
  checkpointError.value = "";
  editorActive.value = false;
  pageGone.value = pageId;
  page.value = { ...current, canEdit: false };
  void loadTree(current.spaceId).catch(() => {});
}
// The server ended the page's channel with the trash, so an untrash by someone else is never pushed here: the page is read again whenever
// the tab is looked at, and followPage opens it when it is readable again (the banner and the read-only state go with the reopen).
watch(pageGone, (id, _before, onCleanup) => {
  if (!id) return;
  const again = () => { if (document.visibilityState !== "hidden") void followPage(id, true); };
  window.addEventListener("focus", again);
  document.addEventListener("visibilitychange", again);
  onCleanup(() => { window.removeEventListener("focus", again); document.removeEventListener("visibilitychange", again); });
});
async function reopenPage(id: string) {
  dirty.value = false;
  savedOnPage.value = undefined; // „Uloženo“ of the page before is not the reopened page's
  page.value = undefined;
  documentValue.value = undefined;
  editorActive.value = false;
  await nextTick();
  await openPage(id);
}
/** Ukládám… / Uloženo / Offline / Chyba (SaveState) for the open page, once it changed in this view. */
const saveState = computed<SaveStateKind | null>(() => {
  if (!page.value || pageGone.value === page.value.id) return null;
  const connection = collaborationStatus.value?.connection;
  // Offline the editor is read-only by design (a fresh authenticated join, no typing cached across it): say so, not "uloží se".
  if (pageCanWrite.value && (!browserOnline.value || connection === "offline")) return "paused";
  if (savedOnPage.value !== page.value.id && !dirty.value && !checkpointError.value) return null;
  if ((dirty.value || checkpointError.value) && connection === "reconnecting") return "offline";
  if (checkpointError.value) return "error";
  return dirty.value ? "saving" : "saved";
});
/** The last keystrokes on their way to the shared session (at most 5 s); a checkpoint still not ready then says why. */
function untilSaveReady(timeoutMs = 5000): Promise<void> {
  if (canSaveEditor.value) return Promise.resolve();
  return new Promise(resolve => {
    const done = () => { clearTimeout(timer); stop(); resolve(); };
    const timer = setTimeout(done, timeoutMs);
    const stop = watch(canSaveEditor, ready => { if (ready) done(); });
  });
}
/** What the last checkpoint sent at `version`: publishing skips a checkpoint that would change nothing (E). */
let savedSnapshot: { pageId: string; version: number; title: string; contentJson?: string; generation?: string } | undefined;
function savedAsShown(pageId: string) {
  const saved = savedSnapshot;
  if (!saved || dirty.value || saved.pageId !== pageId || saved.version !== page.value?.version || saved.title !== title.value || !editor.value) return false;
  // A live checkpoint sends no content (the server saved its projection): every change since, a colleague's too, made the page dirty.
  if (saved.contentJson === undefined) return true;
  // The visible snapshot, including remote collaborative edits that emit no local change.
  const shown = editor.value.capture();
  return shown.contentJson === saved.contentJson && shown.collaborationGeneration === saved.generation;
}
/** The live session's visible state as a checkpoint, unless it is exactly what the last one sent (remote hydration emits no change). */
async function flushCheckpoint() {
  if (collaborationStatus.value?.enabled && page.value && !savedAsShown(page.value.id)) forceCheckpoint = true;
  await autosave.flush();
}
/** Publikovat checkpoints first, so the publication check (E) reads the revision Publish publishes. */
/** Publikovat may be pressed while the page still saves: it answers at once (pending state) and waits for the save itself. */
async function preparePublication() {
  await untilSaveReady(10_000);
  await run(flushCheckpoint);
  return !error.value;
}
/** Saved changes readers do not see yet: the draft (koncept) in the top bar and Zveřejnit změny beside it. */
const unpublishedChanges = computed(() => !!page.value && page.value.version !== page.value.publishedVersion
  && !(nothingToPublish.value && page.value.publishedVersion !== null));
/** What the writer's top bar says (owner 2026-11-07, variant A): a draft tints it, a live page says Živě; a reader's says nothing. */
const barPublication = computed(() => !pageCanWrite.value || !page.value ? undefined
  : unpublishedChanges.value ? 'draft' : publishedLive.value ? 'live' : 'published');
/** The draft in words: the version readers read meanwhile (its date once the publication is read). */
const draftText = computed(() => draftWording(page.value?.publishedVersion != null, publication.value?.publishedAt));
/** Automaticky publishes only a page published before and never a template (AutoPublish.cs): only then the bar says so. */
const draftPublishesItself = computed(() => publication.value?.effective === "Automatic" && page.value?.publishedVersion != null && !page.value.isTemplate);
/** The writer's page's publication (live or not, since when): read on opening a published page and after each publication. */
async function readPublication(pageId: string) {
  let value: PagePublication;
  try { value = await pagePublicationApi.get(pageId); } catch { return; }
  if (page.value?.id === pageId) publication.value = value;
}
// After the content (afterContent): the dock's Obsah beside it reads the publication meanwhile and hands it over (PageSidePanel
// @publication), and a page opened asked twice; one already known for this published version is not asked for again.
watch(() => (afterContent.value && pageCanWrite.value && page.value?.publishedVersion != null ? `${page.value.id}:${page.value.publishedVersion}` : undefined), key => {
  if (key && page.value && publication.value?.publishedVersion !== page.value.publishedVersion) void readPublication(page.value.id);
});
watch(() => page.value?.id, () => { publication.value = undefined; }, { flush: "sync" });
/** „Publikovaná“ beside a published page's draft: the revision readers read, read-only, while the live editor keeps its session
 *  hidden underneath (switching back is instant, nothing typed is lost). Only a writer's draft of a published page has it. */
const publishedView = ref(false);
const canViewPublished = computed(() => barPublication.value === 'draft' && !!publication.value?.publishedRevisionId);
const publishedRevision = shallowRef<PageRevision>();
const publishedRevisionError = ref("");
watch(canViewPublished, can => { if (!can) publishedView.value = false; });
// A draft that can be discarded: the confirmation's chunk is in before it is asked for, so the dialog opens in the same task.
watch(canDiscard, can => { if (can) void preloadComponent(DiscardChangesDialog); }, { immediate: true });
watch(() => (publishedView.value ? publication.value?.publishedRevisionId ?? undefined : undefined), async (revisionId, _, cleanup) => {
  if (!revisionId || !page.value) return;
  if (publishedRevision.value?.id === revisionId) return;
  const abort = new AbortController();
  cleanup(() => abort.abort());
  publishedRevision.value = undefined;
  publishedRevisionError.value = "";
  try { publishedRevision.value = await historyApi.revision(page.value.id, revisionId, abort.signal); }
  catch (failure) { if (!abort.signal.aborted) publishedRevisionError.value = errorMessage(failure); }
});
/** Back to the draft: the focus that „Zpět ke konceptu“ held goes to the editor. */
function leavePublishedView() {
  publishedView.value = false;
  void nextTick(() => { if (!document.activeElement || document.activeElement === document.body) editor.value?.focus("visible"); });
}
function publishedViewKeys(event: KeyboardEvent) {
  // Escape in a dialog or a menu is theirs (they are portalled out of the workspace).
  if (event.key !== "Escape" || event.defaultPrevented || !publishedView.value) return;
  event.preventDefault();
  leavePublishedView();
}
const documentEditEpoch=ref(0), pendingAiAssisted=ref(false);let aiAppliedEpoch=0;
watch(()=>page.value?.id,()=>{pendingAiAssisted.value=false;aiAppliedEpoch++;});
const publicationContextKey=computed(()=>page.value ? `${page.value.id}:${page.value.version}:${documentEditEpoch.value}:${title.value}` : "");
// Read by the publish panel when it asks, not by the shell: the key changes with every keystroke and the shell would render again.
const publicationContextKeyOf = () => publicationContextKey.value;
// The same for the publish button: the shared session's sync flips canSaveEditor twice a keystroke.
const publishDisabledOf = () => pending.value || !canPublishSoon.value;
const publishAssistant=usePublishAssistant({page,checkAllowed:computed(()=>editable.value && aiCapabilities.value.publishCheck),summaryAllowed:computed(()=>editable.value && aiCapabilities.value.changeSummary),prepare:preparePublication,snapshot:()=>editor.value?.capture().contentJson ?? "",contextKey:publicationContextKey});
const ordinaryPublicationChecks=createPublicationChecks({page:()=>page.value,save:preparePublication});
const publicationChecks=()=>{publishAssistant.startSpelling();return ordinaryPublicationChecks();};
/** Resolves `true` once published and `false` when nothing was; rejects with the failure for the publish panel. */
async function publish(changeSummary: string | null = null) {
  let done = false;
  await run(async () => {
    if (!page.value) return;
    await untilSaveReady(10_000);
    if (!canSaveEditor.value) throw new Error(t.toast.waitForSync);
    const publishedPageId = page.value.id;
    publishingPageId.value = publishedPageId;
    try {
      // The visible collaborative snapshot is checkpointed before choosing the immutable revision to publish, unless it is exactly
      // what the last checkpoint sent. Without a live session the saved revision is what gets published.
      await flushCheckpoint();
      if (page.value?.id !== publishedPageId) return;
      if (dirty.value) throw new Error(t.toast.moreChangesPublish);
      const published = await post<Page>(`/pages/${publishedPageId}/publish`, publishRequest(page.value.version, changeSummary, !collaborationStatus.value?.enabled));
      if (page.value?.id !== publishedPageId) return;
      page.value = published;
      libraryRefresh.value++;
      await loadTree(page.value.spaceId);
      confirmDocument(
        dirty.value
          ? t.toast.savedVersionPublished
          : t.toast.pageWasPublished,
      );
      done = true;
      // Publikovat leaves the bar with the publication; the focus it held goes to the editor rather than to the page body.
      await nextTick();
      if (!document.activeElement || document.activeElement === document.body) editor.value?.focus("visible");
    } finally {
      publishingPageId.value = null;
    }
  });
  if (!done && error.value) throw new Error(error.value);
  return done;
}
/** „Uložit verzi“ (Ctrl+S, the page menu): the last changes as a checkpoint, then the current revision named in the history (3.6-C). */
function openSaveVersion() {
  if (!pageCanWrite.value || !page.value) return;
  saveVersionLabel.value = "";
  saveVersionError.value = "";
  saveVersionOpen.value = true;
}
async function saveVersion() {
  const current = page.value, label = saveVersionLabel.value.trim();
  if (!current || saveVersionBusy.value) return;
  saveVersionBusy.value = true;
  saveVersionError.value = "";
  try {
    await flushCheckpoint();
    if (page.value?.id !== current.id) return;
    await historyApi.nameVersion(current.id, page.value.version, label || null);
    saveVersionOpen.value = false;
    toaster.success(label ? t.toast.versionSavedNamed(label) : t.toast.versionSaved);
  } catch (failure) { saveVersionError.value = errorMessage(failure); }
  finally { saveVersionBusy.value = false; }
}
/** The live document as a file, when its changes cannot reach the server (a lost right, a failing checkpoint). */
function downloadDraft() {
  if (!editor.value || !page.value) return;
  const blob = new Blob(
    [
      JSON.stringify(
        { title: title.value, ...editor.value.capture() },
        null,
        2,
      ),
    ],
    { type: "application/json" },
  );
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = `atlas-${page.value.id}-draft.json`;
  link.click();
  URL.revokeObjectURL(url);
}
/** `keepOpen`: a move between two sections of the profile discards the drafts but does not close the profile, so the page stays mounted and fades one section into the next. */
function resetProfile(keepOpen = false) {
  if (user.value) adoptAppearance(user.value.appearance);
  readingWidth.value = savedReadingWidth.value;
  if (!keepOpen) profileOpen.value = false;
  profileDirty.value = false;
  profileError.value = "";
  // The appearance confirmation is a toast now and owns its own lifetime, but a
  // page-scoped message ("Stránka byla publikována.") can still be standing here.
  clearTimeout(messageTimer);
  message.value = "";
  messagePageId.value = undefined;
}
// Navigating away from an open profile while it holds unsaved changes (a link click,
// browser back/forward, opening a different page) used to fall back on a bare
// window.confirm() stacked on top of ProfilePage's own UnsavedChangesDialog. It now
// reuses that same dialog so there is exactly one, consistent leave-confirmation UI.
const profileLeaveGuard = ref<{ fromHistory: boolean; keepOpen: boolean; resolve: (proceed: boolean) => void } | null>(null);
const profileLeaveSaving = ref(false);
function leaveProfile(fromHistory = false, keepOpen = false): boolean | Promise<boolean> {
  if (!profileOpen.value) return true;
  if (profilePending.value || profileBusy.value) {
    if (fromHistory) history.pushState(null, "", currentRoute());
    return false;
  }
  if (!profileDirty.value && !appearanceDirty.value) {
    resetProfile(keepOpen);
    return true;
  }
  return new Promise<boolean>(resolve => { profileLeaveGuard.value = { fromHistory, keepOpen, resolve }; });
}
function stayInProfile() {
  const guard = profileLeaveGuard.value;
  profileLeaveGuard.value = null;
  if (!guard) return;
  if (guard.fromHistory) history.pushState(null, "", currentRoute());
  guard.resolve(false);
}
function discardProfileAndLeave() {
  const guard = profileLeaveGuard.value;
  profileLeaveGuard.value = null;
  resetProfile(guard?.keepOpen);
  guard?.resolve(true);
}
async function saveProfileLeaveGuard() {
  profileLeaveSaving.value = true;
  try {
    await saveProfile();
    if (profileError.value) return;
    const guard = profileLeaveGuard.value;
    profileLeaveGuard.value = null;
    resetProfile(guard?.keepOpen);
    guard?.resolve(true);
  } finally {
    profileLeaveSaving.value = false;
  }
}
async function openProfile(tab: ProfileTab = "account", fromHistory = false) {
  if (moduleSetup.value || pending.value || profilePending.value || profileBusy.value) {
    if (fromHistory) history.pushState(null, "", currentRoute());
    return;
  }
  // Účet and Vzhled are halves of one page: moving between them keeps the page and its drafts.
  if (profileOpen.value && profilePageOf(profileTab.value) !== profilePageOf(tab) && !(await leaveProfile(fromHistory, true))) return;
  if (!profileOpen.value) {
    // The profile does not lie over a settings surface: a draft there is asked about, the way back remembers the
    // settings address, and the surface is closed so that only the profile's sections are in the rail.
    if (administrationOpen.value && !guardNavigation(fromHistory, () => void openProfile(tab))) return;
    if (!fromHistory) profileReturnRoute.value = currentRoute();
    if (administrationOpen.value) { administrationOpen.value = false; settingsLocation.value = undefined; settingsDirty.value = false; }
    navigationVersion++;
    openingPage.value = false;
  }
  profileTab.value = tab;
  profileOpen.value = true;
  profileError.value = "";
  sidebarOpen.value = false;
  const address = buildShellHash({ view: "profile", tab });
  if (!fromHistory && location.hash !== address) history.pushState(null, "", address);
  await nextTick();
  document.getElementById("profile-heading")?.focus();
}
async function cancelProfile() {
  if (!(await leaveProfile())) return;
  const underlyingRoute = currentRoute();
  history.pushState(null, "", profileReturnRoute.value);
  if (underlyingRoute !== profileReturnRoute.value) await historyNavigation();
}
function cancelAppearance() {
  if (profilePending.value || !user.value) return;
  adoptAppearance(user.value.appearance);
  readingWidth.value = savedReadingWidth.value;
  profileError.value = "";
}
async function saveProfile() {
  if (profilePending.value || !user.value) return;
  const owner = user.value.id;
  profilePending.value = true;
  profileError.value = "";
  try {
    const saved = await put<Appearance>("/profile/appearance", { ...appearance });
    if (user.value?.id !== owner) return;
    if (saved.collaboratorColor !== user.value.appearance.collaboratorColor) preferredColorChanged(owner, saved.collaboratorColor ?? "auto");
    user.value.appearance = { ...saved, motion: saved.motion ?? "system" };
    adoptAppearance(saved);
    // The reading width belongs to this browser; it is kept once the account's part is saved.
    savedReadingWidth.value = readingWidth.value;
    saveReadingPreferences();
    // Another language is a new page load (no reactive texts, src/i18n).
    if (rememberLanguage(saved.language)) { location.reload(); return; }
    // A toast, not the document's status slot: this confirmation is profile-scoped, and as
    // a page-scoped message it had to be hand-cleared on close so the document behind
    // it never inherited it (findings-settings §4.3). A toast owns its own
    // lifetime, so that whole coupling goes away.
    toaster.success(t.toast.appearanceSaved);
  } catch (failure) {
    if (user.value?.id === owner) profileError.value = errorMessage(failure);
  } finally {
    profilePending.value = false;
  }
}
function searchUrl(open: boolean, push = false) {
  const url = new URL(location.href);
  for (const key of ["search", "searchView", "q", "scope", "content", "changedWithin", "sort", "tag", "tagName"]) url.searchParams.delete(key);
  if (open) {
    url.searchParams.set("search", "1");
    if (searchView.value === "page") url.searchParams.set("searchView", "page");
    if (query.value) url.searchParams.set("q", query.value);
    if (searchTag.value) { url.searchParams.set("tag", searchTag.value.id); url.searchParams.set("tagName", searchTag.value.name); }
    if (searchSpace.value) url.searchParams.set("scope", searchSpace.value);
    if (searchContent.value !== "all") url.searchParams.set("content", searchContent.value);
    if (searchChangedWithin.value !== "any") url.searchParams.set("changedWithin", searchChangedWithin.value);
    if (searchSort.value !== "relevance") url.searchParams.set("sort", searchSort.value);
  }
  history[push ? "pushState" : "replaceState"](!push && url.href === location.href ? history.state : null, "", url);
}
function rememberSearchView() {
  if (!searchIsOpen.value || new URL(location.href).searchParams.get("search") !== "1") return;
  history.replaceState({ ...history.state, atlasSearchView: {
    index: searchIndex.value,
    scrollTop: document.querySelector(".search-dialog .search-content")?.scrollTop ?? 0,
  } }, "");
}
async function openSearch(fromHistory: boolean | Event = false) {
  if (moduleSetup.value) return;
  if (!searchIsOpen.value) { routeAtSearch = currentRoute(); caretAtSearch = editorCaret(); selectionAtSearch.value = []; }
  const linked = new URL(location.href);
  if (fromHistory === true) {
    const view = history.state?.atlasSearchView;
    searchViewToRestore = view && Number.isSafeInteger(view.index) && view.index >= 0 && Number.isFinite(view.scrollTop) && view.scrollTop >= 0
      ? { url: linked.href, index: view.index, scrollTop: view.scrollTop } : undefined;
    query.value = (linked.searchParams.get("q") ?? "").slice(0, 200);
    const tagId = linked.searchParams.get("tag");
    searchTag.value = tagId && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(tagId)
      ? { id: tagId, name: (linked.searchParams.get("tagName") || t.toast.selectedTag).slice(0, 48) } : undefined;
    const scope = linked.searchParams.get("scope");
    searchSpace.value = spaces.value.some(s => s.id === scope) ? scope! : "";
    const content = linked.searchParams.get("content") ?? "all";
    searchContent.value = ["published", "drafts"].includes(content) ? content : "all";
    const changed = linked.searchParams.get("changedWithin") ?? "any";
    searchChangedWithin.value = ["day", "week", "month"].includes(changed) ? changed : "any";
    searchSort.value = linked.searchParams.get("sort") === "published" ? "published" : "relevance";
  } else if (!searchIsOpen.value) {
    searchViewToRestore = undefined;
    filtersOpen.value = false;
    query.value = "";
    resetSearchFilters();
  }
  if (!searchIsOpen.value) {
    commandAfterSearchClose = undefined; // reopened before the fold ended: the command was abandoned with it
    searchView.value = fromHistory === true && linked.searchParams.get("searchView") === "page" ? "page" : "dialog";
    searchIsOpen.value = true;
    // The dialog opens first; the selection's actions (a query over a big document, ~0.3 s at 4x) fill the palette the next frame (the selection stays while the dialog has the focus).
    requestAnimationFrame(() => { if (searchIsOpen.value) selectionAtSearch.value = editor.value?.selectionActions() ?? []; });
    if (fromHistory !== true) searchUrl(true, linked.searchParams.get("search") !== "1");
    scheduleSearch();
  }
  await nextTick();
  fieldElement(searchField.value)?.focus();
}
function stopSearch() {
  searchIsOpen.value = false;
  clearTimeout(searchTimer);
  clearTimeout(searchEnrichmentTimer);
  searchAbort?.abort();
  searchVersion++;
  searching.value = false;
  searchOpening.value = false;
  loadingMore.value = false;
}
async function openTagSearch(tag: PageTag) {
  await openSearch();
  searchTag.value = tag;
  query.value = "";
}
function resetSearchFilters() {
  searchTag.value = undefined;
  searchSpace.value = "";
  searchContent.value = "all";
  searchChangedWithin.value = "any";
  searchSort.value = "relevance";
}
function openSearchResult(event: MouseEvent, result: SearchResult) {
  if (!isPlainNavigationClick(event)) return;
  event.preventDefault();
  if (!searchOpening.value) void openPage(result.id, false, false, result.headingId ?? undefined);
}
function openSearchBoard(event: MouseEvent, result: SearchResult) {
  if (!isPlainNavigationClick(event)) return;
  event.preventDefault();
  if (searchOpening.value) return;
  rememberSearchView();
  openBoard(result.id, false, result.spaceId);
}
function searchRequestPath(text: string, offset?: number, prefixOnly = false) {
  return buildSearchRequestPath(text, offset, prefixOnly, {
    spaceId: searchSpace.value, content: searchContent.value, changedWithin: searchChangedWithin.value, sort: searchSort.value,
    tagId: searchTag.value?.id, contextSpaceId: currentSpace.value?.id, relaxed: searchRelaxed.value,
  });
}
function closeSearch() {
  if (!searchIsOpen.value) return;
  stopSearch();
  searchUrl(false);
}
let searchMorphing = false;
/**
 * Dialog ⇄ whole page: the same search (query, filters, results, selection, scroll) in a larger layout, with its own address
 * (replaced, not pushed: Back still leaves the search, and returns to it from an opened result in the view it had).
 */
async function toggleSearchView() {
  if (!searchIsOpen.value || searchMorphing) return;
  searchMorphing = true;
  const view = searchView.value === "page" ? "dialog" : "page";
  const apply = async () => { searchView.value = view; await nextTick(); };
  try {
    // Lazy: the transition is the only code the page view adds (bundle budget).
    const morph = await import("./search/searchMorph").then(module => module.morphSearchPanel, () => null);
    await (morph ? morph(document.querySelector<HTMLElement>("dialog.search-dialog"), view, apply) : apply());
  } finally { searchMorphing = false; }
  if (!searchIsOpen.value) return;
  searchUrl(true);
  document.getElementById(searchResultId(searchIndex.value))?.scrollIntoView({ block: "nearest" });
  // From the switch (or nowhere) into the field; a row or the answer keeps its focus.
  const active = document.activeElement;
  if (!active || active === document.body || active.closest("[data-search-view-toggle]")) fieldElement(searchField.value)?.focus({ preventScroll: true });
}
/** Infinite results: the next page loads while the end of the pages (or headings) comes within 600 px of the view. */
let moreObserver: IntersectionObserver | undefined;
function observeMoreResults() {
  moreObserver?.disconnect();
  const root = document.querySelector(".search-dialog .search-content");
  if (!searchIsOpen.value || !root || typeof IntersectionObserver === "undefined") return;
  moreObserver = new IntersectionObserver(entries => { if (entries.some(entry => entry.isIntersecting)) void moreSearch(); }, { root, rootMargin: "0px 0px 600px 0px" });
  root.querySelectorAll("[data-search-more]").forEach(sentinel => moreObserver!.observe(sentinel));
}
// Observing again after every batch reports a sentinel that is still near (a short batch) at once, so loading goes on.
watch([searchResultKeys, hasMore, loadingMore, searchIsOpen], observeMoreResults, { flush: "post" });
let openWhenFresh = false;
watch(searchStale, async stale => {
  if (stale || !openWhenFresh) return;
  openWhenFresh = false; await nextTick();
  document.getElementById(searchResultId(searchIndex.value))?.click();
});
function scheduleSearch() {
  openWhenFresh = false;
  const text = searchQuestion.value;
  searchResultChosen.value = false; highlightedSource.value = null;
  const keepShown = !commandMode.value && (!!searchTag.value || text.length >= 2);
  clearTimeout(searchTimer);
  clearTimeout(searchEnrichmentTimer);
  searchAbort?.abort();
  searchVersion++;
  if (!keepShown) {
    results.value = [];
    searchSpaces.value = [];
    searchResources.value = [];
    hasMore.value = false;
    searchAttempted.value = false;
    searchSemantic.value = false;
    arrivals.reset();
  }
  didYouMean.value = "";
  searchRelaxed.value = false;
  searchStale.value = keepShown;
  searchNextOffset.value = null;
  loadingMore.value = false;
  searchError.value = "";
  searching.value = false;
  searchOpening.value = false;
  searchIndex.value = 0;
  if (!showSuggestions.value) suggestedPages.value = [];
  if (!keepShown) return;
  const version = searchVersion;
  // The prefix preview covers the meaning search's wait for its query vector. Without it the full pass answers as fast (UX SE3-1),
  // so one request per query.
  const progressive = !searchTag.value && text.length >= 3 && semanticOn.value;
  const abort = new AbortController();
  searchAbort = abort;
  searching.value = true;
  let previewShown = false;
  async function searchPass(prefixOnly: boolean) {
    try {
      const found = await api<{ items: SearchResult[]; hasMore: boolean; spaces?: typeof searchSpaces.value; resources?: typeof searchResources.value; nextOffset?: number | null; semantic?: boolean; didYouMean?: string; relaxed?: boolean }>(
        searchRequestPath(text, undefined, prefixOnly),
        { signal: abort.signal },
      );
      if (version === searchVersion) {
        searchError.value = "";
        // The row under the cursor (the preselected first one too) of this query's preview holds its place, by the page it came from.
        const under = previewShown ? searchGroups.value.flatMap(group => group.items)[searchIndex.value] : undefined;
        const anchor = under && /^(page|widget)-/u.test(under.key) ? under : undefined;
        results.value = prefixOnly || !anchor ? found.items : mergeSearchResults(results.value, found.items, searchItemKey(anchor.result), searchItemKey);
        if (anchor) searchIndex.value = Math.max(0, searchResultKeys.value.indexOf(anchor.key));
        previewShown = prefixOnly;
        searchSemantic.value = !prefixOnly && found.semantic === true;
        didYouMean.value = prefixOnly ? "" : found.didYouMean ?? "";
        searchRelaxed.value = !prefixOnly && found.relaxed === true;
        searchStale.value = false;
        void nextTick(() => { if (version === searchVersion) settleArrivals(!prefixOnly); });
        // Pagination belongs to the final ranked list, not its prefix-only preview.
        hasMore.value = !prefixOnly && found.hasMore;
        searchSpaces.value = found.spaces ?? [];
        // shortcut: the prefix pass names no boards, so they wait for the full pass; ask the server for them there too if it flickers.
        searchResources.value = prefixOnly ? [] : found.resources ?? [];
        searchNextOffset.value = prefixOnly ? null : found.nextOffset ?? (found.hasMore ? found.items.length : null);
        const view = searchViewToRestore;
        if (!prefixOnly && view?.url === location.href) {
          searchViewToRestore = undefined;
          // Settle the status row before restoring scroll; removing its loading text
          // afterwards would shift the restored position through browser anchoring.
          searching.value = false;
          searchAttempted.value = true;
          // The batches the reader had scrolled through (infinite results), up to the selected row.
          await restoreBatches(view.index, version, abort.signal);
          while (version === searchVersion && groupedResultCount.value <= view.index && searchNextOffset.value !== null && !searchError.value) await moreSearch();
          if (version !== searchVersion) return;
          searchIndex.value = Math.min(view.index, Math.max(0, groupedResultCount.value - 1));
          await nextTick();
          const content = document.querySelector(".search-dialog .search-content");
          if (version === searchVersion && content) content.scrollTop = view.scrollTop;
        }
      }
    } catch (failure) {
      if (!prefixOnly && !abort.signal.aborted && version === searchVersion) {
        // Results of an earlier query must not pose as the answer; a prefix preview of this one may stay.
        if (searchStale.value) { results.value = []; searchSpaces.value = []; searchResources.value = []; hasMore.value = false; }
        searchError.value = errorMessage(failure);
      }
    } finally {
      if (!prefixOnly && version === searchVersion) {
        searchStale.value = false;
        searching.value = false;
        searchAttempted.value = true;
      }
    }
  }
  // The first key of a burst answers at once; from the second character on, keys typed within the debounce window wait for it, which spares the server (and its rate limit) a request per key.
  let firstPass = Promise.resolve();
  const typingNow = performance.now(), typingFast = typingNow - lastSearchScheduleAt < SEARCH_TYPING_DEBOUNCE_MS;
  lastSearchScheduleAt = typingNow;
  searchTimer = setTimeout(() => { firstPass = searchPass(progressive); }, text.length > 1 && typingFast ? SEARCH_TYPING_DEBOUNCE_MS : 0);
  searchEnrichmentTimer = setTimeout(async () => {
    if (!progressive) return;
    // Serialize the two wiki requests: no duplicate DB work or late preview overwriting the full list.
    await firstPass;
    if (!abort.signal.aborted && version === searchVersion) await searchPass(false);
  }, 180);
}
const settleArrivals = (animate: boolean) => arrivals.settle(searchResultKeys.value.map((key, index) => [key, () => document.getElementById(`search-result-${index}`)] as [string, () => Element | null]), animate);
const SEARCH_TYPING_DEBOUNCE_MS = 70;
let lastSearchScheduleAt = -Infinity;
/** A result's page: a contact or heading row of it is the same result. */
const searchItemKey = (result: Pick<SearchResult, "id" | "isDraft">) => `${result.id}-${result.isDraft}`;
function acceptDidYouMean() { query.value = didYouMean.value; }
/** The next page; a call while one loads for the same search waits for it (the scroll observer and a restored view both ask). */
let moreLoad: { version: number; done: Promise<void> } | undefined;
function moreSearch(): Promise<void> {
  if (moreLoad?.version === searchVersion) return moreLoad.done;
  const load = { version: searchVersion, done: loadMoreResults() };
  moreLoad = load;
  void load.done.finally(() => { if (moreLoad === load) moreLoad = undefined; });
  return load.done;
}
/** Restoring a scrolled view fetches the batches up to the selected row together; loadingMore keeps the scroll observer from fetching the same batch again meanwhile. */
function appendBatch(batch: { items: SearchResult[]; hasMore: boolean; nextOffset: number | null }) {
  const known = new Set(results.value.map(x => x.id));
  results.value.push(...batch.items.filter(x => !known.has(x.id)));
  hasMore.value = batch.hasMore; searchNextOffset.value = batch.nextOffset;
}
async function restoreBatches(index: number, version: number, signal: AbortSignal) {
  if (loadingMore.value) await moreSearch();
  loadingMore.value = true;
  try {
    const found = await loadBatchesTogether(searchNextOffset.value, groupedResultCount.value, index, offset => api<{ items: SearchResult[]; hasMore: boolean; nextOffset: number | null }>(searchRequestPath(searchQuestion.value, offset), { signal }));
    if (version !== searchVersion || !found.length) return;
    for (const batch of found) appendBatch(batch);
  } catch { /* the one-by-one loop after this reports a real failure */ }
  finally { if (version === searchVersion) loadingMore.value = false; }
}
async function loadMoreResults() {
  if (loadingMore.value || searching.value || searchNextOffset.value === null) return;
  const version = searchVersion, offset = searchNextOffset.value;
  const controller = new AbortController(); searchAbort = controller;
  loadingMore.value = true; searchError.value = "";
  try {
    const found = await api<{ items: SearchResult[]; hasMore: boolean; nextOffset: number | null }>(searchRequestPath(searchQuestion.value, offset), { signal: controller.signal });
    if (version !== searchVersion) return;
    const selectedKey = searchResultKeys.value[searchIndex.value];
    appendBatch(found);
    searchIndex.value = Math.max(0, searchResultKeys.value.indexOf(selectedKey ?? ''));
    await nextTick();
    if (version === searchVersion) settleArrivals(true);
  } catch (failure) { if (!controller.signal.aborted && version === searchVersion) searchError.value = errorMessage(failure); }
  finally { if (version === searchVersion) loadingMore.value = false; }
}
async function openSearchSpace(id: string) {
  rememberSearchView();
  const space = spaces.value.find(x => x.id === id);
  if (space && await selectSpace(space)) closeSearch();
}
function openSearchSpaceLink(event: MouseEvent, id: string) {
  if (!isPlainNavigationClick(event)) return;
  event.preventDefault();
  if (!searchOpening.value) void openSearchSpace(id);
}
watch([query, searchTag, searchSpace, searchContent, searchChangedWithin, searchSort], () => { if (searchIsOpen.value) searchUrl(true); });
// The question, not the typed text: „? VPN“ (Ctrl+Enter, the ask mode) searches what „VPN“ already found.
watch([searchQuestion, commandMode, searchTag, searchSpace, searchContent, searchChangedWithin, searchSort], () => { if (searchIsOpen.value) scheduleSearch(); });
function searchKeys(event: KeyboardEvent) {
  const inField = event.target === fieldElement(searchField.value);
  // The first Esc empties the field, the next one closes the search (P3).
  if (inField && clearOnEscape(event, query.value, () => { query.value = ''; })) return;
  // Tab takes the preselected result as Enter does, once something is typed (P8); the question mode keeps its Tab.
  const tabTakes = inField && event.key === 'Tab' && !event.shiftKey && !event.ctrlKey && !event.metaKey && !event.altKey && !event.isComposing && !!query.value.replace(/^\s*[>?]/u, '').trim() && !askMode.value;
  if (!event.isComposing && event.key === 'Enter' && (event.ctrlKey || event.metaKey) && !event.altKey && !event.shiftKey && aiCapabilities.value.answer && event.target === fieldElement(searchField.value)) {
    event.preventDefault();
    const text = searchQuestion.value.replace(/^>\s*/u, '');
    query.value = '? ' + text;
    searchResultChosen.value = false;
    // The answer to this question already there or on its way stays the one (no second conversation).
    if (searchAnswer.state.value !== 'done') void nextTick(() => searchAnswer.ask());
    return;
  }
  if (commandMode.value) {
    if (event.isComposing || event.ctrlKey || event.metaKey || event.altKey || event.shiftKey) return;
    if (['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key) && (event.key.startsWith('Arrow') || commands.value.length || event.target !== fieldElement(searchField.value))) {
      event.preventDefault();
      const count = commands.value.length;
      // The first command is preselected, so ↓ goes on from it to the second (F13); ↑ from the untouched list goes to the last.
      searchIndex.value = count ? event.key === 'Home' ? 0 : event.key === 'End' ? count - 1 : !searchResultChosen.value && event.key === 'ArrowUp' ? count - 1 : (searchIndex.value + (event.key === 'ArrowDown' ? 1 : -1) + count) % count : 0;
      searchResultChosen.value = true;
      document.getElementById(`command-${commands.value[searchIndex.value]?.id}`)?.scrollIntoView({ block: 'nearest' });
    }
    if (event.key === 'Enter' || tabTakes) { const command = commands.value[searchIndex.value]; if (event.key === 'Enter' || command) event.preventDefault(); if (command) runCommand(command.id); }
    return;
  }
  if (searchOpening.value) {
    if (event.key === "Enter") event.preventDefault();
    return;
  }
  if (event.isComposing || event.ctrlKey || event.metaKey || event.altKey || event.shiftKey) return;
  // Home and End walk the list too while it has rows (F13); with nothing listed they move the caret.
  if (["ArrowDown", "ArrowUp", "PageDown", "PageUp"].includes(event.key) || ((event.key === "Home" || event.key === "End") && (searchResultCount.value > 0 || event.target !== fieldElement(searchField.value)))) {
    event.preventDefault();
    const count = searchResultCount.value;
    const step = event.key === "PageDown" ? 8 : event.key === "PageUp" ? -8 : event.key === "ArrowDown" ? 1 : event.key === "ArrowUp" ? -1 : 0;
    // The first row is preselected, so ↓ goes on from it (F13). Pressed before any row is there, the move is kept: the rows of the
    // answer take it (the searchResultKeys watcher clamps it to them) instead of the answer resetting it.
    searchIndex.value = !count ? (event.key === "Home" ? 0 : Math.max(0, searchIndex.value + step))
      : Math.max(0, Math.min(count - 1, event.key === 'Home' ? 0 : event.key === 'End' ? count - 1 : !searchResultChosen.value && event.key === 'ArrowUp' ? count - 1 : searchIndex.value + step));
    searchResultChosen.value = true;
    const selected = document.getElementById(searchResultId(searchIndex.value));
    selected?.scrollIntoView({ block: "nearest" });
    if (event.target !== fieldElement(searchField.value)) selected?.focus({ preventScroll: true });
  }
  if (event.key === "Enter" || tabTakes) {
    if (event.key === "Enter" && askMode.value && !searchResultChosen.value && event.target === fieldElement(searchField.value)) {
      event.preventDefault(); void searchAnswer.ask(); return;
    }
    // Never open a result of the previous query while the typed one is still loading.
    // The wish survives until the typed query answers (a further key cancels it): see openWhenFresh.
    if (searchStale.value && !showSuggestions.value) { event.preventDefault(); openWhenFresh = true; return; }
    const selected = document.getElementById(searchResultId(searchIndex.value));
    if (selected) { event.preventDefault(); selected.click(); }
  }
}
function selectSearchPointer(event: PointerEvent, index: number) {
  // Scrolling/reflow beneath a stationary pointer must not override keyboard selection.
  if (!event.movementX && !event.movementY) return;
  searchIndex.value = index;
  searchResultChosen.value = true;
}
function searchDialogKeys(event: KeyboardEvent) {
  // Tab in a typed field takes the first result (P8), so the scope and Filtry after the field have a key of their own: Ctrl+Shift+F lands on Filtry, Shift+Tab goes on to Prostor.
  if ((event.ctrlKey || event.metaKey) && event.shiftKey && !event.altKey && !event.isComposing && event.key.toLowerCase() === 'f' && !commandMode.value) {
    event.preventDefault();
    document.querySelector<HTMLElement>('.search-dialog .search-filter-toggle')?.focus();
    return;
  }
  if (event.key === 'F6' && !event.isComposing) {
    event.preventDefault();
    const field = fieldElement(searchField.value);
    const panel = document.querySelector<HTMLElement>('.search-answer-pane:not([inert])');
    const target = event.target === field && panel
      ? panel.querySelector<HTMLElement>('button:not(:disabled), a[href]') ?? panel
      : field;
    target?.focus({ preventScroll: true });
    return;
  }
  if (event.key !== "Escape" || event.isComposing || (event.target instanceof Element && event.target.closest('[role="menu"]'))) return;
  event.preventDefault();
  event.stopPropagation();
  closeSearch();
}
// One listener for every application shortcut (keyboard/shortcuts.ts): the registry says which key is which, this says what
// each does here and when it does not apply (then the key keeps its usual meaning).
const matchShortcut = createShortcutMatcher(appShortcuts);
/** After a shortcut changed the view: the logical place in it, once it has rendered (keyboard pass). */
function landAfterNavigation() {
  // Once the view has rendered, and when a view renders its heading only after loading, as soon as it does: the view is watched, no
  // timer chases it (P7, landFocusWhenRendered). Never while the caret is on its way into a page being edited (focusEditorWhenEditable owns that).
  // Nor while a menu, dialog or popover is there: one that closes drops focus to <body> until it hands it back to its trigger (SH-1).
  const allowed = () => !(focusEditorPending && editorActive.value) && !document.querySelector('[role="menu"], dialog[open], [data-slot="popover-content"]');
  stopLanding();
  const mine = landVersion;
  void nextTick(() => {
    const area = workspace.value;
    if (shellUnmounted || !area || mine !== landVersion) return;
    stopLandingWait = landFocusWhenRendered(area, allowed);
  });
}
// The wait of the last navigation: a newer one replaces it, an unmounted shell drops it (also a navigation's nextTick that runs
// after the unmount).
let stopLandingWait: (() => void) | undefined;
let shellUnmounted = false;
let landVersion = 0;
function stopLanding() { landVersion++; stopLandingWait?.(); stopLandingWait = undefined; }
// Every change of view, also from a header button, a card or a link (SH-1): landFocus moves only a reader whose focus went with the old view.
watch(currentRoute, landAfterNavigation);
// The page editor's own requests: Ctrl+/ in the text is the same help as outside it, Up at the start of the text goes to the title.
function editorAsksHelp(event: Event) {
  if (document.querySelector("dialog[open]") && !keyboardHelpOpen.value) return;
  event.preventDefault();
  keyboardHelpOpen.value = !keyboardHelpOpen.value;
}
function editorLeavesStart(event: Event) {
  const title = document.querySelector<HTMLTextAreaElement>(".document-header .document-title-text");
  if (!editable.value || !title || title.disabled || title.readOnly) return;
  event.preventDefault();
  title.focus();
  title.setSelectionRange(title.value.length, title.value.length);
}
function runShortcut(id: AppShortcutId, event: KeyboardEvent): boolean {
  const modal = document.querySelector("dialog[open]");
  const go = (open: () => unknown) => { if (modal) return false; void open(); landAfterNavigation(); return true; };
  switch (id) {
    case "search":
    case "commands":
      // Over a selection on a board, Ctrl/⌘+K opens the board's command panel (objects spec §5.2), not the search.
      if (id === "search" && (event.target as Element | null)?.closest?.("[data-board-surface][data-board-selection]")) return false;
      // Another dialog is on top; the palette shortcut belongs to the palette.
      if (modal && !modal.classList.contains("search-dialog")) return false;
      void openSearch().then(() => { if (id === "commands") void setSearchMode("commands"); });
      return true;
    case "searchView":
      // Only in the search: elsewhere the key keeps its meaning.
      if (!searchIsOpen.value || (modal && !modal.classList.contains("search-dialog"))) return false;
      void toggleSearchView();
      return true;
    case "assistant":
      // Ctrl/Cmd+J opens the assistant from anywhere (refresh 2.5), like the sidebar entry.
      if (!assistantReachable.value || modal) return false;
      launchAssistant();
      return true;
    case "help":
    case "helpAlt":
      if (modal && !keyboardHelpOpen.value) return false;
      keyboardHelpOpen.value = !keyboardHelpOpen.value;
      return true;
    case "newPage":
      if (modal || !currentSpace.value || !canWrite.value || profileOpen.value || administrationOpen.value) return false;
      showCreatePage();
      return true;
    case "edit":
      // The lock of editing (3.6-F); in the page's text the letter is never a shortcut (globalKeys).
      if (modal || !documentOpen.value || !pageCanWrite.value) return false;
      void toggleLock();
      return true;
    case "save":
      // On a page Ctrl+S never opens the browser's „Save page as“: it names a version (3.6-B).
      if (!documentOpen.value) return false;
      if (!modal && pageCanWrite.value) openSaveVersion();
      return true;
    case "findInPage": {
      // On an open page only, and not from another editor (a board's block keeps its own Najít a nahradit).
      const target = event.target instanceof Element ? event.target : null;
      if (modal || !pageDockTabs.value || (target?.closest('[contenteditable="true"]') && !documentBody.value?.contains(target))) return false;
      // Selected words become the search, as in the browser's own find.
      const picked = window.getSelection()?.toString().trim() ?? "";
      if (picked && picked.length <= 80 && !picked.includes("\n")) pageSearchQuery.value = picked;
      showPageDock("search");
      rememberPageDock(true);
      pageSearchFocus.value++;
      return true;
    }
    case "discussion":
      if (modal || (!boardView.value && (!documentOpen.value || !pageDockTabs.value))) return false;
      toggleDiscussion();
      return true;
    case "treeFilter": return !modal && treeFilterShortcut();
    case "goLibrary": return go(() => openLibrary("spaces"));
    case "goRecent": return go(() => openLibrary("recent"));
    case "goFavorites": return go(() => openLibrary("favorites"));
    case "goDrafts": return go(() => openLibrary("drafts"));
    case "goContacts": return go(() => openLibrary("contacts"));
    case "goPersonal": return go(() => openLibrary("personal"));
    case "goShared": return go(() => openLibrary("shared"));
    case "goNotifications": return go(() => openLibrary("notifications"));
    case "goProfile": return go(() => openProfile());
    case "goSettings":
      if (user.value?.isWikiAdmin) return go(openAdministration);
      if (currentSpace.value && spaceCanManage(currentSpace.value)) { const spaceId = currentSpace.value.id; return go(() => openSettings(spaceSettingsHash(spaceId))); }
      return false;
  }
}
/** When the last printable key went down: E typed amid a word („hello“) into a locked page is a letter, not the unlock (3.6-F). */
let lastPrintableAt = -Infinity;
const TYPING_MS = 1000;
function globalKeys(event: KeyboardEvent) {
  if (!user.value) return;
  const typing = event.timeStamp - lastPrintableAt < TYPING_MS;
  if (event.key.length === 1 && !event.ctrlKey && !event.metaKey && !event.altKey) lastPrintableAt = event.timeStamp;
  const shortcut = matchShortcut(event);
  if (!shortcut || moduleSetup.value || templateBusy.value) return;
  // While a page is edited, or typed into before its editor attaches, plain letters are text: they must not open the new-page form,
  // navigate away or touch the lock (WR3-1, 3.6-F). Only the Ctrl chords and the help stay.
  // A reader's read-only text (Tab lands on it) types nothing: the letters stay shortcuts there.
  const inPage = event.target instanceof Node && !!documentBody.value?.contains(event.target)
    && !(event.target instanceof Element && event.target.closest('[contenteditable="false"]'));
  // Alt C (`anywhere`) passes: it takes the selection from the page being edited too.
  if ((editorActive.value || inPage) && !event.ctrlKey && !event.metaKey && shortcut.where !== "anywhere") return;
  // E unlocks only as a deliberate first key; while typing it pulses the lock like the other letters.
  if (shortcut.id === "edit" && typing && pageLocked.value && documentOpen.value && pageCanWrite.value) { lockPulse.value++; event.preventDefault(); return; }
  if (!runShortcut(shortcut.id, event)) return;
  event.preventDefault();
  event.stopPropagation();
}
function beforeUnload(event: BeforeUnloadEvent) {
  // Live edits the server has not acknowledged yet (reconnecting, offline or refused) exist only in this tab.
  if ((collaborationStatus.value?.pendingChanges ?? 0) > 0 || (!!writeRevoked.value?.unsaved && !writeRevoked.value.copied) || documentDraft() || hasUnsavedWork() || titleDirty() || settingsDirty.value || pdfExportDirty.value || pdfExportBusy.value || profileDirty.value || appearanceDirty.value || profilePending.value || profileBusy.value || templateDirty.value || templateBusy.value) {
    event.preventDefault();
    event.returnValue = "";
  }
}
const colorQuery = window.matchMedia("(prefers-color-scheme: dark)");
function colorChanged(event: MediaQueryListEvent) {
  systemDark.value = event.matches;
}
let historyVersion = 0;
async function historyNavigation() {
  if (!user.value || moduleSetup.value) return;
  const version = ++historyVersion;
  const target = location.href;
  const linkedSearch = new URL(target).searchParams.get("search") === "1";
  // Restore the article underneath the overlay first. Closing here must not rewrite
  // the destination URL or treat the history navigation as a clicked search result.
  if (searchIsOpen.value) stopSearch();
  const route = parseShellHash(location.hash);
  if (route.view === "pageCreation" || route.view === "pageTemplates") {
    const space = spaces.value.find(item => item.id === route.spaceId);
    if (!space) home(true);
    // The former template list address now leads to its section in Nastavení prostoru.
    else if (route.view === "pageTemplates") { history.replaceState(null, "", spaceSettingsHash(space.id, "templates")); openSettings(location.hash, true); }
    else await openTemplateWorkspace(space, route.kind, true);
  }
  else if (route.view === "whatsNew") openWhatsNew(true);
  else if (route.view === "board") openBoard(route.boardId, true);
  else if (route.view === "library") openLibrary(route.tab, true, route.q);
  else if (route.view === "spaceCreation") openSpaceCreation(true);
  else if (route.view === "settings") openSettings(location.hash, true);
  else if (route.view === "profile") {
    // The bare #/profile of older links lands on its first section with a canonical address.
    const address = buildShellHash(route);
    if (location.hash !== address) history.replaceState(null, "", address);
    await openProfile(route.tab, true);
  }
  else if (route.view === "pageAccess") await openPageAccess(route.pageId, route.tab, true);
  else if (route.view === "page") {
    requestedComment.value = route.comment;
    await openPage(route.pageId, false, true, route.heading, undefined, route.block);
    // A comment link (a mention's notification) opens the dock's Diskuse, which reveals it; a phone's thread is the section under the content.
    if (route.comment && !phoneLayout.value && pageDockTabs.value) showPageDock("comments", false);
  }
  else if (route.view === "space") {
    const space = spaces.value.find(item => item.id === route.spaceId);
    if (space) await selectSpace(space, true, route.tab ?? "pages"); else { home(true); unavailableSpace.value = route.spaceId; }
  } else home(true);
  if (version === historyVersion && target === location.href && linkedSearch) {
    await nextTick(); await openSearch(true);
  } else if (version === historyVersion) landAfterNavigation();
}

watch(
  dark,
  (value) => {
    document.documentElement.style.colorScheme = value ? "dark" : "light";
  },
  { immediate: true },
);
// The right to write went away while editing: the editor stops taking input at once and the last checkpoint is tried.
watch(pageCanWrite, (allowed) => {
  if (allowed || !editorActive.value) return;
  void endEditing();
  if (dirty.value) error.value = t.toast.permissionChanged;
});
let removeScrollMemory: (() => void) | undefined;
onMounted(() => {
  void initialize();
  window.addEventListener("keydown", globalKeys, true);
  window.addEventListener(MARKDOWN_SHORTCUT_HELP_EVENT, editorAsksHelp);
  window.addEventListener(MARKDOWN_LEAVE_START_EVENT, editorLeavesStart);
  window.addEventListener("beforeunload", beforeUnload);
  window.addEventListener("pagehide", leaveCheckpoint);
  document.addEventListener("visibilitychange", hidingTab);
  window.addEventListener("pointerdown", pressElsewhere, true);
  window.addEventListener("offline", wentOffline);
  window.addEventListener("online", wentOnline);
  window.addEventListener("popstate", historyNavigation);
  removeScrollMemory = installScrollMemory();
  colorQuery.addEventListener("change", colorChanged);
  desktopQuery.addEventListener('change', sidebarViewportChanged);
  window.addEventListener('resize', viewportResized);
  window.addEventListener("focus", refreshBackupWarning);
  window.addEventListener("pointerover", pageLinkIntent, { passive: true });
  window.addEventListener("focusin", pageLinkIntent);
  window.addEventListener("touchstart", pageLinkIntent, { passive: true });
});
// The theme lives on <html>, not on the shell div: Tailwind's `dark:` variant is
// keyed to the root element, and an overlay portalled to document.body inherits
// only what is declared above it.
watchEffect(() => {
  const root = document.documentElement;
  root.dataset.mode = dark.value ? "dark" : "light";
  root.dataset.layout = appearance.layout;
  root.dataset.accent = appearance.accent;
  forceReducedMotion(root, appearance.motion === "reduce");
});

onUnmounted(() => {
  shellUnmounted = true;
  stopLanding();
  publishControlLoader.dispose();
  window.removeEventListener("keydown", globalKeys, true);
  window.removeEventListener(MARKDOWN_SHORTCUT_HELP_EVENT, editorAsksHelp);
  window.removeEventListener(MARKDOWN_LEAVE_START_EVENT, editorLeavesStart);
  window.removeEventListener("beforeunload", beforeUnload);
  window.removeEventListener("pagehide", leaveCheckpoint);
  document.removeEventListener("visibilitychange", hidingTab);
  window.removeEventListener("pointerdown", pressElsewhere, true);
  autosave.cancel();
  clearTimeout(editingRest);
  window.removeEventListener("offline", wentOffline);
  window.removeEventListener("online", wentOnline);
  window.removeEventListener("popstate", historyNavigation);
  removeScrollMemory?.();
  colorQuery.removeEventListener("change", colorChanged);
  desktopQuery.removeEventListener('change', sidebarViewportChanged);
  window.removeEventListener('resize', viewportResized);
  window.removeEventListener("focus", refreshBackupWarning);
  window.removeEventListener("pointerover", pageLinkIntent);
  window.removeEventListener("focusin", pageLinkIntent);
  window.removeEventListener("touchstart", pageLinkIntent);
  workspaceObserver?.disconnect();
  searchAbort?.abort();
  personalVersion++; personalAbort?.abort();
  clearTimeout(searchTimer);
  clearTimeout(messageTimer);
});
</script>

<template>
  <TooltipProvider :delay-duration="400">
  <div
    class="atlas"
    :data-reading-width="readingWidth"
    :data-sidebar-collapsed="sidebarCollapsed"
    :data-document-open="documentOpen || boardOpen"
    :data-editor-active="editorActive"
    :data-reading-space="readingSpaceWidth < 1184 ? 'compact' : 'spacious'"
  >
    <a class="skip-link" href="#main">{{ t.nav.skipLink }}</a>
    <PublicSharedPage v-if="publicToken" :state="publicShare" @retry="loadPublicShare" />
    <main v-else-if="!user" id="main" class="entry-shell" :data-connecting="connecting || undefined">
      <SetupWizard
        v-if="!loading && status?.databaseReady && status.requiresSetup"
        v-model:step="setupStep"
        :status="status"
        :form="setup"
        :pending="pending"
        :error="error"
        @complete="completeSetup"
      />
      <LoginLayout v-else :wiki-name="wikiName">
      <section class="entry-card" aria-labelledby="entry-title">
        <div class="entry-brand">
          <span class="brand-mark"><AtlasBrandLogo motion="static" /></span>
          {{ wikiName }}
        </div>
        <template v-if="loading"><h1 id="entry-title" class="sr-only">{{ t.nav.connectionHeading }}</h1><LoadingNote :label="t.nav.connecting" /></template>
        <template v-else-if="!status || !status.databaseReady"
          ><h1 id="entry-title">{{ t.nav.setupHeading }}</h1>
          <p>
            {{ t.nav.setupText }}
          </p>
          <p v-if="status?.error" class="notice error" role="alert">
            {{ status.error }}
          </p>
          <Button variant="default" @click="initialize">
            {{ t.nav.verifyAgain }}
          </Button>
          <AtlasVersionInfo class="entry-version" :version="status?.version" /></template
        >
        <template v-else
          ><h1 id="entry-title">{{ t.nav.signInHeading }}</h1>
          <LoginMethods :version="status.version" @signed-in="loginComplete"
        /></template>
        <Transition :css="false" v-bind="disclosureHooks"><p v-if="error" class="notice error" role="alert">{{ error }}</p></Transition>
      </section>
      </LoginLayout>
    </main>
    <main v-else-if="moduleSetup" id="main" class="module-onboarding"><ModuleSettingsHost is-wiki-admin setup @complete="finishModuleSetup" @imported="refreshImportedSpaces" /></main>
    <template v-else>
      <Transition name="sidebar-backdrop">
        <button
          v-if="sidebarOpen"
          class="sidebar-backdrop"
          :aria-label="t.nav.closeMenu"
          @click="closeMobileSidebar"
        ></button>
      </Transition>
      <!-- The width is written on the sidebar, which is what reads it, and the variable is registered as not
           inherited (refresh.css §1): written on the app root, each step of a resize restyled the whole document. -->
      <aside
        id="wiki-navigation"
        class="sidebar"
        ref="sidebar"
        :class="{ 'mobile-open': sidebarOpen }"
        :style="{ '--atlas-sidebar-width': `${effectiveSidebarWidth}px` }"
        :inert="desktopLayout ? sidebarCollapsed : !sidebarOpen"
        @keydown="sidebarKeys"
        :aria-label="t.nav.mainNavigation"
      >
        <!-- Settings and the profile take the rail over (refresh 2.5): the way back, the name of the place
             (Profil, Nastavení prostoru or Správa wiki) and its own sections, teleported here by SettingsShell. -->
        <template v-if="settingsMode">
          <div class="sidebar-top settings-sidebar-top">
            <button class="settings-back" :data-profile-back="(profileOpen && desktopLayout) || undefined" :disabled="profileOpen && (profilePending || profileBusy)" @click="leaveSettingsSurface()"><AtlasIcon name="chevron-left" :size="16" />{{ settingsBackLabel }}</button>
            <Button variant="ghost" size="icon-sm" class="sidebar-close text-muted-foreground" :aria-label="t.nav.closeMenu" @click="closeMobileSidebar"><AtlasIcon name="close" :size="18" /></Button>
          </div>
          <div class="settings-sidebar-scope">
            <span class="settings-sidebar-title">{{ settingsPlaceHeading[settingsPlace] }}</span>
          </div>
          <div v-if="settingsSpace" class="settings-switcher-slot"><SpaceSwitcher :current="settingsSpace" :spaces="switcherSpaces" @switch="switchSettingsSpace" /></div>
          <div id="atlas-settings-rail" v-scroll-edges class="settings-sidebar-rail"></div>
        </template>
        <KeepAlive><SidebarWikiNavigation v-if="!settingsMode">
        <div class="sidebar-top">
          <a class="brand" href="#/" :title="wikiName" @click="navigateLink($event, () => home())">
            <span class="brand-mark"><AtlasBrandLogo /></span
            ><span class="brand-name">{{ wikiName }}</span>
          </a>
          <Tooltip><TooltipTrigger as-child><Button variant="ghost" size="icon-sm" class="desktop-sidebar-toggle sidebar-collapse text-muted-foreground" aria-controls="wiki-navigation" :aria-label="t.cmd.collapseSidebar" :aria-expanded="!sidebarCollapsed" @click="toggleSidebar"><AtlasIcon name="chevron-left" :size="16" /></Button></TooltipTrigger><TooltipContent>{{ t.cmd.collapseSidebar }}</TooltipContent></Tooltip>
          <Button variant="ghost" size="icon-sm" class="sidebar-close text-muted-foreground" :aria-label="t.nav.closeMenu" @click="closeMobileSidebar"><AtlasIcon name="close" :size="18" /></Button>
        </div>
        <div class="sidebar-search-row">
          <button class="search-launch" @click="openSearch">
            <AtlasIcon name="search" /><span>{{ t.common.searchWiki }}</span
            ><kbd>Ctrl K</kbd>
          </button>
          <!-- With the module Boards the „+“ is Nový: Stránka or Tabule, one way of creating (catalog §2). -->
          <ActionDisclosure v-if="currentSpace && canWrite && !templateView && boardsEnabled" :label="t.nav.newInSpace(currentSpace.name)" variant="default" size="icon" trigger-class="sidebar-new-page" :disabled="pending">
            <template #trigger><AtlasIcon name="plus" :size="18" /></template>
            <ActionDisclosureItem data-new-page @click="showCreatePage"><AtlasIcon name="file-plus" />{{ t.common.newPage }}</ActionDisclosureItem>
            <ActionDisclosureItem data-new-board @click="showCreateBoard"><AtlasIcon name="layout-dashboard" />{{ t.common.newBoard }}</ActionDisclosureItem>
          </ActionDisclosure>
          <Tooltip v-else-if="currentSpace && canWrite && !templateView"><TooltipTrigger as-child><Button size="icon" class="sidebar-new-page" :aria-label="t.nav.newPageInSpaceLabel" :disabled="pending" @click="showCreatePage"><AtlasIcon name="plus" :size="18" /></Button></TooltipTrigger><TooltipContent>{{ t.nav.newPageInSpace(currentSpace.name) }}</TooltipContent></Tooltip>
        </div>
        <AssistantLauncher variant="sidebar" @launch="launchAssistant()" />
        <nav class="sidebar-primary" :aria-label="t.common.library" @keydown="listKeys">
          <button
            class="nav-link sidebar-library"
            :class="{ active: libraryHomeCurrent }"
            :aria-current="libraryHomeCurrent ? 'page' : undefined"
            @click="openLibrary('spaces')"
          >
            <AtlasIcon name="book" />{{ t.common.library }}
          </button>
          <button
            v-for="tab in sidebarLibraryTabs"
            :key="tab.id"
            class="nav-link sidebar-library-tab"
            :data-library-tab="tab.id"
            :class="{ active: libraryView === tab.id && !profileOpen }"
            :aria-current="libraryView === tab.id && !profileOpen ? 'page' : undefined"
            @click="openLibrary(tab.id)"
          >
            <AtlasIcon :name="tab.icon" />{{ tab.title }}
            <span v-if="tab.id === 'notifications' && unreadNotifications" class="sidebar-badge" data-unread-badge
              :aria-label="t.nav.unreadCount(unreadNotifications)">{{ unreadNotifications > 99 ? "99+" : unreadNotifications }}</span>
          </button>
        </nav>
        <!-- Pinned spaces, each with its own tree; the reader picks any other one in Knihovna. -->
        <nav class="sidebar-spaces" aria-labelledby="sidebar-spaces-heading">
          <div class="nav-section sidebar-spaces-heading">
            <h2 id="sidebar-spaces-heading">{{ t.nav.pinnedSpaces }}</h2>
            <Tooltip v-if="canCreateSpace"><TooltipTrigger as-child><Button variant="ghost" size="icon-sm" class="text-muted-foreground" :aria-label="t.common.newSpace"
              :aria-current="showSpaceForm ? 'page' : undefined" :disabled="pending" @click="openSpaceCreation()"
            ><AtlasIcon name="plus" :size="16" /></Button></TooltipTrigger><TooltipContent>{{ t.common.newSpace }}</TooltipContent></Tooltip>
          </div>
          <div v-scroll-edges class="spaces-scroll">
            <TransitionGroup v-if="sidebarSpaces.length" tag="ul" class="sidebar-space-list" :css="false" @enter="sidebarSpaceEnters" @leave="sidebarSpaceLeaves">
              <SidebarSpace
                v-for="space in sidebarSpaces"
                :key="space.id"
                :ref="view => { if (view) sidebarSpaceViews.set(space.id, view as unknown as { openFilter(typed?: string): void }); else sidebarSpaceViews.delete(space.id); }"
                :space="space"
                :pages="pagesBySpace[space.id]"
                :expanded="openedSpaces.has(space.id)"
                :current="spaceOverviewOpen(space)"
                :boards="boardsEnabled"
                :active-id="treeItemId"
                :collapsed-ids="collapsedPages"
                :can-write="spaceCanWrite(space)"
                :can-manage="spaceCanManage(space)"
                :busy="pending"
                :dragged-id="treeDrag?.spaceId === space.id ? treeDrag.id : undefined"
                :drop-target="rootDrop === space.id"
                :pin-pending="pinPendingIds.has(space.id)"
                :load-failed="treeFailures.has(space.id)"
                @open="selectSpace(space)"
                @toggle="toggleSpaceExpanded(space.id)"
                @intent="loadSidebarTreeAhead(space.id)"
                @pin="pinned => pinSpace(space, pinned)"
                @pin-policy="spaceMenu(space, 'settings')"
                @settings="spaceMenu(space, 'settings')"
                @templates="spaceMenu(space, 'templates')"
                @access="spaceMenu(space, 'access')"
                @trash="spaceMenu(space, 'trash')"
                @export-space="spaceMenu(space, 'export')"
                @content-health="spaceMenu(space, 'content-health')"
                @create-page="createPageInSpace(space)"
                @create-board="createBoardInSpace(space)"
                @set-collapsed="(ids, collapse) => setPagesCollapsed(space.id, ids, collapse)"
                @select="openTreeNode(space.id, $event)"
                @toggle-collapsed="togglePageCollapsed"
                @drag-start="(item, event) => startTreeDrag(space.id, item, event)"
                @drag-end="endTreeDrag"
                @drop="dropTree(space.id, $event)"
                :root-by-title="rootByTitle[space.id]"
                :drag="user?.appearance.dragPages !== false"
                @sort-root="byTitle => sortTreeByTitle(space.id, null, byTitle)"
                @drag-pages="setTreeDrag"
                @root-drag-over="rootDragOver(space.id, $event)"
                @root-drag-leave="rootDrop = undefined"
                @root-drop="dropTreeRoot(space.id, $event)"
                @insert="(destination, target) => insertTreeReference(space.id, destination, target)"
                @action="(action, node) => treeAction(space.id, action, node)"
                @rename="(node, title) => renameTreePage(space.id, node, title)"
                @search-wiki="searchWholeWiki"
                @retry="retrySidebarTree(space.id)"
              />
            </TransitionGroup>
            <p v-else-if="!spaces.length" class="sidebar-empty">
              {{
                canCreateSpace
                  ? t.nav.startFirstSpace
                  : t.nav.noSpaceAccess
              }}
            </p>
            <p v-else class="sidebar-empty">{{ t.nav.noPinnedBefore }} <a href="#/library/spaces" @click="navigateLink($event, () => openLibrary('spaces'))">{{ t.nav.libraryDative }}</a>.</p>
          </div>
        </nav>
        </SidebarWikiNavigation></KeepAlive>
        <AiQuotaStatus v-if="!settingsMode && (assistantReachable || aiCapabilities.answer || aiCapabilities.pageSummary)" :key="user.id" compact class="sidebar-quota" />
        <div v-if="!profileOpen" class="account">
          <button class="profile-launch" @click="openProfile()" :aria-current="profileOpen ? 'page' : undefined">
            <span class="avatar">{{ initialsOf(user.displayName) }}</span
            ><span>{{ user.displayName }}<small>{{ t.cmd.profileAppearance }}</small></span
            ></button>
          <Tooltip v-if="user.isWikiAdmin"><TooltipTrigger as-child><button
            class="nav-link settings-nav"
            :aria-current="administrationOpen && !profileOpen && settingsLocation?.scope === 'global' ? 'page' : undefined"
            :aria-label="backupWarning ? t.nav.wikiAdminWarnAria(backupWarning) : undefined"
            @click="openAdministration()"
          >
            <AtlasIcon name="settings" :size="17" /><span class="sr-only">{{ t.common.wikiAdmin }}</span><span v-if="backupWarning" class="nav-attention" aria-hidden="true"><AtlasIcon name="triangle-alert" :size="12" /></span>
          </button></TooltipTrigger><TooltipContent>{{ backupWarning ? t.nav.wikiAdminWarn(backupWarning) : t.common.wikiAdmin }}</TooltipContent></Tooltip>
        </div>
        <!-- Last in the rail: Tab reaches the width handle after the navigation, at the rail's right edge where it is drawn (keyboard pass). -->
        <SidebarResizer :key="user.id" :model-value="effectiveSidebarWidth" :max="sidebarMaximum" @update:model-value="sidebarWidth = $event" @commit="saveReadingPreferences" />
      </aside>
      <main id="main" ref="workspace" class="workspace" :data-space-hero-new="spaceOverview && canWrite ? '' : undefined" :style="{ '--atlas-workspace-bar-height': `${workspaceBarHeight}px` }" tabindex="-1" :inert="sidebarOpen" @keydown="publishedViewKeys">
        <header ref="workspaceBar" class="workspace-bar" :data-publication="documentOpen ? (publishedView ? 'published' : barPublication) : undefined" :aria-busy="pending || profilePending || openingPage">
          <div class="breadcrumbs">
            <!-- In the bar all the time: its room comes and goes with the rail's own slide (refresh-theme.css §4). While
                 the rail is shown it has no width and is neither visible nor focusable. -->
            <Tooltip><TooltipTrigger as-child><Button variant="ghost" size="icon-sm" class="desktop-sidebar-toggle sidebar-expand text-muted-foreground" aria-controls="wiki-navigation" :aria-label="t.cmd.showSidebar" :aria-expanded="false" @click="toggleSidebar"><AtlasIcon name="menu" /></Button></TooltipTrigger><TooltipContent>{{ t.cmd.showSidebar }}</TooltipContent></Tooltip>
            <Button variant="ghost" size="icon-sm"
              ref="mobileMenuTrigger"
              class="mobile-menu text-muted-foreground"
              :aria-label="t.cmd.showSidebar"
              aria-controls="wiki-navigation"
              :aria-expanded="sidebarOpen"
              :aria-description="unreadNotifications ? t.nav.hasUnread : undefined"
              @click="openMobileSidebar"
            >
              <AtlasIcon name="menu" /><span v-if="unreadNotifications" class="mobile-menu-unread" data-unread-dot aria-hidden="true" /></Button
            >
            <!-- The crumbs are a navigation landmark with list semantics; the
                 sidebar toggles above stay outside it, since they are controls
                 rather than trail steps. The space's action menu lives inside
                 its own crumb, which is where it visually belongs. -->
            <nav class="breadcrumb-trail" :aria-label="t.nav.breadcrumbs">
              <ol>
                <li>
                  <Tooltip><TooltipTrigger as-child><a
                    class="breadcrumb-space breadcrumb-root"
                    :class="{ 'breadcrumb-current': !currentSpace && !profileOpen && !administrationOpen && !libraryView && !showSpaceForm }"
                    :aria-current="!currentSpace && !profileOpen && !administrationOpen && !libraryView && !showSpaceForm ? 'page' : undefined"
                    href="#/"
                    @click="navigateLink($event, () => home())"
                    >{{ wikiName }}</a
                  ></TooltipTrigger><TooltipContent>{{ wikiName }}</TooltipContent></Tooltip>
                </li>
                <li v-if="profileOpen">
                  <span class="breadcrumb-separator" aria-hidden="true">/</span
                  ><span class="breadcrumb-title" aria-current="page">{{ t.common.profile }}</span>
                </li>
                <li v-else-if="administrationOpen">
                  <!-- A space's name can be longer than the bar: like every other crumb that ellipsises, it shows its full text. -->
                  <span class="breadcrumb-separator" aria-hidden="true">/</span
                  ><Tooltip v-if="settingsSpaceName"><TooltipTrigger as-child><span class="breadcrumb-title" aria-current="page">{{ t.nav.spaceSettingsOf(settingsSpaceName) }}</span></TooltipTrigger><TooltipContent>{{ t.nav.spaceSettingsOf(settingsSpaceName) }}</TooltipContent></Tooltip
                  ><span v-else class="breadcrumb-title" aria-current="page">{{ t.common.wikiAdmin }}</span>
                </li>
                <li v-else-if="showSpaceForm">
                  <span class="breadcrumb-separator" aria-hidden="true">/</span
                  ><span class="breadcrumb-title" aria-current="page">{{ t.common.newSpace }}</span>
                </li>
                <li v-else-if="libraryView">
                  <span class="breadcrumb-separator" aria-hidden="true">/</span
                  ><span class="breadcrumb-title" aria-current="page">{{ t.common.library }}</span>
                </li>
                <template v-else>
                  <li v-if="currentSpace">
                    <span class="breadcrumb-separator" aria-hidden="true">/</span
                    ><Tooltip><TooltipTrigger as-child><a
                      class="breadcrumb-space"
                      :class="{ 'breadcrumb-current': !page && !templateView && !boardId }"
                      :aria-current="!page && !templateView && !boardId ? 'page' : undefined"
                      :href="spaceLink(currentSpace.id)"
                      @click="navigateLink($event, () => selectSpace(currentSpace!))"
                      ><AtlasIcon :name="currentSpace.icon" :size="16" /><span class="breadcrumb-label">{{ currentSpace.name }}</span></a
                    ></TooltipTrigger><TooltipContent>{{ currentSpace.name }}</TooltipContent></Tooltip
                    ><ActionDisclosure
                      v-if="spaceCanManage(currentSpace)"
                      :label="t.nav.spaceActions(currentSpace.name)"
                      :trigger-title="currentSpace.name"
                      :disabled="pending"
                      ><template #trigger><AtlasIcon name="down" :size="14" /></template>
                      <!-- The same list as the sidebar's „Další akce prostoru“ (SpaceMenuItems). -->
                      <SpaceMenuItems :can-write="spaceCanWrite(currentSpace)" can-manage :boards="boardsEnabled" :empty="!(spacePages[currentSpace.id]?.length)" :content-health="currentSpace.verificationOn"
                        @create-page="createPageInSpace(currentSpace)" @create-board="createBoardInSpace(currentSpace)" @settings="spaceMenu(currentSpace, 'settings')" @pin-policy="spaceMenu(currentSpace, 'settings')"
                        @templates="spaceMenu(currentSpace, 'templates')" @access="spaceMenu(currentSpace, 'access')" @trash="spaceMenu(currentSpace, 'trash')" @export-space="spaceMenu(currentSpace, 'export')" @content-health="spaceMenu(currentSpace, 'content-health')" />
                    </ActionDisclosure>
                  </li>
                  <li v-if="templateView"><span class="breadcrumb-separator" aria-hidden="true">/</span><span class="breadcrumb-title" aria-current="page">{{ templateView === 'new-page' ? t.common.newPage : templateView === 'new-board' ? t.common.newBoard : templateView === 'new-deployment' ? t.nav.newDeployment : t.nav.newTemplate }}</span></li>
                  <!-- A deep page folds the middle levels into „…“ (refresh 2.5), and a bar without room for the path folds
                       every parent into it (refresh-theme.css §3): the menu lists all of them, so each stays reachable. -->
                  <li v-if="ancestors.length" class="breadcrumb-folded" :data-folded="ancestors.length > 2 || undefined">
                    <span class="breadcrumb-separator" aria-hidden="true">/</span
                    ><ActionDisclosure :label="t.nav.parentPages(ancestors.length)"
                      ><template #trigger><span class="breadcrumb-ellipsis" aria-hidden="true">…</span></template>
                      <ActionDisclosureItem v-for="ancestor in ancestors" :key="ancestor.id" @click="openPage(ancestor.id)"><AtlasIcon name="page" />{{ ancestor.title }}</ActionDisclosureItem>
                    </ActionDisclosure>
                  </li>
                  <li v-for="ancestor in (ancestors.length > 2 ? ancestors.slice(-1) : ancestors)" :key="ancestor.id">
                    <span class="breadcrumb-separator breadcrumb-ancestor" aria-hidden="true">/</span
                    ><Tooltip><TooltipTrigger as-child><a class="breadcrumb-space breadcrumb-ancestor" :href="pageLink(ancestor.id)" @click="navigateLink($event, () => openPage(ancestor.id))">{{ ancestor.title }}</a></TooltipTrigger><TooltipContent>{{ ancestor.title }}</TooltipContent></Tooltip>
                  </li>
                  <li v-if="page && pageAccessTab" class="breadcrumb-crumb">
                    <span class="breadcrumb-separator" aria-hidden="true">/</span
                    ><Tooltip><TooltipTrigger as-child><a class="breadcrumb-space" data-breadcrumb-page :href="pageLink(page.id)" @click="navigateLink($event, () => openPage(page!.id))">{{ page.title }}</a></TooltipTrigger><TooltipContent>{{ page.title }}</TooltipContent></Tooltip>
                  </li>
                  <li v-if="page && pageAccessTab" class="breadcrumb-crumb">
                    <span class="breadcrumb-separator" aria-hidden="true">/</span
                    ><span class="breadcrumb-title" aria-current="page">{{ t.common.access }}</span>
                  </li>
                  <li v-else-if="page" class="breadcrumb-crumb breadcrumb-title-crumb">
                    <span class="breadcrumb-separator" aria-hidden="true">/</span
                    ><Tooltip><TooltipTrigger as-child><span class="breadcrumb-title" aria-current="page">{{ page.title }}</span></TooltipTrigger><TooltipContent>{{ page.title }}</TooltipContent></Tooltip>
                  </li>
                  <li v-else-if="boardId && boardsEnabled && boardTitle" class="breadcrumb-crumb breadcrumb-title-crumb" data-breadcrumb-board>
                    <span class="breadcrumb-separator" aria-hidden="true">/</span
                    ><Tooltip><TooltipTrigger as-child><span class="breadcrumb-title" aria-current="page">{{ boardTitle }}</span></TooltipTrigger><TooltipContent>{{ boardTitle }}</TooltipContent></Tooltip>
                  </li>
                </template>
              </ol>
            </nav>
          </div>
          <div v-if="!profileOpen" class="bar-actions">
            <div v-if="page && documentValue && !pageAccessTab && !libraryTab && !administrationOpen" class="document-actions">
                <CollaboratorAvatars v-if="collaborationEnabled && browserOnline" :peers="pagePeers" @navigate="editor?.navigateToPeer($event)" />
                <!-- The page's Diskuse like a board's: the count is the comments', accented while a colleague's new one is unseen (Alt C opens it too). -->
                <span v-if="pageDockTabs" class="page-discussion-anchor"><PageDiscussionButton v-if="afterContent" ref="discussionButton" :page-id="page.id" :self-id="user?.id" :open="discussionThreadOpen" :docked="pageDockOpen" :host="pageCommentHost" @toggle="toggleDiscussion" @open="openDiscussion" /></span>
                <div class="document-secondary-actions">
                <CopyPageLink ref="copyPageLink" :page-id="page.id" />
                <!-- Icon-only like „Kopírovat odkaz“ beside them, so they say what they do the same way when pointed at or focused. -->
                <Tooltip><TooltipTrigger as-child><Button variant="ghost" size="icon-sm" class="page-access-button text-muted-foreground" :aria-label="t.common.whoHasAccess" @click="openPageAccess(page.id)"><AtlasIcon name="users" /></Button></TooltipTrigger><TooltipContent>{{ t.common.whoHasAccess }}</TooltipContent></Tooltip>
                <Tooltip><TooltipTrigger as-child><Button variant="ghost" size="icon-sm" class="favorite-button text-muted-foreground" :aria-label="personalState?.isFavorite ? t.cmd.removeFavorite : t.cmd.addFavorite" :aria-pressed="personalState?.isFavorite ?? false" :aria-busy="favoritePending" :disabled="!personalState || favoritePending" @click="toggleFavorite"><AtlasIcon name="star" /></Button></TooltipTrigger><TooltipContent>{{ personalState?.isFavorite ? t.cmd.removeFavorite : t.cmd.addFavorite }}</TooltipContent></Tooltip>
                <ActionDisclosure :label="t.nav.pageActions" :disabled="pending">
                <ActionDisclosureItem class="mobile-page-action" :disabled="favoritePending || !personalState" @click="toggleFavorite"><AtlasIcon name="star" />{{ personalState?.isFavorite ? t.cmd.removeFavorite : t.cmd.addFavorite }}</ActionDisclosureItem>
                <ActionDisclosureItem v-if="pageDockTabs" class="mobile-page-action" data-page-discussion-menu @click="toggleDiscussion"><AtlasIcon name="quote" />{{ t.common.discussion }}<span v-if="discussionButton?.badge" class="discussion-count" :data-fresh="discussionButton.unread > 0 || undefined">{{ discussionButton.badge }}</span></ActionDisclosureItem>
                <ActionDisclosureItem class="mobile-page-action" @click="openPageAccess(page.id)"><AtlasIcon name="users" />{{ t.common.whoHasAccess }}</ActionDisclosureItem>
                <ActionDisclosureItem class="mobile-page-action" @click="copyPageLink?.copy()"><AtlasIcon name="link" />{{ t.common.copyLink }}</ActionDisclosureItem>
                <ActionDisclosureItem v-if="currentSpace && canWrite" class="mobile-page-action" @click="showCreatePage"><AtlasIcon name="plus" />{{ t.common.newPage }}</ActionDisclosureItem>
                <ActionDisclosureItem v-if="currentSpace && canWrite && boardsEnabled" class="mobile-page-action" @click="showCreateBoard"><AtlasIcon name="layout-dashboard" />{{ t.common.newBoard }}</ActionDisclosureItem>
                <ActionDisclosureItem class="mobile-page-action" @click="openSearch"><AtlasIcon name="search" />{{ t.common.searchWiki }}</ActionDisclosureItem>
                <ActionDisclosureGroup label="Export">
                  <ActionDisclosureItem :disabled="pending || (dirty && !canSaveEditor)" :aria-description="t.nav.exportZipHint" :title="t.nav.exportZipHint" @click="exportContent('pages')"><AtlasIcon name="download" />{{ t.nav.exportPage }}</ActionDisclosureItem>
                  <ActionDisclosureItem :disabled="pending || (dirty && !canSaveEditor)" :aria-description="t.nav.exportPdfHint" :title="t.nav.exportPdfHint" @click="pdfDownloadOpen = true"><AtlasIcon name="scroll-text" />{{ t.nav.exportPdf }}</ActionDisclosureItem>
                  <ActionDisclosureItem v-if="pageCanWrite" :disabled="pending" @click="pdfExportOpen = true"><AtlasIcon name="clock" />{{ t.nav.autoPdf }}</ActionDisclosureItem>
                </ActionDisclosureGroup>
                <ActionDisclosureGroup :label="t.common.page">
                  <template v-if="editorActive"><ActionDisclosureItem v-for="action in toolActions" :key="action.id" :disabled="pending || !action.enabled" :aria-label="action.label" :aria-description="action.description" :title="action.description" @click="action.run()"><MarkdownIcon v-if="action.icon" :name="action.icon as MarkdownIconName" :size="16" /><span v-else class="action-icon-gap" aria-hidden="true" />{{ action.label }}</ActionDisclosureItem></template>
                  <ActionDisclosureItem @click="toggleReadingWidth"><AtlasIcon :name="readingWidth === 'wide' ? 'minimize-2' : 'maximize-2'" />{{ readingWidth === 'wide' ? t.cmd.comfortableWidth : t.cmd.wideDocument }}</ActionDisclosureItem>
                  <ActionDisclosureItem v-if="pageCanWrite" :disabled="pending" @click="openPageHistory"><AtlasIcon name="updates" />{{ t.common.history }}</ActionDisclosureItem>
                  <ActionDisclosureItem v-if="pageCanWrite" :disabled="pending" data-page-save-version :aria-description="t.nav.saveVersionHint" @click="openSaveVersion"><AtlasIcon name="check" />{{ t.common.saveVersionMore }}<span class="action-hint" aria-hidden="true">Ctrl S</span></ActionDisclosureItem>
                  <ActionDisclosureItem data-page-readership @click="readershipOpen = true"><AtlasIcon name="bar-chart-3" />{{ t.nav.readership }}</ActionDisclosureItem>
                  <ActionDisclosureItem v-if="canManageTemplates" :disabled="pending || !editorReady || (dirty && !canSaveEditor)" @click="togglePageTemplate"><AtlasIcon name="layout-template" />{{ page.isTemplate ? t.nav.removeTemplate : t.nav.useTemplate }}</ActionDisclosureItem>
                  <ActionDisclosureItem v-if="pageCanWrite" :disabled="pending" @click="organizationOpen = 'page'"><AtlasIcon name="folder-input" />{{ t.common.move }}</ActionDisclosureItem>
                </ActionDisclosureGroup>
                <!-- Page actions first (the phone's quick actions, Export, Stránka), then what goes into the page (Vložit, the editor's
                     component inserts, and the deployment) as one section; what throws work away or removes the page comes last,
                     red, behind a line. This menu is the same on a desktop; only the quick actions are phone-only. An item's name is its visible label, its
                     description is read after it (U2-7). -->
                <ActionDisclosureGroup v-for="group in actionGroups" :key="group.label" :label="group.label">
                  <ActionDisclosureItem v-for="action in group.actions" :key="action.id" :disabled="pending || !action.enabled" :aria-label="action.label" :aria-description="action.description" :title="action.description" @click="action.run()"><MarkdownIcon v-if="action.icon" :name="action.icon as MarkdownIconName" :size="16" /><span v-else class="action-icon-gap" aria-hidden="true" />{{ action.label }}</ActionDisclosureItem>
                  <ActionDisclosureItem v-if="group.label === PROJECTOR_GROUP && pageHasRequirements && boardsEnabled && deploymentBoardOffered" data-page-deployment-board :disabled="pending" :aria-description="t.nav.deploymentBoardHint" @click="openBoardOfDeployment"><AtlasIcon name="layout-dashboard" />{{ t.cmd.deploymentBoard }}</ActionDisclosureItem>
                  <template v-if="group.label === PROJECTOR_GROUP && projectorEnabled && canWrite">
                    <ActionDisclosureItem data-page-new-deployment :disabled="pending" :aria-description="t.nav.newDeploymentHint" @click="showCreateDeployment"><AtlasIcon name="list-checks" />{{ t.nav.newDeploymentMore }}</ActionDisclosureItem>
                    <ActionDisclosureItem data-page-deployment :disabled="pending" @click="askDeployment"><AtlasIcon name="file-plus" />{{ t.nav.deploymentFromPage }}</ActionDisclosureItem>
                    <ActionDisclosureItem data-page-catalog-toggle :disabled="pending || catalogPage === undefined" @click="toggleCatalogPage"><AtlasIcon name="book-open" />{{ catalogPage ? t.nav.unmarkCatalog : t.nav.markCatalog }}</ActionDisclosureItem>
                  </template>
                </ActionDisclosureGroup>
                <ActionDisclosureGroup v-if="pageCanWrite" :label="t.nav.groupEnd">
                  <ActionDisclosureItem data-page-trash destructive :disabled="pending" @click="organizationOpen = 'pageTrash'"><AtlasIcon name="trash" />{{ t.common.moveToTrashMore }}</ActionDisclosureItem>
                </ActionDisclosureGroup>
                </ActionDisclosure>
                </div>
                <div v-if="pageCanWrite" class="document-primary-actions">
                  <!-- The lock is the writer's only switch (3.6-F): the page saves itself and attaches its editor on an intent to write. -->
                  <EditLockToggle :locked="pageLocked" :pulse="lockPulse" :shortcut="t.nav.shortcutNotInText" @toggle="toggleLock" />
                  <!-- Saved changes nobody published yet: readers, the search and the assistant do not see them. -->
                  <PagePublicationState v-if="barPublication === 'draft' || barPublication === 'live'" :state="barPublication" :published="page.publishedVersion != null" :published-at="publication?.publishedAt" :automatic="draftPublishesItself"
                    :can-view-published="canViewPublished" :viewing-published="publishedView" @update:viewing-published="$event ? (publishedView = true) : leavePublishedView()" />
                  <component :is="PublishControl" v-if="unpublishedChanges && !publishedView" ref="publishControl" :key="page.id" :label="t.nav.publishChanges" :quiet="pageLocked" :description="draftText.publishHint" :publishing="publishing" status-selector=".document-status:not(:has([data-confirmed]))" :disabled="publishDisabledOf" :pending="pending" :prepare="preparePublication" :check="publicationChecks" :checks="publishAssistant.findings.value" :suggest-summary="aiCapabilities.changeSummary ? publishAssistant.suggestSummary : undefined" :summary-context-key="publicationContextKeyOf" :publish="publish" :can-discard="canDiscard" @opened="discardSoon" @discard="openDiscard" @reveal="editor?.reveal($event)" />
                </div>
                <Tooltip v-if="pageDockTabs"><TooltipTrigger as-child><Button variant="ghost" size="icon-sm" class="page-dock-toggle text-muted-foreground" :aria-label="t.common.sidePanel" :aria-pressed="pageDockOpen" @click="togglePageDock"><AtlasIcon name="panel-right-close" /></Button></TooltipTrigger><TooltipContent>{{ t.common.sidePanel }}</TooltipContent></Tooltip>
            </div>
            <!-- A board's actions where a page has its own (interaction spec §6): who else is here, the link, the star and the menu. -->
            <div v-else-if="boardOpen && !libraryTab && !administrationOpen" class="document-actions" data-board-actions>
                <CollaboratorAvatars v-if="browserOnline" :peers="boardPeers" @navigate="boardView?.goToPeer($event)" />
                <!-- The board's Diskuse as a page's: the same button, count and peek, toggling the dock's Diskuse (Alt C too). -->
                <span class="page-discussion-anchor"><PageDiscussionButton v-if="boardView" ref="discussionButton" :page-id="boardId!" board :self-id="user?.id" :open="boardView.discussionOpen" :docked="workspacePanel.open.value" :host="boardView.host" @toggle="toggleDiscussion" @open="toggleDiscussion" /></span>
                <div class="document-secondary-actions">
                <CopyPageLink ref="copyBoardLink" :href="boardLink(boardId!)" :label="t.cmd.copyBoardLink" />
                <Tooltip><TooltipTrigger as-child><Button variant="ghost" size="icon-sm" class="favorite-button text-muted-foreground" :aria-label="personalState?.isFavorite ? t.cmd.removeFavorite : t.cmd.addFavorite" :aria-pressed="personalState?.isFavorite ?? false" :aria-busy="favoritePending" :disabled="!personalState || favoritePending" @click="toggleFavorite"><AtlasIcon name="star" /></Button></TooltipTrigger><TooltipContent>{{ personalState?.isFavorite ? t.cmd.removeFavorite : t.cmd.addFavorite }}</TooltipContent></Tooltip>
                <!-- A reader's board menu holds only the phone's quick actions: on a wider screen it would open empty, so it is not there. -->
                <ActionDisclosure :label="t.nav.boardActions" :disabled="pending" :trigger-class="boardFrame?.canEdit ? undefined : 'board-menu-phone-only'">
                <ActionDisclosureItem class="mobile-page-action" @click="openSearch"><AtlasIcon name="search" />{{ t.common.searchWiki }}</ActionDisclosureItem>
                <ActionDisclosureItem class="mobile-page-action" :disabled="favoritePending || !personalState" @click="toggleFavorite"><AtlasIcon name="star" />{{ personalState?.isFavorite ? t.cmd.removeFavorite : t.cmd.addFavorite }}</ActionDisclosureItem>
                <ActionDisclosureItem class="mobile-page-action" @click="copyBoardLink?.copy()"><AtlasIcon name="link" />{{ t.common.copyLink }}</ActionDisclosureItem>
                <ActionDisclosureItem class="mobile-page-action" data-page-discussion-menu @click="toggleDiscussion"><AtlasIcon name="quote" />{{ t.common.discussion }}<span v-if="discussionButton?.badge" class="discussion-count" :data-fresh="discussionButton.unread > 0 || undefined">{{ discussionButton.badge }}</span></ActionDisclosureItem>
                <!-- Where a page has Uspořádat: a board is placed in the tree the same way (its versions come later, spec §6 P2; it has no access of its own). -->
                <ActionDisclosureItem v-if="boardFrame?.canEdit" data-board-move :disabled="pending" @click="organizationOpen = 'board'"><AtlasIcon name="organize" />{{ t.common.move }}</ActionDisclosureItem>
                <ActionDisclosureItem v-if="boardFrame?.canEdit" data-board-trash :disabled="pending" @click="boardTrashOpen = true; boardTrashError = ''"><AtlasIcon name="trash" />{{ t.common.moveToTrashMore }}</ActionDisclosureItem>
                </ActionDisclosure>
                </div>
                <!-- The board's lock where a page has its own (F21): the board keeps its state and its E, the top bar shows the switch. -->
                <div v-if="boardFrame?.canEdit && boardView" class="document-primary-actions">
                  <EditLockToggle :locked="boardView.locked" :pulse="boardView.lockPulse" :shortcut="t.nav.shortcutNotInText" @toggle="boardView.toggleLock()" />
                </div>
                <Tooltip><TooltipTrigger as-child><Button variant="ghost" size="icon-sm" class="page-dock-toggle text-muted-foreground" :aria-label="t.common.sidePanel" :aria-pressed="workspacePanel.open.value" @click="togglePageDock"><AtlasIcon name="panel-right-close" /></Button></TooltipTrigger><TooltipContent>{{ t.common.sidePanel }}</TooltipContent></Tooltip>
            </div>
            <Button variant="ghost" size="icon-sm"
              class="mobile-menu text-muted-foreground"
              :aria-label="t.common.search"
              @click="openSearch"
            >
              <AtlasIcon name="search" /></Button
            ><ActionDisclosure v-if="currentSpace && canWrite && !templateView && !administrationOpen && boardsEnabled" :label="t.nav.newInSpaceLabel" :trigger-title="t.nav.newPageOrBoard" variant="ghost" size="sm" trigger-class="new-page-button font-normal" :disabled="pending">
              <template #trigger><AtlasIcon name="file-plus" :size="16" /><span class="new-page-label-full">{{ t.nav.new }}</span><span class="new-page-label-short">{{ t.nav.new }}</span></template>
              <ActionDisclosureItem data-new-page @click="showCreatePage"><AtlasIcon name="file-plus" />{{ t.common.page }}</ActionDisclosureItem>
              <ActionDisclosureItem data-new-board @click="showCreateBoard"><AtlasIcon name="layout-dashboard" />{{ t.common.board }}</ActionDisclosureItem>
            </ActionDisclosure
            ><Button variant="ghost" size="sm"
 v-else-if="currentSpace && canWrite && !templateView && !administrationOpen"
 class="new-page-button text-muted-foreground font-normal"
 :aria-label="t.common.newPage"
 :disabled="pending"
 @click="showCreatePage">
              <AtlasIcon name="file-plus" :size="16" /><span class="new-page-label-full">{{ t.common.newPage }}</span><span class="new-page-label-short">{{ t.nav.newShort }}</span>
            </Button>
          </div>
        </header>
        <div v-if="error && (!page || pageAccessTab || profileOpen || administrationOpen)" class="notice error global-notice" role="alert">
          {{ error
          }}<button v-if="dirty" @click="downloadDraft">
            {{ t.nav.downloadUnsaved }}
          </button>
        </div>
        <!-- The workspace row under the top bar: the view, and beside it the one side panel (boards 03, 18). -->
        <div class="workspace-body">
        <ProfilePage v-if="profileOpen" :key="user.id" :tab="profileTab" :appearance="appearance"
          :username="user.username" :user-id="user.id" :is-wiki-admin="user.isWikiAdmin" :version="status?.version"
          :appearance-dirty="appearanceDirty" :appearance-pending="profilePending" :appearance-error="profileError" :back-label="settingsBackLabel"
          :reading-width="readingWidth" @preview-reading-width="readingWidth = $event"
          @navigate="openProfile($event)" @back="cancelProfile" @preview="Object.assign(appearance, $event)"
          @save-appearance="saveProfile" @cancel-appearance="cancelAppearance"
          @dirty="profileDirty = $event" @busy="profileBusy = $event"
          @ai-preferences-saved="searchAnswer.refreshPreference(); assistant.refreshPreference()" @open-conversations="showMyConversations" @account-saved="user.displayName = $event" @session-changed="refreshSession" @access-changed="refreshProfileAccess" @select-page="openContent" @logout="logout" />
        <KeyboardHelpDialog v-if="lazyDialogs.help" :open="keyboardHelpOpen" :ai="assistantReachable" @close="keyboardHelpOpen = false" />
        <UnsavedChangesDialog :open="!!leaveRequest" :can-save="false" @stay="leaveRequest = null" @discard="discardAndLeave"
          @closed="afterLeaveQuestion?.(); afterLeaveQuestion = undefined" />
        <UnsavedChangesDialog :open="!!profileLeaveGuard" :saving="profileLeaveSaving" :can-save="appearanceDirty && !profileDirty"
          @stay="stayInProfile" @discard="discardProfileAndLeave" @save="saveProfileLeaveGuard" />
        <div v-show="!profileOpen" ref="readingContent" class="workspace-content">
        <WhatsNewPage v-if="whatsNewOpen" :key="`whats-new-${whatsNewSince ?? ''}`" :current="releaseOf(status?.version)" :since="whatsNewSince"
          :dark="dark" :accent="appearance.accent" @close="home()" />
        <BoardView v-else-if="boardId && boardsEnabled" ref="boardView" :key="`board:${user.id}:${boardId}`" :board-id="boardId" :open-locked="touchScreen || !!user.appearance.openLocked" @meta="boardMeta" @peers="boardPeers = $event" @open-page="(id: string, heading?: string, block?: string) => openPage(id, false, false, heading, undefined, block)" @open-object="(id: string, typeId: string) => typeId === 'atlas.board' && openBoard(id)" @session-expired="refreshSession" />
        <CreateSpaceForm v-else-if="showSpaceForm && canCreateSpace" key="new-space" :disabled="pending" @created="spaceCreated" @cancel="cancelSpaceCreation" @busy="pending = $event" @dirty="templateDirty = $event" />
        <PageCreationFlow v-else-if="templateView && currentSpace && (templateView !== 'new-board' || boardsEnabled) && (templateView !== 'new-deployment' || projectorEnabled)" :key="`create:${user.id}:${currentSpace.id}:${templateView}`"
          :space="currentSpace" :pages="spacePages[currentSpace.id] ?? []" :initial-parent-id="initialPageParent" :initial-title="initialPageTitle"
          :as-template="templateView === 'new-template'" :as-board="templateView === 'new-board'" :projector="projectorEnabled && templateView !== 'new-template'" :deployment="templateView === 'new-deployment'"
          :dark="dark" :accent="appearance.accent" @created="pageCreatedFromTemplate" @created-board="boardCreated" @cancel="cancelTemplateWorkspace"
          @dirty="templateDirty = $event" @busy="templateBusy = $event" />
        <SettingsHost
          v-else-if="administrationOpen && settingsLocation"
          :key="settingsLocation.scope === 'space' ? `space:${user.id}:${settingsLocation.spaceId}` : `global:${user.id}`"
          :scope="settingsLocation.scope"
          :space-id="settingsLocation.scope === 'space' ? settingsLocation.spaceId : undefined"
          :user-id="user.id"
          :is-wiki-admin="user.isWikiAdmin"
          :spaces="spaces"
          :backup-warning="backupWarning"
          :version="status?.version"
          :mcp-enabled="user.features?.mcp === true"
          @dirty="settingsDirty = $event"
          @imported="refreshImportedSpaces"
          @warning="backupWarningReported($event)"
          @open-page="openPageFromSettings"
          @new-template="openSpaceNewTemplate"
          @new-page="openSpaceNewPage"
          @space-changed="spaceMetadataChanged"
          @space-trashed="spaceTrashed"
          @spaces-changed="refreshImportedSpaces"
          @access-changed="refreshAccess"
          @wiki-settings="openAdministration"
          @open-trash="spaceId => { const space = spaces.find(item => item.id === spaceId); if (space) void spaceMenu(space, 'trash'); }"
        />
        <!-- Domů at `#/` (4.0): what changed, the last visits and what waits for the reader. -->
        <HomeDashboard v-else-if="homeOpen" @select="openContent" @navigate="openLibrary($event)" @history="openChangeInHistory" />
        <!-- Knihovna: the personal lists and the overview of all spaces. -->
        <LibraryPage v-else-if="libraryView" :view="libraryView" :space-count="spaces.length" :can-create="canCreateSpace && !pending" @navigate="openLibrary($event)" @create="openSpaceCreation()">
          <LibraryContent :view="libraryView" :query="libraryQuery" :user-id="user.id" :spaces="spaces" :can-create="canCreateSpace && !pending" :can-manage="spaces.some(spaceCanManage)" :pending-ids="pinPendingIds" :limit="SPACE_LIST_LIMIT"
            :refresh-key="libraryRefresh" :personal-refresh-key="personalRefresh" :notification-signal="notificationSignal" :spaces-loaded="spacesLoaded"
            @open="selectSpace($event)" @pin="(space, pinned) => pinSpace(space, pinned)" @create="openSpaceCreation()" @navigate="openLibrary($event)" @select="openContent" @unread="unreadNotifications = $event" />
        </LibraryPage>
        <!-- A page on its way (3.6-G N4): its title from the tree at once, the body's skeleton after the defer token (LoadingSkeleton). -->
        <template v-else-if="arriving"
          ><div class="document-header" data-page-arrival><h1 class="document-title">{{ arriving.title }}</h1><div class="document-meta" :data-writable="canWrite || undefined" aria-hidden="true" /></div>
          <section class="document-body" aria-busy="true"><LoadingSkeleton :rows="6" :label="t.nav.loadingPage" /></section></template>
        <PageSharingView v-else-if="page && pageAccessTab" :key="`access:${user.id}:${page.id}`" :page-id="page.id" :page-title="page.title" :tab="pageAccessTab" />
        <template v-else-if="page && documentValue"
          ><PageUndoWedge v-if="pageCanWrite" :editable="editable" :can-undo="pageCanUndo" @undo="editor?.undo()" /><div class="document-header">
            <DocumentTitle
              v-model="titleDraft"
              :decoration="page.decoration"
              :readonly="!editable || publishedView"
              maxlength="200"
              :disabled="!browserOnline"
              :aria-invalid="!!titleError || undefined"
              aria-describedby="document-title-error"
              @continue="editor?.focus('visible')"
              @blur="commitTitle"
              @keydown.esc.stop="titleDraft = title"
            />
            <InlineReveal :open="!!titleError"><p id="document-title-error" class="document-title-error" role="alert">{{ titleError }}</p></InlineReveal>
            <div class="document-meta" :data-writable="(pageCanWrite && browserOnline) || undefined">
              <AssistantLauncher variant="page" :page-reading="assistantPageReading" :can-ask="canAsk" @launch="launchAssistant(true)" />
              <StatusBadge v-if="catalogPage" tone="info" icon="book-open" size="sm" :label="t.nav.catalogBadge" data-page-catalog />
              <PageVerification v-if="verificationShown(verification)" ref="verificationChip" :key="page.id" v-model:open="verificationOpen" :page-id="page.id" :verification="verification" :can-edit="pageCanWrite" :online="browserOnline" :is-published="page.isPublished" @change="value => { verification = value; }" @open-history="openPageHistory" />
              <Button v-if="page.isTemplate && canManageTemplates" variant="ghost" size="sm" @click="openSpaceTemplates(currentSpace!.id)">{{ t.nav.templateManage }}</Button>
              <div class="document-status" :data-tone="documentStatusTone" :data-state="documentStatusState" :data-transient="documentStatusTransient || undefined" role="status" aria-live="polite" aria-atomic="true">
                <StatusBadge v-if="documentStatusTransient" :key="documentStatusText" tone="success" :label="documentStatusText" confirmed />
                <span v-else :key="documentStatusText" class="document-status-text">{{ documentStatusText }}</span>
                <Button variant="outline" v-if="error && dirty" @click="downloadDraft">{{ t.nav.downloadUnsaved }}</Button>
              </div>
              <PagePdfExportStatus v-if="pageCanWrite && afterContent" :key="`pdf:${user.id}:${page.id}`" :page-id="page.id" :version="page.version" :dirty="dirty" :settings-open="pdfExportOpen" @download="pdfDownloadOpen = true" />
              <Tooltip v-if="lastChangedAt && pageCanWrite"><TooltipTrigger as-child><button type="button" class="document-updated history-entry" :disabled="pending" :aria-label="t.nav.showHistory" @click="openPageHistory"><AtlasIcon name="updates" :size="13" /><time :datetime="lastChangedAt">{{ t.nav.editedAt(formatDateTime(lastChangedAt)) }}</time></button></TooltipTrigger><TooltipContent>{{ t.nav.showHistoryShort }}</TooltipContent></Tooltip>
              <time v-else-if="lastChangedAt" class="document-updated" :datetime="lastChangedAt">{{ t.nav.editedAt(formatDateTime(lastChangedAt)) }}</time>
              <PageIconPicker :key="`icon:${user.id}:${page.id}`" :page-id="page.id" :user-id="user.id" :can-edit="editable && browserOnline" :decoration="page.decoration" :title="title" @saved="pageDecorationSaved" />
              <PageTags :key="`${user.id}:${page.id}`" :page-id="page.id" :user-id="user.id" :page-version="page.version" :initial="page.bundle?.tags?.tags" :can-edit="editable && browserOnline" :can-write="pageCanWrite" @select-tag="openTagSearch" />
              <!-- A writer's row keeps the save state's slot from the first frame: „Ukládám…“ arriving never wraps the row (F1, CLS). -->
              <span v-if="pageCanWrite || saveState" class="document-save-state"><SaveState v-if="saveState" :state="saveState" :error="checkpointError" data-page-save-state /></span>
            </div>
            <!-- Without the dock (closed, narrow, phone) the page outline is one quiet row above the text (board 13). It steps
                 aside while the dock is open, whatever its tab, so switching tabs never moves the article. -->
            <PageOutline :key="`outline:${page.id}`" class="document-outline" :root="documentBody" variant="disclosure" :minimum="3" :collapsed="pageDockOpen" :page-id="page.id" />
          </div>
          <p v-if="personalError" class="personal-error notice error" role="alert">{{ t.nav.favoritesError(personalError) }} <button v-if="!personalState" @click="loadPersonalState(page.id)">{{ t.common.retry }}</button></p>
          <p v-if="pageGone === page.id" class="notice" role="status" data-page-gone>{{ t.nav.pageGone }}
            <button v-if="currentSpace && spaceCanManage(currentSpace)" type="button" @click="organizationOpen = 'trash'">{{ t.nav.restoreFromTrash }}</button></p>
          <p v-else-if="writeRevoked?.pageId === page.id" class="notice" role="status" data-page-write-revoked>{{ t.nav.writeRevoked }}
            <template v-if="writeRevoked.unsaved">{{ t.nav.writeRevokedUnsaved }}
              <button type="button" @click="copyRevokedText">{{ writeRevoked.copied ? t.nav.copyAgain : t.nav.copyMyText }}</button></template></p>
          <section v-if="publishedView" class="document-body document-published" :aria-label="t.nav.publishedReadOnly" :aria-busy="!publishedRevision && !publishedRevisionError" data-published-view>
            <p v-if="publishedRevisionError" class="notice error" role="alert">{{ publishedRevisionError }}</p>
            <LoadingNote v-else-if="!publishedRevision" :label="t.nav.loadingPublished" />
            <HistoryRevisionPreview v-else :key="publishedRevision.id" :content-json="publishedRevision.contentJson" :page-id="page.id" :dark="dark" :accent="appearance.accent" />
          </section>
          <section v-show="!publishedView" ref="documentBody" class="document-body" :aria-label="t.common.document" :aria-readonly="!editable || undefined"
            @keydown="documentKeys" @pointerdown="stillEditing" @focusin="caretEntered"
            @focusout="documentFocusOut">
            <PageSummaryAnswer v-if="page.isPublished" :page="{ id: page.id, version: page.publishedVersion ?? page.version }" :enabled="aiCapabilities.pageSummary" :can-continue="canAsk" @open-source="assistant.openSource" @continue="continuePageSummary" />
            <DocumentEditor
              :key="`${page.id}:${editorMountVersion}`"
              ref="editor"
              :value="documentValue"
              :readonly="!editable"
              :dark="dark"
              :accent="appearance.accent"
              :space-id="page.spaceId"
              :page-id="page.id"
              :catalog="!!page.isCatalog"
              @update:catalog="setCatalog"
              :document-title="title"
              :unsaved="() => dirty"
              :identity="{ id: user.id, displayName: user.displayName }"
              :collaboration-allowed="pageCanWrite"
              :collaboration="page.bundle?.collaboration"
              :live-reading="!!page.live"
              :skeleton-shown="arrivalSkeletonShown"
              :arrived="arrivalQuiet"
              :can-edit="editable"
              :deployment-board="boardsEnabled && deploymentBoardOffered"
              @edit-intent="editIntent"
              @collaboration-status="updateCollaborationStatus"
              @collaboration-peers="collaborators = $event"
              @plugin-actions="editorActions = $event"
              @change="documentChanged"
              @ai-assisted-applied="pendingAiAssisted = true; aiAppliedEpoch++"
              @detail-draft="detailDirty = $event"
              :can-edit-source="canEditSource"
              :save-source="savePageSource"
              :pages="spacePages[page.spaceId]"
              :assist="editorAssist" :assist-expected="editorAssistExpected"
              :readonly-selection-enabled="!editorActive"
              @readonly-selection="readonlySelection = $event"
              @find-selection="findSelection"
              @source-draft="sourceDirty = $event"
              @request-source="openPageSource"
              @import-title="importTitle"
              @ready="editorBecameReady"
              @reopened="pageReopened"
              @can-undo="pageCanUndo = $event"
            /></section>
          <PageConnections v-if="editorReady" layout="graph" :page-title="page.title" :document="documentValue" :page-id="page.id" :space-id="page.spaceId" :refresh-key="libraryRefresh" :related-enabled="aiCapabilities.related" />
          <!-- A phone's discussion (no dock there): the same thread as the dock's Diskuse. -->
          <CommentThread v-if="editorReady && afterContent && phoneLayout" ref="phoneDiscussion" :key="`comments:${page.id}`" class="page-comments" :object-id="page.id" :view-key="`page:${page.id}`" :title="t.common.discussion"
            :host="pageCommentHost" @count="discussionButton?.set($event)" @anchor="revealCitedHeading($event)" />
          <PageMobileBar v-if="editorReady" :can-ask="canAsk" :lockable="pageCanWrite" :locked="pageLocked" :favorite="personalState?.isFavorite ?? false" :favorite-busy="favoritePending || !personalState"
            :discussion="pageDockTabs ? discussionButton?.count ?? 0 : null" :unread="discussionButton?.unread ?? 0" @read="readFromTop" @ask="launchAssistant(true)" @favorite="toggleFavorite" @lock="toggleLock" @discussion="revealPageComments" />
        </template>
        <section v-else-if="unavailablePage || unavailableSpace" class="overview page-unavailable" data-page-unavailable>
          <EmptyState v-if="unavailableSpace" data-focus-landing icon="lock" :title="t.nav.spaceUnavailableTitle" :text="t.nav.spaceUnavailableText">
            <Button variant="outline" @click="goBack"><AtlasIcon name="back" />{{ t.common.back }}</Button>
            <Button variant="default" @click="openLibrary('spaces')"><AtlasIcon name="book" />{{ t.common.library }}</Button>
          </EmptyState>
          <EmptyState v-else data-focus-landing icon="lock" :title="t.nav.pageUnavailableTitle" :text="t.nav.pageUnavailableText">
            <Button variant="outline" @click="goBack"><AtlasIcon name="back" />{{ t.common.back }}</Button>
            <Button variant="default" @click="openLibrary('spaces')"><AtlasIcon name="book" />{{ t.common.library }}</Button>
          </EmptyState>
        </section>
        <section v-else-if="currentSpace" ref="spaceOverview" class="overview space-overview">
          <!-- Content first: the space's head with its actions (refresh 2.5), then straight to the pages. -->
          <SpaceHeader :space="currentSpace" :pages="spacePages[currentSpace.id] ?? []" :pages-loaded="!!pagesBySpace[currentSpace.id]" :can-manage="spaceCanManage(currentSpace)" :can-write="canWrite" :busy="pending" :boards="boardsEnabled"
            @settings="openSettings(spaceSettingsHash(currentSpace!.id))" @new-page="showCreatePage" @new-board="showCreateBoard" />
          <!-- Obsah (once Stránky) and O prostoru are for everyone who can open the space; Ke schválení has no address of its own. -->
          <div ref="spaceTabsRow" v-scroll-edges:x class="space-tabs" role="tablist" :aria-label="t.nav.spaceViews" @keydown="tablistKeys">
            <button type="button" role="tab" data-space-tab="pages" :aria-selected="spaceView === 'pages'" :tabindex="spaceView === 'pages' ? 0 : -1" :class="{ active: spaceView === 'pages' }" @click="showSpaceTab('pages')">{{ t.common.contents }}<!-- Only Obsah's own count: the tree's number first jumped to it once the summary came (F3, 175 → 294). --><span v-if="contentCounts[currentSpace.id] !== undefined" class="space-tab-count">{{ formatNumber(contentCounts[currentSpace.id]!) }}</span></button>
            <button type="button" role="tab" data-space-tab="contacts" :aria-selected="spaceView === 'contacts'" :tabindex="spaceView === 'contacts' ? 0 : -1" :class="{ active: spaceView === 'contacts' }" @click="showSpaceTab('contacts')">{{ t.cmd.contacts }}</button>
            <button v-if="unlinkedCounts[currentSpace.id] || spaceView === 'unlinked'" type="button" role="tab" data-space-tab="unlinked" :aria-selected="spaceView === 'unlinked'" :tabindex="spaceView === 'unlinked' ? 0 : -1" :class="{ active: spaceView === 'unlinked' }" @click="showSpaceTab('unlinked')">{{ t.nav.unlinkedMentions }}<span v-if="unlinkedCounts[currentSpace.id]" class="space-tab-count">{{ unlinkedCounts[currentSpace.id] }}</span></button>
            <button type="button" role="tab" data-space-tab="about" :aria-selected="spaceView === 'about'" :tabindex="spaceView === 'about' ? 0 : -1" :class="{ active: spaceView === 'about' }" @click="showSpaceTab('about')">{{ t.nav.aboutSpace }}</button>
            <button v-if="user.isWikiAdmin" type="button" role="tab" data-space-approval-tab :aria-selected="spaceView === 'approval'" :tabindex="spaceView === 'approval' ? 0 : -1" :class="{ active: spaceView === 'approval' }" @click="showSpaceTab('approval')">{{ t.nav.awaitingApproval }}</button>
            <template v-if="projectorEnabled">
              <button type="button" role="tab" data-space-tab="deployments" :aria-selected="spaceView === 'deployments'" :tabindex="spaceView === 'deployments' ? 0 : -1" :class="{ active: spaceView === 'deployments' }" @click="showSpaceTab('deployments')">{{ t.common.deploymentOverview }}</button>
              <button type="button" role="tab" data-space-tab="waiting" :aria-selected="spaceView === 'waiting'" :tabindex="spaceView === 'waiting' ? 0 : -1" :class="{ active: spaceView === 'waiting' }" @click="showSpaceTab('waiting')">{{ t.common.whatWeWaitFor }}</button>
            </template>
            <span class="library-tabs-indicator" :class="{ 'is-ready': spaceTabIndicator.ready }" :style="{ width: `${spaceTabIndicator.width}px`, transform: `translateX(${spaceTabIndicator.left}px)` }" aria-hidden="true" />
          </div>
          <!-- The views switch like the parts of Knihovna (LibraryPage.vue): the underline slides, the views
               cross-fade in one grid cell and the cell eases its height (stageHeight.ts); reduced motion only fades. -->
          <div ref="spaceStage" class="library-stage">
            <Transition name="library-part" @before-leave="retireSpaceView" @enter="spaceStageHeight.onEnter">
              <div :key="spaceView" class="library-part space-view">
          <SpaceDraftApproval v-if="spaceView === 'approval'" :key="currentSpace.id"
            :space-id="currentSpace.id" :dark="dark" :accent="appearance.accent"
            @published="refreshPublishedSpace($event)" />
          <SpaceAbout v-else-if="spaceView === 'about'" :space="currentSpace" :pages="spacePages[currentSpace.id] ?? []" :can-manage="spaceCanManage(currentSpace)"
            :templates="spaceTemplates" :templates-loading="spaceTemplatesLoading" :templates-error="spaceTemplatesError"
            @open-page="openPage($event)" @retry-templates="reloadSpaceTemplates" />
          <ContactOverview v-else-if="spaceView === 'contacts'" :key="`contacts:${currentSpace.id}`" :space-id="currentSpace.id" />
          <DeploymentOverview v-else-if="spaceView === 'deployments'" :space-id="currentSpace.id" />
          <WaitingList v-else-if="spaceView === 'waiting'" :space-id="currentSpace.id" />
          <UnlinkedMentions v-else-if="spaceView === 'unlinked'" :key="`unlinked:${currentSpace.id}`" :space-id="currentSpace.id"
            @open="openPage($event)" @counted="(count, more) => countUnlinked(currentSpace!.id, count, more)" />
          <SpacePageList v-else-if="pagesBySpace[currentSpace.id]?.length" :key="`${currentSpace.id}:${spaceTab === 'boards' && boardsEnabled ? 'boards' : ''}`"
            :space-id="currentSpace.id" :pages="boardsEnabled ? spaceContents[currentSpace.id] ?? [] : spacePages[currentSpace.id] ?? []" :can-write="canWrite" :initial-type="spaceTab === 'boards' && boardsEnabled ? 'board' : ''"
            @open="(id, type) => openContent(id, type, currentSpace?.id)" @published="refreshPublishedSpace(currentSpace.id)" @refresh="refreshPublishedSpace(currentSpace.id)" @counted="contentCounts[currentSpace.id] = $event" />
          <!-- Only a tree that has arrived can be empty: while it is on its way the space must not claim to have no pages. -->
          <EmptyState v-else-if="pagesBySpace[currentSpace.id]" class="space-empty" icon="book" :title="t.nav.spaceEmptyTitle" :text="canWrite ? (boardsEnabled ? t.nav.emptyWriteBoards : t.nav.emptyWrite) : t.nav.emptyRead">
            <Button variant="default" v-if="canWrite" @click="showCreatePage">
              <AtlasIcon name="plus" />{{ t.nav.createFirstPage }}
            </Button>
            <Button variant="outline" v-if="canWrite && boardsEnabled" data-space-empty-board @click="showCreateBoard">
              <AtlasIcon name="layout-dashboard" />{{ t.nav.createBoard }}
            </Button>
          </EmptyState>
          <!-- The tree is on its way: a list-sized placeholder instead of an empty block under the tabs. -->
          <LoadingSkeleton v-else-if="!treeFailures.has(currentSpace.id)" class="space-contents-loading" :rows="5" row-height="52px" :label="t.nav.loadingContents" />
              </div>
            </Transition>
          </div>
        </section>
        </div>
        <!-- One panel: the assistant anywhere, and on a page the dock whose tabs switch between the page tools and the assistant. -->
        <WorkspaceSidePanel v-if="workspacePanel.content.value" ref="workspaceSidePanel" v-model:open="workspacePanel.open.value" :label="workspacePanel.label.value"
          :storage-key="pageDockTabs ? `atlas:reading:${user.id}:dock-width` : `atlas:reading:${user.id}:panel-width`"
          :tabs="pageDockTabs ?? workspacePanel.tabs.value" :active-tab="pageDockTabs ? pageDockActive : workspacePanel.activeTab.value"
          :min-width="pageDockTabs ? (pageDockTabs.length > 4 ? 392 : pageDockTabs.length > 3 ? 340 : 300) : 360" :default-width="pageDockTabs ? (pageDockTabs.length > 4 ? 392 : 340) : 400"
          :auto-focus="workspacePanel.autoFocus.value"
          @update:active-tab="pageDockTabs ? selectPageDockTab($event) : (workspacePanel.activeTab.value = $event)" @close="pageDockClosed"
          ><template v-if="workspacePanel.header.value && !pageDockShown" #header><component :is="workspacePanel.header.value" /></template
          ><PageSidePanel v-if="pageDockShown && page" :tab="pageDockTab" :page-id="page.id" :space-id="page.spaceId" :page-title="page.title"
            :document-root="documentBody" :document="documentValue" :version="shownVersion" :is-published="page.isPublished" :published-version="nothingToPublish && page.publishedVersion !== null ? shownVersion : page.publishedVersion"
            :updated-at="page.activeFrom" :host="pageCommentHost" @comments="discussionButton?.set($event)" @anchor="revealCitedHeading($event)" @publication="value => { publication = value; }" :verification="verification" :readers="pageReaders" :can-edit="pageCanWrite && browserOnline" @verification="value => { verification = value; }" @open-readership="readershipOpen = true" @open-verification="verificationChip?.openSteward()" :related-enabled="aiCapabilities.related" :refresh-key="libraryRefresh"
            @open-history="openPageHistory" @open-access="openPageAccess(page.id)" @open-place="(id, typeId) => openContent(id, typeId === 'atlas.board' ? 'board' : 'page')"
          /><component :is="workspacePanel.content.value" v-else-if="!pageDockShown" v-bind="workspacePanel.contentProps.value" /></WorkspaceSidePanel>
        </div>
      </main>
    </template>
    <PageOrganizationPanel
      v-if="organizationOpen && currentSpace && user"
      :space-id="currentSpace.id"
      :space-role="currentSpace.role"
      :is-wiki-admin="user.isWikiAdmin"
      :page="organizationOpen === 'page' || organizationOpen === 'pageTrash' ? page : undefined"
      :start-with-trash="organizationOpen === 'pageTrash'"
      :board="organizationOpen === 'board' && boardId && boardFrame ? { id: boardId, title: boardFrame.title } : undefined"
      :before-mutation="beforeOrganizationMutation"
      :spaces="spacesPinnedFirst"
      @close="organizationOpen = undefined"
      @changed="organizationChanged"
      @renamed="organizationRenamed"
    />
    <ConfirmDialog v-if="lazyDialogs.version" :open="saveVersionOpen" :title="t.common.saveVersion" :confirm-label="t.common.saveVersion" confirm-icon="check" :destructive="false"
      :busy="saveVersionBusy" :error="saveVersionError || undefined" @confirm="saveVersion" @cancel="saveVersionOpen = false">
      <form class="deployment-form" data-save-version @submit.prevent="saveVersion">
        <label for="save-version-label">{{ t.dlg.versionLabel }}</label>
        <Input id="save-version-label" v-model="saveVersionLabel" maxlength="200" autofocus autocomplete="off" :placeholder="t.dlg.versionPlaceholder" />
      </form>
      <p>{{ t.dlg.versionText }}</p>
    </ConfirmDialog>
    <!-- A copy with warnings keeps the dialog: what to set by hand stays in view until the copy is opened (as the summary menu). -->
    <!-- Both stay mounted on the last deployment (deploymentSeen): a closing one leaves with its motion and the form gives way to the result. -->
    <ConfirmDialog v-if="deploymentSeen?.created" :open="!!deployment?.created" :title="t.dlg.deploymentCreated(deploymentSeen.created.title)" :confirm-label="t.common.open" confirm-icon="file-text"
      :cancel-label="t.common.close" :destructive="false" @confirm="openCreated" @cancel="deployment = undefined">
      <div class="deployment-warnings" role="status" data-deployment-warnings>{{ t.dlg.deploymentPartial }}
        <ul><li v-for="warning in deploymentSeen.created.warnings" :key="warning"><AtlasIcon name="info" :size="14" />{{ warning }}</li></ul>
      </div>
    </ConfirmDialog>
    <ConfirmDialog v-if="deploymentSeen" :open="!!deployment && !deployment.created" :title="t.dlg.deploymentFromPage" :confirm-label="t.dlg.create" confirm-icon="file-plus" :destructive="false"
      :busy="deploymentSeen.busy" :error="deploymentSeen.error || undefined" @confirm="createDeployment" @cancel="deployment = undefined">
      <form class="deployment-form" @submit.prevent="createDeployment">
        <label for="deployment-title">{{ t.dlg.deploymentTitle }}</label>
        <Input id="deployment-title" v-model="deploymentSeen.title" maxlength="200" required autofocus autocomplete="off" :placeholder="t.dlg.deploymentPlaceholder" @focus="selectPrefilled" />
      </form>
      <p>{{ t.dlg.deploymentCopyText }}</p>
      <p v-if="dirty">{{ t.dlg.deploymentDirty }}</p>
    </ConfirmDialog>
    <DiscardChangesDialog v-if="discardSeen && page && publication?.publishedRevisionId" :open="discardOpen" :title="t.dlg.discardTitle" :page-id="page.id" :published-revision-id="publication.publishedRevisionId" :version="page.version"
      :dark="dark" :accent="appearance.accent" :prepare="saveBeforeDiscard" :before-restore="beforeHistoryRestore" @close="discardOpen = false" @restored="discarded" />
    <ConfirmDialog v-if="boardOpen" :open="boardTrashOpen" :title="t.dlg.boardTrashTitle" :confirm-label="t.common.moveToTrash" confirm-icon="trash"
      :busy="boardTrashBusy" :error="boardTrashError || undefined" @confirm="trashBoard" @cancel="boardTrashOpen = false">
      <p><strong>{{ boardTitle }}</strong> {{ t.dlg.boardTrashText }}</p>
      <UsagePanel v-if="boardId" :object-id="boardId" still />
    </ConfirmDialog>
    <PageReadership v-if="readershipOpen && page && user" :key="`${user.id}:${page.id}`" :page-id="page.id" @close="readershipOpen = false" />
    <PagePdfDownloadDialog v-if="lazyDialogs.pdf" :open="pdfDownloadOpen" v-model="pdfCoverDetails" :title="page?.title" :space="currentSpace?.name" :save-first="dirty" @confirm="downloadPdf" @cancel="pdfDownloadOpen = false" />
    <PagePdfExportDialog v-if="pdfExportOpen && page && user && pageCanWrite" :key="user.id + ':' + page.id" :page-id="page.id" :title="page.title" @dirty="pdfExportDirty = $event" @busy="pdfExportBusy = $event" @close="pdfExportOpen = false" />
    <PageHistoryPanel
      v-if="historyOpen && page && user && pageCanWrite"
      :page="page"
      :initial-revision="historyInitialRevision"
      :space-role="currentSpace?.role ?? 'Reader'"
      :is-wiki-admin="user.isWikiAdmin"
      :dark="dark"
      :accent="appearance.accent"
      :before-restore="beforeHistoryRestore"
      @close="historyOpen = false"
      @restored="applyRestoredPage"
    />
    <!-- Was a second hand-built <dialog> repeating AppDialog's chrome, its focus
         handling, its Escape semantics and its close-on-route-change. It is the
         same primitive now; only the palette itself is bespoke. -->
    <AppDialog
      class="search-dialog"
      unfold-trigger=".search-launch"
      :title="t.common.searchWiki"
      size="wide"
      header-class="search-top"
      :data-command-mode="commandMode"
      :data-search-view="searchView"
      :open="searchIsOpen"
      @close="closeSearch"
      @closed="searchDialogClosed"
      @keydown="searchDialogKeys"
    >
      <!-- Refresh 2.5 (board „Hledání (Ctrl K)“): the field row with the scope chips, then the modes as tabs with the state of
           the sources. Both stay in the header, so they never scroll away with the results. -->
      <template #header-extra>
        <div class="search-query-controls">
        <div class="omnisearch-input" :data-searching="searching">
          <AtlasIcon name="search" :size="20" /><Input
            ref="searchField"
            autofocus
            class="h-auto border-0 bg-transparent p-0 shadow-none dark:bg-transparent focus-visible:border-0 focus-visible:[outline:none]!"
            v-model="query"
            maxlength="200"
            :placeholder="commandMode ? t.search.writeCommand : askMode ? t.search.askWiki : t.search.placeholder"
            :aria-label="commandMode ? t.search.findCommand : t.search.wikiPrefix + ' ' + searchScopeLabel"
            role="combobox"
            aria-autocomplete="list"
            :aria-describedby="aiCapabilities.answer && !commandMode ? 'search-hint search-mode-hint' : 'search-hint'"
            :aria-controls="commandMode ? 'command-results' : showSuggestions ? 'search-suggestions-results' : 'search-results'"
            :aria-expanded="commandMode ? commands.length > 0 : searchResultCount > 0"
            :aria-activedescendant="
              commandMode ? (commands[searchIndex] ? `command-${commands[searchIndex]!.id}` : undefined) : searchResultCount ? searchResultId(searchIndex) : undefined
            "
            @keydown="searchKeys"
          />
          <AiStatus v-if="meaningPending" size="xs" state="thinking" :label="t.search.meaning" class="search-meaning-status" data-search-meaning-status />
          <Button variant="ghost" size="icon-sm" v-if="query" type="button" class="search-clear text-muted-foreground" :aria-label="t.search.clearQuery" @click="query = ''; fieldElement(searchField)?.focus()">
            <AtlasIcon name="close" :size="14" />
          </Button>
        </div>
        <Transition name="search-scope">
        <div v-if="!commandMode" class="search-scope" role="group" :aria-label="t.search.scopeGroup">
          <FilterMenu v-model="searchSpace" :options="searchSpaceOptions" :label="t.search.spaceLabel" :search-label="t.search.findSpace" icon="folder" data-search-space />
          <Button variant="outline" type="button" size="sm" class="search-filter-toggle" aria-controls="search-filters" aria-keyshortcuts="Control+Shift+F" :aria-expanded="filtersOpen" @click="filtersOpen = !filtersOpen">{{ t.search.filters }}<Badge variant="secondary" v-if="searchFiltered">{{ activeFilterCount }}</Badge></Button>
        </div>
        </Transition>
        <Tooltip>
          <TooltipTrigger as-child>
            <Button variant="ghost" size="icon-sm" type="button" class="search-view-toggle text-muted-foreground" data-search-view-toggle aria-keyshortcuts="Control+Shift+Enter"
              :aria-label="searchView === 'page' ? t.search.shrink : t.search.fullPage" @click="toggleSearchView">
              <AtlasIcon :name="searchView === 'page' ? 'minimize-2' : 'maximize-2'" :size="16" />
            </Button>
          </TooltipTrigger>
          <TooltipContent>{{ searchView === 'page' ? t.search.shrinkShort : t.search.fullPageShort }} (Ctrl Shift Enter)</TooltipContent>
        </Tooltip>
        </div>
        <div class="search-tabs">
          <SearchModeSwitch :mode="commandMode ? 'commands' : askMode ? 'ask' : 'pages'" :answer="aiCapabilities.answer" :count="!commandMode && !showSuggestions && searchResultCount ? `${searchResultCount}${hasMore ? '+' : ''}` : null" @change="setSearchMode" />
          <div v-if="!commandMode && (searching || searchAttempted)" class="search-source-status" role="status" aria-live="polite">
            <span :data-loading="searching" :data-error="(!searching && !!searchError) || undefined"><i />Wiki · {{ searching ? t.search.searching : searchError ? t.search.unavailable : t.search.done }}</span>
            <span v-if="searchSemantic && semanticOn" data-semantic-source><i />{{ t.search.semanticSource }} · {{ searching ? t.search.searching : t.search.done }}</span>
            <small v-if="searching">{{ t.search.resultsFilling }}</small>
          </div>
          <span v-else-if="!commandMode" class="search-scope-note">{{ semanticOn ? t.search.textAndMeaning : t.search.text }} · {{ t.search.readableOnly }}</span>
        </div>
      </template>
      <div class="search-columns" :class="{ 'with-answer': searchAnswerPaneOpen }">
      <div class="search-content">
        <template v-if="commandMode">
          <p id="search-hint" class="command-hint">{{ t.search.commandHint }} <kbd>Ctrl ⇧ P</kbd></p>
          <div id="command-results" role="listbox" :aria-label="t.search.commands" @keydown="searchKeys">
            <button v-for="(command, index) in commands" :id="`command-${command.id}`" :key="command.id" type="button" role="option" :aria-selected="index === searchIndex" tabindex="-1" class="command-result" :class="{ active: index === searchIndex }" @pointermove="selectSearchPointer($event, index)" @mousedown.prevent @click="runCommand(command.id)">
              <CommandIcon :name="command.icon" /><span><strong>{{ command.label }}</strong><small>{{ command.detail }}</small></span><kbd aria-hidden="true">↵</kbd>
            </button>
          </div>
          <EmptyState v-if="!commands.length" class="command-empty" compact icon="search" :title="pending || profileBusy || profilePending ? t.search.waitForAction : t.search.noCommand" :text="pending || profileBusy || profilePending ? undefined : t.search.tryOtherWords" />
        </template>
        <template v-else>
        <TagSearchSuggestions v-if="user && searchIsOpen && !searchTag" :query="query" :user-id="user.id" @select-tag="openTagSearch" />
        <InlineReveal :open="searchFiltered">
        <div class="search-active-filters tags-ui" :aria-label="t.search.activeFilters">
          <button v-if="searchTag" type="button" class="tag-chip selected-search-tag" :data-tone="tagTone(searchTag.name)" :aria-label="t.search.clearTag(searchTag.name)" @click="searchTag = undefined"><AtlasIcon name="hash" :size="14" />{{ searchTag.name }}<AtlasIcon name="close" :size="14" /></button>
          <Badge variant="secondary" v-if="searchSpace" as="button" type="button" class="max-w-full cursor-pointer break-words bg-accent-soft text-primary hover:brightness-[0.96]" :aria-label="t.search.clearSpace(spaces.find(s => s.id === searchSpace)?.name)" @click="searchSpace = ''">{{ spaces.find(s => s.id === searchSpace)?.name }}<AtlasIcon name="close" :size="14" /></Badge>
          <Badge variant="secondary" v-if="searchContent !== 'all'" as="button" type="button" class="max-w-full cursor-pointer break-words bg-accent-soft text-primary hover:brightness-[0.96]" :aria-label="t.search.clearScope" @click="searchContent = 'all'">{{ searchContent === 'drafts' ? t.search.drafts : t.search.published }}<AtlasIcon name="close" :size="14" /></Badge>
          <Badge variant="secondary" v-if="searchChangedWithin !== 'any'" as="button" type="button" class="max-w-full cursor-pointer break-words bg-accent-soft text-primary hover:brightness-[0.96]" :aria-label="t.search.clearChanged" @click="searchChangedWithin = 'any'">{{ searchChangedWithin === 'day' ? t.search.h24 : searchChangedWithin === 'week' ? t.search.d7 : t.search.d30 }}<AtlasIcon name="close" :size="14" /></Badge>
          <Badge variant="secondary" v-if="searchSort !== 'relevance'" as="button" type="button" class="max-w-full cursor-pointer break-words bg-accent-soft text-primary hover:brightness-[0.96]" :aria-label="t.search.resetSort" @click="searchSort = 'relevance'">{{ t.search.newest }}<AtlasIcon name="close" :size="14" /></Badge>
        </div>
        </InlineReveal>
        <InlineReveal id="search-filters" :open="filtersOpen" keep-mounted>
        <div class="search-filters">
          <OptionPills v-model="searchChangedWithin" :label="t.search.edited" :options="searchChangedOptions" />
          <OptionPills v-model="searchContent" :label="t.search.content" :options="searchContentOptions" />
          <OptionPills v-model="searchSort" :label="t.search.sorting" :options="searchSortOptions" />
          <Button variant="ghost" v-if="searchFiltered" type="button" @click="resetSearchFilters">{{ t.search.clearFilters }}</Button>
        </div>
        </InlineReveal>
        <p id="search-hint" class="visually-hidden">
          {{ searchTag ? t.search.tagPages : searchScopeHint }}
        </p>
        <div v-if="!searchResultCount && !searchAttempted && searching" class="search-skeleton" aria-hidden="true"><div v-for="row in 6" :key="row"><i /><span><b /><b /></span></div></div>
        <div v-if="searchError" role="alert" class="notice error">
          {{ searchError }}
          <button class="search-retry" type="button" @click="scheduleSearch">
            {{ t.search.retry }}
          </button>
        </div>
        <EmptyState v-else-if="searchAttempted && !searchResultCount && !showSuggestions" compact icon="search" :title="t.search.noPages" :text="searchFiltered ? t.search.tryClear : t.search.tryOtherWords" :data-stale="searchStale || undefined" />
        <SearchSuggestions v-else-if="showSuggestions" :active-index="searchResultChosen ? searchIndex : -1" :user-id="user?.id" @loaded="suggestedPages = $event" @select="searchIndex = $event; searchResultChosen = true" @keydown="searchKeys" @open="id => openPage(id)" />
        <EmptyState v-else-if="!searchTag && query.trim().length < 2" compact icon="search" :title="t.search.twoChars" :text="t.search.inTitlesAndContent" />
        <p v-else-if="results.length" role="status" class="visually-hidden">
          {{ t.search.shownResults }} {{ searchResultCount }}
        </p>
        <div
          id="search-results"
          role="listbox"
          :aria-label="t.search.resultsAria"
          :aria-busy="searching || searchOpening"
          :data-stale="searchStale || undefined"
          @keydown="searchKeys"
        >
          <section v-for="group in searchGroups" :key="group.kind" class="search-group" role="group" :aria-label="group.title" :data-kind="group.kind">
            <h3 class="search-group-title">{{ group.title }}<span>{{ group.items.length }}</span></h3>
            <template v-if="group.kind === 'spaces'">
              <a v-for="item in group.items" v-memo="[item, searchIndex === item.index, query]" :id="`search-result-${item.index}`" :key="item.key" :href="item.href" class="search-result" role="option" :tabindex="searchIndex === item.index ? 0 : -1" :aria-selected="searchIndex === item.index" :class="{ active: searchIndex === item.index }" @mousedown.prevent @pointermove="selectSearchPointer($event, item.index)" @focus="searchIndex = item.index" @click="openSearchSpaceLink($event, item.result.id)"><span class="space-icon" :data-color="item.color"><AtlasIcon :name="item.icon" :size="18" /></span><span><strong><SearchHighlight :text="item.title" :query="query" /></strong><small>{{ item.detail }}</small></span></a>
            </template>
            <template v-else>
            <a v-for="item in group.items" v-memo="[item, searchIndex === item.index, query, searchOpening, highlightedPage === item.result.id]" :id="`search-result-${item.index}`" :key="item.key"
              role="option" :tabindex="item.index === searchIndex ? 0 : -1" :aria-disabled="searchOpening"
              :href="item.href" :target="item.widget?.url ? '_blank' : undefined" :rel="item.widget?.url ? 'noopener noreferrer' : undefined" :aria-selected="item.index === searchIndex" :class="{ active: item.index === searchIndex }" :data-semantic="item.semantic" class="search-result" :data-page-id="!item.widget && !item.board && !item.result.isDraft ? item.result.id : undefined" :data-search-board="item.board || undefined" :data-answer-highlight="highlightedPage === item.result.id || undefined"
              @mousedown.prevent @click="item.widget?.url ? undefined : item.board ? openSearchBoard($event, item.result) : openSearchResult($event, item.result)" @pointermove="selectSearchPointer($event, item.index)" @pointerenter="truncatedTitle($event, '.search-result-title')" @focus="searchIndex = item.index; searchResultChosen = true">
              <span class="space-icon" :data-color="item.color"><AtlasIcon :name="item.icon" :size="18" /></span>
              <span class="search-result-main"><span class="search-result-head"><strong class="search-result-title"><span v-if="item.widget?.key" class="search-issue-key">{{ item.widget.key }}</span><SearchHighlight :text="item.title" :query="query" /></strong>
                <time v-if="!item.widget && item.result.changedAt" class="search-result-when" :datetime="item.result.changedAt" :title="`${item.result.isDraft ? t.search.saved : t.common.publishedShort} ${formatDateTime(item.result.changedAt)}`">{{ formatShortDate(item.result.changedAt) }}</time></span>
                <small class="search-result-meta"><span class="search-result-context"><i class="search-space-dot" :data-color="spaces.find(s => s.id === item.result.spaceId)?.color" aria-hidden="true" /><SearchHighlight :text="item.detail" :query="query" /></span><PageStatusBadge v-if="item.result.isDraft" :is-published="false" :version="1" :published-version="null" /><AtlasIcon v-if="'mark' in item && item.mark" :name="item.mark.icon" :size="13" class="search-verification" :data-warn="item.mark.warn || undefined" role="img" :aria-label="item.mark.warn ? t.common.verificationExpired : t.common.verifiedPage" :title="item.mark.warn ? t.common.verificationExpired : t.common.verifiedPage" data-search-verification /><SemanticBadge v-if="item.semantic" />
                  <span v-if="item.board" class="search-live-label" data-search-type>{{ t.common.board }}</span>
                  <span v-else-if="item.widget">{{ item.result.title }} · {{ item.result.spaceName }}<span v-if="item.widget.state"> · {{ item.widget.state }} {{ t.search.savedState }}</span></span>
                </small>
                <small v-if="item.result.matchedAlias" class="search-alias">{{ t.search.alsoFound }} <SearchHighlight :text="item.result.matchedAlias" :query="query" /></small>
                <ResultPassage v-if="item.passage" :text="item.passage.text" :heading-title="item.passage.headingTitle" :query="query" />
                <span v-else-if="!item.widget" class="search-snippet"><SearchHighlight :text="cleanSnippet(item.result.snippet)" :query="query" /></span>
              </span><AtlasIcon v-if="item.widget?.url" name="external" :size="16" />
            </a>
            </template>
            <div v-if="hasMore && (group.kind === 'pages' || group.kind === 'headings')" class="search-more-sentinel" data-search-more aria-hidden="true" />
          </section>
        </div>
        <p v-if="searchRelaxed && results.length && searchAttempted && !searchError" class="search-relaxed" role="status" data-search-relaxed>{{ t.search.partial }}</p>
        <p v-if="didYouMean && searchAttempted && !searchError" class="search-did-you-mean">{{ t.search.didYouMean }} <button type="button" data-did-you-mean @click="acceptDidYouMean">„{{ didYouMean }}“?</button></p>
        <div v-if="aiCapabilities.answer && searchAttempted && !searchResultCount && !searchError" class="search-answer-rescue">
          <Button type="button" variant="outline" size="sm" data-search-rescue-ask @click="setSearchMode('ask').then(() => searchAnswer.ask())"><CommandIcon name="ai" />{{ t.search.askAssistant }}</Button>
        </div>
        </template>
      </div>
      <Transition name="search-answer-pane"
        @before-enter="element => { element.removeAttribute('inert'); element.removeAttribute('aria-hidden'); }"
        @before-leave="element => { element.setAttribute('inert', ''); element.setAttribute('aria-hidden', 'true'); }">
      <aside v-if="searchAnswerPaneOpen" class="search-answer-pane" :aria-label="t.search.aiAnswer" tabindex="-1">
        <p v-if="searchTag || searchContent !== 'all' || searchChangedWithin !== 'any'" class="search-answer-scope-note">{{ t.search.answerScope }}</p>
        <SearchAnswerCard ref="searchAnswerCard" :answer="searchAnswer" :can-continue="canAsk" :source-origin="answerSourceOrigin"
          @continue="continueSearchAnswer" @highlight="highlightedSource = $event" @open-source="assistant.openSource"
          @try-other="fieldElement(searchField)?.focus()" />
        <div v-if="!searchAnswer.visible.value" class="search-answer-idle">
          <h2><CommandIcon name="ai" />{{ t.search.wikiAnswer }}</h2>
          <p>{{ searchAnswer.state.value === 'canceled' ? t.search.answerStopped : searchAnswer.preference.value === 'never' ? t.search.answerOff : t.search.answerEmpty }}</p>
        </div>
      </aside>
      </Transition>
      </div>
      <footer class="search-footer">
        <span class="search-keyboard-hint">
          <span><kbd>↑</kbd><kbd>↓</kbd> {{ t.search.select }}</span>
          <span><kbd>Enter</kbd> {{ commandMode ? t.search.run : askMode && !searchResultChosen ? t.search.ask : t.common.open }}</span>
          <span v-if="!commandMode"><kbd>&gt;</kbd> {{ t.search.commands }}</span>
          <span v-if="!commandMode"><kbd>Ctrl ⇧ F</kbd> {{ t.search.scopeAndFilters }}</span>
          <span v-if="aiCapabilities.answer && !commandMode" id="search-mode-hint" class="search-mode-hint" data-search-shortcut-hint><kbd>?</kbd> {{ t.search.askAiHint }} · <kbd>F6</kbd> {{ t.search.answerOrQuery }}</span>
          <span class="search-view-hint"><kbd>Ctrl ⇧ Enter</kbd> {{ searchView === 'page' ? t.search.toWindow : t.search.wholePage }}</span>
          <span><kbd>Esc</kbd> {{ t.common.close }}</span>
        </span>
        <span v-if="!commandMode" class="search-footer-end">
          <span class="search-summary"><strong class="search-summary-count"><span class="search-summary-label">{{ t.search.shown }} </span>{{ searchResultCount }}{{ hasMore ? '+' : '' }}</strong><LoadingNote inline class="search-opening" :active="searchOpening || loadingMore" :label="loadingMore && !searchOpening ? t.search.loadingMore : t.search.opening" /></span>
          <Button variant="outline" v-if="hasMore" size="sm" class="search-load-more" :disabled="loadingMore || searching" :aria-busy="loadingMore" aria-controls="search-results" @click="moreSearch">{{ t.search.loadMore }}</Button>
        </span>
      </footer>
    </AppDialog>
    <ReadonlySelectionActions v-if="readonlySelection" :selection="readonlySelection" :enabled="!editorActive" :explain-enabled="assistant.canAnswer.value" @ask="selectionAction('ask', $event)" @explain="selectionAction('explain', $event)" @summarize="selectionAction('summarize', $event)" @find="findSelection" @close="readonlySelection = null" />
    <Toaster dir="ltr" position="bottom-right" :theme="dark ? 'dark' : 'light'" :duration="4000" close-button :toast-options="{ closeButtonAriaLabel: t.common.close }"
      :offset="{ bottom: 'var(--atlas-toast-bottom, 24px)' }" :mobile-offset="{ top: 68, bottom: 'var(--atlas-toast-bottom, 16px)' }" /><!-- A toast at the top (Co je nového) stays under the phone's top bar, not over its menu and search. -->
    <ContactLastUsageDialog />
  </div>
  </TooltipProvider>
</template>

<style scoped>
.document-title-error { margin: -8px 0 10px; color: var(--error); font-size: 13px; }
.search-answer-scope-note { margin: 0; color: var(--muted); font-size: 12px; line-height: 1.5; }
.search-answer-rescue { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 10px; }
.search-result[data-answer-highlight] { background: var(--accent-soft); box-shadow: inset 0 0 0 1px var(--line); }
.search-result { transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), box-shadow var(--atlas-motion-fast) var(--atlas-ease-out); }
/* With the Projector tabs the row may outgrow a phone: it scrolls sideways on its own and the labels never wrap. */
.space-tabs { display: flex; gap: 4px; margin-top: 22px; border-bottom: 1px solid var(--line); overflow-x: auto; scrollbar-width: none;
  mask-image: linear-gradient(to right, transparent, #000 var(--atlas-scroll-fade-start), #000 calc(100% - var(--atlas-scroll-fade-end)), transparent);
  transition: --atlas-scroll-fade-start var(--atlas-motion-fast) var(--atlas-ease-out), --atlas-scroll-fade-end var(--atlas-motion-fast) var(--atlas-ease-out); }
.space-tabs[data-more-before] { --atlas-scroll-fade-start: 24px; }
.space-tabs[data-more-after] { --atlas-scroll-fade-end: 24px; }
.space-tabs button { flex-shrink: 0; border: 0; border-bottom: 2px solid transparent; background: transparent; color: var(--muted); padding: 10px 14px; font: inherit; font-size: 13px; white-space: nowrap; cursor: pointer; }
@media (hover: hover) and (pointer: fine) { .space-tabs button:hover { color: var(--text); background: var(--hover); } }
.space-tabs button.active { border-bottom-color: var(--accent); color: var(--text); font-weight: 600; }
.space-tabs button:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
.deployment-warnings ul { display: grid; gap: 4px; margin: 6px 0 0; padding: 0; list-style: none; }
.deployment-warnings li { display: flex; align-items: flex-start; gap: 6px; }
.deployment-warnings li :deep(svg) { flex: none; margin-top: 2px; color: var(--warning, var(--accent)); }
</style>
