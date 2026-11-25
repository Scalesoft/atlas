<script lang="ts">
import { shallowRef } from "vue";
import { lazyComponent } from "../lazyComponent";
import { loadMarkdownDialect } from "@atlas/markdown/core";
import { createCodeBlockHighlighting } from "@atlas/markdown/plugins/editor-tools";
// Loaded once per app session, as soon as this module is evaluated — well before any
// page opens (opening one is itself an async network round trip) — so the extension is
// usually ready by the time the first editor mounts. `codeBlock` is immutable per editor
// instance (see the plugin's README): once `MarkdownEditor` builds its controller it never
// picks up a later change to this ref. An instance that mounts before the promise settles
// re-keys (see `codeBlockGeneration` below) to pick it up once it arrives, instead of
// being permanently built without it. A rejection is logged once here rather than
// becoming an unhandled rejection.
const sharedCodeBlock = shallowRef<Awaited<ReturnType<typeof createCodeBlockHighlighting>>>();
/** Settles (never rejects) once highlighting is available or failed; the async loader in App.vue awaits it. */
export const codeBlockHighlightingReady: Promise<void> = createCodeBlockHighlighting()
  .then(extension => { sharedCodeBlock.value = extension; })
  .catch(error => { console.error("Nepodařilo se načíst zvýrazňování kódu.", error); });
// A collaborative editor waits for it instead of re-keying: its adapter serves one editor only, and a remount on the late extension
// with the used adapter failed (UX EP2-6). A local (reader's) editor does not wait and re-keys once.
const codeBlockSettled = shallowRef(false);
void codeBlockHighlightingReady.then(() => { codeBlockSettled.value = true; });
// The editor loads the Markdown dialect of `a-` components (contacts, charts) on demand and refuses to read or write
// one before it has arrived, while this host serializes synchronously (capture, projections, the hydration check)
// and a renderer would draw the page again once it arrives. Loaded with this module; the editors below wait for it.
const markdownDialectSettled = shallowRef(false);
void loadMarkdownDialect()
  .catch(error => { console.error("Nepodařilo se načíst dialekt Markdownu.", error); })
  .finally(() => { markdownDialectSettled.value = true; });
</script>
<script setup lang="ts">
import t from "../i18n/shell";
import shellMore from "../i18n/shell-more";
import type { Appearance } from "../api";
import {
  ref,
  computed,
  watch,
  nextTick,
  onBeforeUnmount,
  onMounted,
  onUnmounted,
  onErrorCaptured,
} from "vue";
import { MarkdownEditor } from "@atlas/markdown/vue";
import { Selection, TextSelection } from "@tiptap/pm/state";
import type {
  MarkdownCollaborationAdapter,
  MarkdownEditorController,
  MarkdownEditorState,
  MarkdownIntention,
  MarkdownPlugin,
  MarkdownRichValue,
} from "@atlas/markdown/core";
import { detectSecrets } from "../assist/rules/secretDetector";
import { createYouTrackClient, editorChrome, editorOutline, editorThemeClass } from "../editor/editorSetup";
import { insertInlineSecretReference, insertSecretReference, type SecretReplacementContext } from "@atlas/markdown/plugins/secrets";
import { createAtlasSecretsServices } from "../secrets/secretsHost";
import { createSecretAccessEvents } from "../secrets/secretEvents";
import { createSecretReauth } from "../secrets/secretReauth";
import { createSecretPickerPlugin } from "../secrets/secretPickerPlugin";
import secretPickerText from "../i18n/secrets-picker";
import { openVuePopover } from "../projector/openVuePopover";
import { moveSecretToVault as moveToVault } from "../secrets/moveSecretToVault";
import { secretsApi } from "../secrets/secretsApi";
import { projectReadonlySelection } from "../assist/readonlyProjection";
import { createLiveEditorPlugins } from "../assist/liveEditorPlugins";
import type { AtlasAssistFactory, PickedSecret, SecretPickRequest } from "../assist/types";
import { useAiCapabilities } from "../composables/useAiCapabilities";
import { useAssistant } from "../assistant/useAssistant";
import { askAssistant } from "../assistant/assistantEntries";
import { useToast } from "../composables/useToast";
import { animateAtlas, glow, motionMs, prefersReducedMotion } from "../motion/aiMotion";
import { revealSettled } from "../revealSettled";
import LoadingSkeleton from "./LoadingSkeleton.vue";
import { useSegmentedThumb } from "./library/segmentedThumb";
import { atlasContentPlugins } from "../documentWidgets";
import { projectorApi } from "../projector/projectorApi";
import { useModules } from "../composables/useModules";
import type { ProjectorHost } from "../projector/requirementsPlugin";
import { firstVisibleBlock } from "../lib/firstVisibleBlock";
import { createPageSource, createPeopleServices, type PageOrderSection } from "../editorDirectories";
import { lexiconSource } from "../linking/lexicon";
import { useOpenApiDetail, type OpenApiDetailContext } from "../openApiDetail";
import { historyApi } from "../history";
import { reapplyUnsent } from "../editor/unsentEdits";
import OpenApiDetailPanels from "./OpenApiDetailPanels.vue";
import { prefetchPageSource, type PageSourceSaveResult } from "../pageSource";
import { MarkdownSourcePanel, markdownProjection, usePageSourceDraft } from "../pageSourceDraft";
import UnsavedChangesDialog from "./UnsavedChangesDialog.vue";
import {
  createMarkdownImportHooks,
  documentFiles,
  type ImportPage,
} from "../markdownImport";
import { navigateToHeading as navigateHeading } from "@atlas/markdown/plugins/heading-anchors";
import { createRevealPlugin, revealCommand } from "../editor/revealPlugin";
import { atlasObjectDragType, readObjectsPayload, startObjectDrag } from "../boards/boardDrop";
import { contactAt, contactObject, contactObjects, type ContactSnapshots } from "../pageObjectDrag";
import { normalizeContact, type MarkdownContact } from "@atlas/markdown/plugins/contacts";
import { contactApi } from "../contacts/contactApi";
import { contactsIn, linkContact, repeatedContact, type ContactHost } from "../contacts/contactLinking";
import { createHeadingFoldPlugin } from "../editor/headingFoldPlugin";
import { unfoldTo } from "../editor/headingFold";
import type { RevealTarget } from "../editor/revealTarget";
import "@atlas/markdown/styles/base.css";
import "@atlas/markdown/themes/ocean.css";
import "@atlas/markdown/themes/forest.css";
import "@atlas/markdown/themes/midnight.css";
import { errorMessage } from "../api";
import { createEditorUploads } from "../editorUploads";
import type { EditorPluginAction } from "../editorActions";
import { captureDocument } from "../document";
import { afterPaint, whenRested } from "../idle";
import { liveConnection } from "../live/liveConnection";
import { createCollaborationPresence, type CollaborationPeer } from "../collaborationPresence";
import { Button } from './ui/button';
import AtlasIcon from './AtlasIcon.vue';
import {
  collaborationCanSave,
  collaborationCheckpointReady,
  captureCollaborationBaseline,
  collaborationSessionCheck,
  hydrationChanges,
  joinPageCollaboration,
  openBundledCollaboration,
  type BundledCollaboration,
  type CollaborationIdentity,
  type CollaborationLease,
  type CollaborationStatus,
} from "../collaboration";
import { collaborationErrorText } from "../documentPresentation";
const props = defineProps<{
  value: MarkdownRichValue;
  readonly: boolean;
  dark: boolean;
  accent: Appearance["accent"];
  spaceId: string;
  pageId?: string;
  documentTitle?: string;
  identity?: CollaborationIdentity;
  collaborationAllowed?: boolean;
  /**
   * A reader of a page published live (3.6-I, Publikace Živě): the editor is the writers' shared session, read-only, so changes arrive as
   * they are typed. No session yet: the page's own value until it changes.
   */
  liveReading?: boolean;
  /** The page bundle's live state (writers, and readers of a live page): the shared editor builds from it at once, before the hub joins. */
  collaboration?: BundledCollaboration | null;
  /** The author may replace the page from its Markdown source (space Admin or WikiAdmin); offered while editing only. */
  canEditSource?: boolean;
  /** Host save of the „Zdroj“ tab: flushes the author's own live edits, then replaces the page on the server. */
  saveSource?: (value: MarkdownRichValue, markdown: string, signal: AbortSignal) => Promise<PageSourceSaveResult>;
  /** Pages of the current space (loaded tree) for wiki links in imported Markdown. */
  pages?: readonly (ImportPage & { readonly parentId?: string | null })[];
  /** „Návrhy při psaní“ (plan §12 H.4): the lazily loaded adapter, or null while off or unavailable. Live editors only. */
  assist?: AtlasAssistFactory | null;
  /** The assist may still arrive (the wiki switch or AI is on): only then a live editor built before it keeps a place for it. */
  assistExpected?: boolean;
  /** Opt-in safe reading selection; no selection controls or outgoing text when false. */
  readonlySelectionEnabled?: boolean;
  /** Whether the page has unsaved changes (the host's draft state); the deployment summary warns before copying the saved page. */
  unsaved?: () => boolean;
  /** Whether the page is a requirement catalog: the page DTO's `isCatalog` (v-model:catalog), the one flag both page menus change. */
  catalog?: boolean;
  /** The page has its own access (the page DTO's `ownAccess`): „Poskládat tabuli“ refuses it, the board would be the whole space's. */
  ownAccess?: boolean;
  /** The page's deployment board is offered (made, for a reader): the Přehled card of a page read shows „Tabule nasazení“ to open it. */
  deploymentBoard?: boolean;
  /** The page can be written now (the right and the lock). */
  canEdit?: boolean;
  /** The page's arrival skeleton is on screen: the editor's skeleton replaces it as it is, without fading in again. */
  skeletonShown?: boolean;
  /** The page took the place of its arrival (its title stood while it loaded): it draws as it stands, without the reveal wave. */
  arrived?: boolean;
  /** Classes of the editor itself (a board's page preview edited in place: `board-text`, the layout of the preview it replaces). */
  editorClass?: string;
}>();
const emit = defineEmits<{
  change: [];
  ready: [];
  collaborationStatus: [status: CollaborationStatus];
  collaborationPeers: [peers: CollaborationPeer[]];
  pluginActions: [actions: EditorPluginAction[]];
  /** An OpenAPI YAML draft exists that is not yet applied to the page draft. */
  detailDraft: [dirty: boolean];
  /** A page source draft exists that is not yet saved. */
  sourceDraft: [dirty: boolean];
  /** The author picked „Zdroj“ in the Dokument | Zdroj switch; the host flushes their live edits, then calls `openSource`. */
  requestSource: [];
  /** A key, a paste or a drop into the read-only text (3.6-F): the host pulses a writer's lock. */
  editIntent: [];
  /** Imported Markdown named the page while its title was empty. */
  importTitle: [title: string];
  aiAssistedApplied: [];
  readonlySelection: [selection: { text: string | null; searchText: string | null; section?: string; anchorRect: DOMRect; sectionHref?: string } | null];
  /** „Kde se to ještě zmiňuje“ on a selection of the live editor (selectionActions). */
  findSelection: [text: string];
  /** The deployment summary's menu marked or unmarked the page as a catalog (the server answered); the page takes it as its flag. */
  "update:catalog": [isCatalog: boolean];
  /** The writer's session ended on a new generation of the page (a restore, a source replace) and joined the current one by itself. */
  reopened: [];
  /** Whether Ctrl+Z has something to undo in the text the writer can edit now (the page's „Zpět“, PageUndoWedge); sent when it changes. */
  canUndo: [canUndo: boolean];
}>();
const value = shallowRef(props.value),
  adapter = shallowRef<MarkdownCollaborationAdapter>(),
  lease = shallowRef<CollaborationLease>(),
  failure = ref(""),
  joining = ref(false),
  joinFailure = shallowRef<MarkdownEditorState["error"]>(null);
const joinError = computed(() => joinFailure.value?.message ?? "");
const schemaMismatch = computed(() => joinError.value.includes('[SCHEMA_MISMATCH]'));
// A tab of the previous release opening content the new server already saved (registry.migrate): only a reload helps.
const staleEditor = computed(() => failure.value.includes('newer than the installed schema'));
/** Reading a live page without the right to write: the session is the writers', joined read-only. */
const readingLive = computed(() => props.liveReading === true && props.collaborationAllowed !== true);
/** A live page with no session to join yet (404 `no_live_session`): static until the page changes (PageChanged). */
const liveIdle = ref(false);
/** A writer's editor is always the shared session's (read-only while locked); a live page's reader follows it read-only. */
const sessionRequested = computed(() => props.collaborationAllowed === true || (readingLive.value && !liveIdle.value));
const theme = computed(() => editorThemeClass(props.dark, props.accent));
let controller: MarkdownEditorController<"rich"> | undefined,
  collaborationBaseline: MarkdownRichValue | undefined,
  unsubscribe: (() => void) | undefined,
  attempt = 0,
  disposed = false;
// Changing either key recreates the editor instance, so every open block context is stale.
const codeBlockGeneration = ref(0);
/**
 * The shared editor holds the document (lifecycle ready). Built from the bundle's state it does at once (3.6: the server seeds every page,
 * LiveSeeds); only without one (the server's seed failed) the local editor shows the page's value, read-only, until the session has seeded
 * and synced.
 */
const hydrated = ref(false);
const hydrating = computed(() => !!adapter.value && !hydrated.value && !failure.value);
/** The bundle's state is being decoded into the shared editor's session: nothing else builds meanwhile (a few ms). */
const bundleOpening = ref(false);
/**
 * No editor is on the page yet (the bundle's state still opening, the dialect still loading): the body keeps the page's skeleton, the
 * same one the page arrived with, so the frame painted before the editor's build is never an empty page (UX open blink).
 */
const editorPending = computed(() => !failure.value && !(markdownDialectSettled.value
  && ((!!adapter.value && codeBlockSettled.value) || (!bundleOpening.value && (!adapter.value || hydrating.value)))));
/** A lazy block builds its full form before the caret or the eye goes there (G1, @atlas/markdown proximity: `md-reveal`). */
function revealLazy(element: Element | null | undefined) { element?.dispatchEvent(new Event("md-reveal", { bubbles: true })); }
watch(adapter, () => { hydrated.value = false; }, { flush: "sync" });
/**
 * The page's reveal wave (motion.css `data-atlas-reveal`, plan 3.6 „Vlna zjevení“): on while this page draws anew, off once its last
 * staggered block has risen, at the hand-over from the stand-in (the shared editor arrives as it stands) or 1.5 s after an editor shows.
 * Never for a page that takes its arrival's place (`arrived`): its blocks rising 8 px under the title that already stood was the open's jump.
 */
const revealing = ref(!props.arrived);
let revealTimer: ReturnType<typeof setTimeout> | undefined;
watch(editorPending, pending => { if (!pending && !revealTimer) revealTimer = setTimeout(() => { revealing.value = false; }, 1500); }, { immediate: true });
watch(hydrating, (now, before) => { if (before && !now) revealing.value = false; });
/**
 * The wave on the editor that shows: its first eight blocks brighten one after another, a third of --atlas-motion-fast apart (reduced
 * motion: together, at most 160 ms), from .65 so the text is readable at once. Opacity only, nothing measured. By script, not motion.css
 * `:nth-child`: a positional rule marked the editor's block list for good, and each Enter or Backspace then restyled every block of the
 * page (2,000 blocks: 54 → 28 ms a key). Played once; it ends the wave when its last block has risen.
 */
let revealPlayed = false;
function playReveal() {
  const blocks = editorSurface.value?.querySelector(".atlas-document-editor:not(.editor-hydrating) .ProseMirror");
  if (!revealing.value || revealPlayed || !blocks) return;
  revealPlayed = true;
  const stagger = prefersReducedMotion(blocks) ? 0 : motionMs(blocks, "--atlas-motion-fast") / 3, duration = motionMs(blocks, "--atlas-motion-slow");
  const wave = [...blocks.children].slice(0, 8).map((block, index) => animateAtlas(block, [{ opacity: 0.65 }, { opacity: 1 }], { duration, delay: index * stagger }));
  void wave.at(-1)?.finished.then(() => { revealing.value = false; }, () => {});
}
onBeforeUnmount(() => clearTimeout(revealTimer));
// A slow join is said by the page's status chip (App.vue `slowJoin`, documentPresentation.ts), never in or over the document.
// Host-owned OpenAPI detail (OpenApiDetailPanels, a dialog over the page): runtime-only, never serialized or put in the URL.
const detail = useOpenApiDetail({
  readonly: () => props.readonly,
  session: () => `${adapter.value ? `collaboration:${lease.value?.generation}` : "local"}:cb${codeBlockGeneration.value}`,
});
watch(detail.dirty, dirty => emit("detailDraft", dirty));
// Who changed the definition and when: the block carries no author, so the API view names the page's current revision.
const detailChange = shallowRef<{ at: string; by: string }>();
watch(() => detail.open.value && props.pageId, async (pageId, _, onCleanup) => {
  detailChange.value = undefined;
  if (!pageId) return;
  const abort = new AbortController();
  onCleanup(() => abort.abort());
  try {
    const current = (await historyApi.list(pageId, null, abort.signal)).items.find(item => item.isCurrent);
    if (current) detailChange.value = { at: current.activeFrom, by: current.authorName };
  } catch { /* Without access to the history the view only leaves the line out. */ }
});
const uploads = createEditorUploads(() => ({ pageId: props.pageId, canWrite: !props.readonly && !joining.value && !joinError.value && (!sessionRequested.value || !!adapter.value) }));
const presence = createCollaborationPresence({ onChange: peers => emit("collaborationPeers", peers), pointers: true });
// Password references (WP4 V6): one set of host services per document, shared by the reader view and the editors.
// Released values stay in memory only (secrets/secretsHost.ts); the picker and the re-authentication dialog load on use.
const SecretPicker = lazyComponent(() => import("../secrets/SecretPicker.vue"));
const SecretReauthDialog = lazyComponent(() => import("../secrets/SecretReauthDialog.vue"));
const secretReauth = createSecretReauth();
const secrets = createAtlasSecretsServices({
  reauth: secretReauth.prompt,
  events: createSecretAccessEvents(),
  reason: () => `Žádost o přístup ze stránky „${props.documentTitle?.trim() || "bez názvu"}“.`,
  pageId: () => props.pageId,
});
const secretPickerOpen = ref(false);
// The picker is an editor popover like the other in-editor pickers (Esc and the caret's return are the popover's); the state above
// only says whether it is open and for what, this opens and closes it.
let closeSecretPopover: (() => void) | undefined;
watch(secretPickerOpen, open => {
  const close = closeSecretPopover;
  closeSecretPopover = undefined;
  if (!open) return close?.();
  const editor = liveEditor();
  if (!editor) return closeSecretPicker();
  const mode = secretSwap.value ? "swap" : secretRequest.value?.mode ?? "pick";
  closeSecretPopover = openVuePopover({ editor, anchor: editor.view.dom, title: secretPickerText.picker[mode], variant: "panel", component: SecretPicker,
    props: { open: true, spaceId: props.spaceId, mode, initialQuery: secretSwap.value?.initialQuery ?? secretRequest.value?.initialQuery ?? "", currentId: secretSwap.value?.currentId, onInsert: insertSecret },
    closed: () => { closeSecretPopover = undefined; closeSecretPicker(); } });
});
const secretPicker = createSecretPickerPlugin(() => {
  if (props.readonly || !controller?.state.editable) return;
  secretPickerOpen.value = true;
});
function insertSecret(reference: { secretId: string; inline: boolean; vaultName?: string }) {
  if (secretSwap.value) return settleSecretSwap(reference.secretId);
  secretPickerOpen.value = false;
  const live = controller;
  const command = reference.inline ? insertInlineSecretReference({ secretId: reference.secretId }) : insertSecretReference({ secretId: reference.secretId });
  // A request of assistance or of the publication check writes the reference itself (over the span it replaces).
  if (secretRequest.value) return settleSecretRequest({ command, inline: reference.inline, ...(reference.vaultName ? { vaultName: reference.vaultName } : {}) });
  if (!live?.state.editable) return;
  if (live.dispatch(command)) live.focus();
}
// „Vložit odkaz na heslo z trezoru…“ and „Přesunout do trezoru“: the same picker, answered to the caller.
const secretRequest = shallowRef<SecretPickRequest & { resolve: (picked: PickedSecret | null) => void }>();
function settleSecretRequest(picked: PickedSecret | null) {
  const pending = secretRequest.value;
  secretRequest.value = undefined;
  secretPickerOpen.value = false;
  pending?.resolve(picked);
  if (picked) controller?.focus();
}
function pickSecret(request: SecretPickRequest) {
  if (secretRequest.value) settleSecretRequest(null);
  if (props.readonly || !controller?.state.editable || request.signal.aborted) return Promise.resolve(null);
  return new Promise<PickedSecret | null>(resolve => {
    secretRequest.value = { ...request, resolve };
    request.signal.addEventListener("abort", () => { if (secretRequest.value?.resolve === resolve) settleSecretRequest(null); }, { once: true });
    secretPickerOpen.value = true;
  });
}
// „Vyměnit heslo“ on a reference (the plugin's `pickReplacement`): the same picker, opened on the secret's KeePass group
// and marking the secret itself; it answers only the chosen id, which the plugin writes in place of the old one.
const secretSwap = shallowRef<{ currentId: string; initialQuery: string; resolve: (secretId: string | null) => void }>();
function settleSecretSwap(secretId: string | null) {
  const pending = secretSwap.value;
  if (!pending) return;
  secretSwap.value = undefined;
  secretPickerOpen.value = false;
  pending.resolve(secretId);
  if (secretId) controller?.focus();
}
async function swapSecret({ secretId, signal }: SecretReplacementContext) {
  if (props.readonly || !controller?.state.editable || signal.aborted) return null;
  const current = await secretsApi.reference(secretId, signal).catch(() => null);
  if (signal.aborted) return null;
  settleSecretSwap(null);
  if (secretRequest.value) settleSecretRequest(null);
  return new Promise<string | null>(resolve => {
    secretSwap.value = { currentId: secretId, initialQuery: current?.path || current?.title || "", resolve };
    signal.addEventListener("abort", () => { if (secretSwap.value?.resolve === resolve) settleSecretSwap(null); }, { once: true });
    secretPickerOpen.value = true;
  });
}
function closeSecretPicker() {
  if (secretSwap.value) settleSecretSwap(null);
  else if (secretRequest.value) settleSecretRequest(null);
  else secretPickerOpen.value = false;
}
// Directories of the editor: people for contacts and @ mentions, pages for [[ links (one each, shared by every instance).
// Without a page (the creation preview, the gallery) no people, as before the lexicon: such an editor asks the server nothing.
const { contacts, people, knownPeople, contactUsage } = createPeopleServices(() => props.pageId, () => (props.pageId ? props.spaceId : undefined));
// The place's lexicon for recognition in the text (names, page titles, YouTrack keys), on the same terms.
const placeLexicon = lexiconSource(() => (props.pageId ? props.spaceId : undefined));
type LiveEditor = import("@tiptap/core").Editor;
type JSONContent = import("@tiptap/core").JSONContent;
const liveEditor = () => editorSurface.value?.querySelector<HTMLElement & { editor?: LiveEditor }>(".ProseMirror")?.editor;
// The page as a place of its contacts (contactLinking.ts): links contacts without an id, asks at the last usage on „Odebrat“.
const contactHost: ContactHost = { place: () => (props.pageId ? { kind: "page", id: props.pageId } : undefined),
  count: id => { const editor = liveEditor(); return editor ? contactsIn(editor.state.doc, id) : 0; } };
// E4 (plan §19): with the capability `editorLinks` the `[[` list is refined by the section around the caret, which only the
// attached assist adapter reads (registerSection below); without assistance or the capability the order stays lexical.
const { capabilities: aiCapabilities } = useAiCapabilities();
const { canAnswer: assistantCanAnswer } = useAssistant();
let readSection: (() => string | null) | null = null;
function pageOrderSection(): PageOrderSection | null {
  const pageId = props.pageId;
  const context = pageId && aiCapabilities.value.editorLinks && readSection ? readSection() : null;
  return pageId && context ? { pageId, context } : null;
}
const pageLinks = createPageSource(() => props.pages ?? [], { spaceId: () => props.spaceId, pageId: () => props.pageId, order: pageOrderSection });
const youTrack = createYouTrackClient(() => props.spaceId);
// What a page of requirements reads as it opens (who changed each block, other pages' prerequisites) is the module's route, mapped only
// with the module on: without it every open asked and got a 404 (3.6 perf). Not offered then, the blocks read without it.
const modules = useModules();
const projectorReads: ProjectorHost["api"] = { ...projectorApi,
  get pageRequirements() { return modules.enabled("projector") ? projectorApi.pageRequirements : undefined; },
  get prerequisites() { return modules.enabled("projector") ? projectorApi.prerequisites : undefined; } };
const projector: ProjectorHost = { spaceId: () => props.spaceId, pageId: () => props.pageId, api: projectorReads, people, open: id => { location.hash = `#/pages/${encodeURIComponent(id)}`; },
  contact: id => contactApi.live(id).then(live => (live.state === "ok" ? { name: live.contact.name, photo: live.contact.photo } : undefined), () => undefined),
  parentId: () => props.pages?.find(page => page.id === props.pageId)?.parentId, pageTitle: () => props.documentTitle ?? "",
  titleOf: id => props.pages?.find(page => page.id === id)?.title, unsaved: () => props.unsaved?.() ?? false,
  isCatalog: () => props.catalog, setCatalog: isCatalog => emit("update:catalog", isCatalog), writable: () => props.collaborationAllowed === true,
  boardOffered: () => props.deploymentBoard === true };
// Page source („Zdroj“ tab): one detail at a time, so an OpenAPI detail never opens while the source view is there.
const sourceTab = ref(false);
/**
 * Content plugins as fresh instances: the live editor, the source session and the save projection each get their own.
 * The same list serves the page source, so references read and write as `::secret{…}`/`:secret{…}` there. Only
 * instances whose references are on screen (`shown`) attach their secrets plugin for refresh/conceal in place.
 */
function contentPlugins(shown = false) {
  // Asked when a person acts (documentWidgets.ts gateWrites): a readonly toggle reaches the same plugins, never new ones.
  return atlasContentPlugins({ kind: "edit", canWrite: () => !props.readonly, uploaderName: props.identity?.displayName, secrets, pickSecretReplacement: swapSecret, contacts, people, knownPeople, contactUsage, contactHost, pages: pageLinks, projector,
    openDetail: (context, target) => !sourceTab.value && detail.openDetail(context, target),
    upload: uploads.attachment, youTrack,
    headingAnchors: {
      collaborationReady: () => controller?.state.lifecycle === "ready" && controller.state.editable
        && (controller.state.connection === "local" || controller.state.connection === "online"),
      getHeadingUrl: headingUrl,
    },
    ...(shown ? { onSecretsPlugin: plugin => { secrets.attach(plugin); } } : {}) });
}
function headingUrl(id: string) {
  if (!props.pageId) throw new Error(shellMore.doc.noPermalink);
  const url = new URL(location.href);
  url.search = "";
  url.hash = `/pages/${encodeURIComponent(props.pageId)}?heading=${encodeURIComponent(id)}`;
  return url.href;
}
async function copySectionLink(href: string) {
  try { await navigator.clipboard.writeText(new URL(href, location.href).href); toast.success(t.toast.linkCopied); }
  catch { toast.error(t.toast.linkCopyFailed); }
}
/**
 * The reading bar's actions without AI (ReadonlySelectionActions: „Kde se to ještě zmiňuje“, „Kopírovat odkaz na sekci“) also while
 * editing (3.5.2 had them in reading): rows of the live editor's „Co s tím jde dělat“ (context-menu key, Shift+F10, right button,
 * Ctrl+Space) and, through `selectionActions`, of the palette; in the read-only editor from the DOM selection.
 */
function selectionActions(editor = props.readonly ? undefined : liveEditor()): MarkdownIntention[] {
  let text: string | null = null, href: string | undefined;
  if (editor && !editor.isDestroyed) {
    const { doc, selection } = editor.state;
    text = selection.empty ? null : doc.textBetween(selection.from, selection.to, " ", "").trim() || null;
    // The section the selection starts in, as the reading bar finds it: the last h2/h3 before it.
    let headingId: string | undefined;
    doc.forEach((node, offset) => { if (offset <= selection.from && node.type.name === "heading" && [2, 3].includes(node.attrs.level) && node.attrs.headingId) headingId = node.attrs.headingId; });
    href = headingId && props.pageId ? headingUrl(headingId) : undefined;
  } else {
    const rendered = projectRenderedSelection();
    text = rendered?.searchText ?? null;
    href = rendered?.sectionHref;
  }
  const rows: MarkdownIntention[] = [];
  if (text && !detectSecrets(text).length) rows.push({ id: "atlas.selection.find", label: shellMore.doc.findMentions, icon: "svg:search", keywords: ["hledat", "zmínky"], run: () => emit("findSelection", text) });
  if (href) rows.push({ id: "atlas.selection.section-link", label: shellMore.doc.copySectionLink, icon: "svg:link", keywords: ["odkaz", "sekce"], run: () => copySectionLink(href) });
  return rows;
}
const selectionActionsPlugin: MarkdownPlugin = { id: "atlas-selection-actions", apiVersion: 1, create: () => ({
  intentions: ({ editor }) => selectionActions(editor),
}) };
// The live editor only: `reveal` is host navigation (publication check), never part of reading or a projection.
const plugins = [presence.plugin, secretPicker, createRevealPlugin(), createHeadingFoldPlugin(() => props.pageId), selectionActionsPlugin, ...contentPlugins(true)];
// „Návrhy při psaní“ (plan §12 H.4): added per live editor instance, at once or when the adapter arrives, never to
// reading, the page source, save projections or the warm-up.
const toast = useToast();
const livePlugins = createLiveEditorPlugins(plugins, () => props.assist, {
  pageId: () => props.pageId,
  automaticSuggestionsEnabled: () => props.assist?.automaticSuggestionsEnabled?.() ?? true,
  modelSuggestionsEnabled: () => props.assist?.modelSuggestionsEnabled?.() ?? false,
  writing: {
    transformAllowed: () => aiCapabilities.value.editorTransform && !props.readonly,
    outlineAllowed: () => aiCapabilities.value.editorOutline && !props.readonly,
    intentAllowed: () => aiCapabilities.value.editorIntent && !props.readonly,
    title: () => props.documentTitle ?? '',
    onApplied: () => emit('aiAssistedApplied'),
  },
  ask: { available: () => assistantCanAnswer.value && !props.readonly, run: (intent, text, target) => askAssistant(intent, text, target) },
  contacts,
  pages: pageLinks,
  resolveIssue: youTrack.resolve,
  hasSecrets: async (query, signal) => (await secretsApi.search(query, props.spaceId, signal)).length > 0,
  pickSecret,
  notify: (message, undo) => toast.success(message, { undo }),
  warn: message => toast.error(message),
  reducedMotion: () => prefersReducedMotion(editorSurface.value),
  editorLinks: () => aiCapabilities.value.editorLinks,
  registerSection(read) {
    readSection = read;
    return () => { if (readSection === read) readSection = null; };
  },
}, () => !!props.assistExpected);
const releaseLivePlugins = (vnode: { readonly key: PropertyKey | null }) => livePlugins.release(String(vnode.key));
// The assist chunk comes in idle time, after the live editor opened: that editor attaches it in place (liveEditorPlugins.ts).
watch(() => props.assist, assist => { if (assist) livePlugins.arrived(assist); });
// One block-insertion model for both editor instances: the "+" beside the drag handle and the between-blocks
// inserter. Both only appear while the editor is editable.
const blockInsertion = { showBlockAddButton: true, showBlockInserter: true } as const;
// One outline configuration for both editor instances. The page's outline lives in the page dock („Obsah“) and in the
// „Na této stránce“ row (PageOutline, refresh 2.5), so the editor's own floating rail stays hidden.
const outline = editorOutline;
// Markdown pasted or dropped into the page: fill an empty title, resolve wiki links against the loaded
// space tree and embeds against files of this page.
const importHooks = createMarkdownImportHooks({
  title: () => props.documentTitle ?? "",
  setTitle: title => emit("importTitle", title),
  pages: () => props.pages ?? [],
  files: () => documentFiles(currentValue()),
});

// Page source („Zdroj“ view, docs/page-source-editor.md): admins replace the page from its
// protected Markdown. It belongs to editing: the Dokument | Zdroj switch shows only in the editable document,
// and ending the editing closes the view. The draft never writes through the live controller; the host saves
// it on the server (new working revision) and remounts the document. Monaco loads only with the tab.
interface SourceConfirmation { apply: boolean; resolve?: (resolved: boolean) => void; then?: () => void }
const sourceConfirmation = shallowRef<SourceConfirmation>();
/** The Dokument | Zdroj switch stands wherever the page can be written, so attaching the editor never pushes the page down (3.6-A). */
const sourceOffered = computed(() => !!props.canEditSource && !!props.saveSource && !failure.value);
/** The tab itself opens over the attached editor only. */
const sourceAllowed = computed(() => !props.readonly && !!props.canEdit && sourceOffered.value);
let sourcePlugins: ReturnType<typeof contentPlugins> | undefined;
const sourceDraft = usePageSourceDraft({
  // The live document: local edits, remote collaborative edits and a conflict refresh all land here.
  value: () => value.value,
  plugins: () => (sourcePlugins ??= contentPlugins()),
  readonly: () => !sourceAllowed.value,
  async save(next, _text, { signal }) {
    let markdown: string;
    try { markdown = markdownProjection(next, contentPlugins()); }
    catch (error) { return { ok: false, reason: "error", message: errorMessage(error) }; }
    const result = await props.saveSource!(next, markdown, signal);
    return result.ok ? { ok: true } : { ok: false, reason: result.reason, message: result.message };
  },
});
const sourceDirty = computed(() => sourceDraft.dirty.value);
// The raised segment of the switch is one thumb that glides to the chosen view, like the other segmented switches (segmentedThumb.ts).
const sourceSwitch = ref<HTMLElement>();
const sourceThumb = useSegmentedThumb(sourceSwitch, '[aria-pressed="true"]', sourceTab);
/** The document rises in only when it comes back from the source, never when the page opens; off again once it has (see the pane). */
const sourceVisited = ref(false);
// The source reaches from where its pane starts down to the bottom of the window. The page's header above it has no fixed
// height and the page's column does not stretch, so the pane is measured: when it appears and when the window is resized.
const sourcePane = ref<HTMLElement>();
const sourceHeight = ref<string>();
function fitSource() {
  const pane = sourcePane.value, view = pane?.ownerDocument.defaultView;
  if (!pane || !view) return;
  // On a phone the page's bar (PageMobileBar) stands at the bottom of the window: the source ends above it.
  const bar = pane.ownerDocument.querySelector<HTMLElement>(".page-mobile-bar");
  const bottom = bar?.offsetHeight ? Math.min(view.innerHeight, bar.getBoundingClientRect().top) : view.innerHeight;
  sourceHeight.value = `${Math.floor(bottom - pane.getBoundingClientRect().top)}px`;
}
watch(sourcePane, (pane, _previous, onCleanup) => {
  const view = pane?.ownerDocument.defaultView;
  if (!view) return;
  fitSource();
  view.addEventListener("resize", fitSource);
  onCleanup(() => view.removeEventListener("resize", fitSource));
}, { flush: "post" });
/** The source holds an unsaved draft (or a save is in flight): the host's source checkpoint waits for it. */
const sourcePending = computed(() => sourceTab.value && (sourceDirty.value || sourceDraft.busy.value));
watch(sourceDirty, dirty => emit("sourceDraft", dirty));
function settleSourceConfirmation(resolved: boolean) {
  const pending = sourceConfirmation.value;
  sourceConfirmation.value = undefined;
  pending?.resolve?.(resolved);
}
/** Opens the „Zdroj“ tab over the live value while editing. The host flushes the author's own live edits first. */
function openSource() {
  if (!sourceAllowed.value) return false;
  currentValue();
  // One detail at a time: the host already resolved an OpenAPI YAML draft.
  if (detail.open.value) detail.open.value = false;
  detail.dispose();
  sourceTab.value = true;
  sourceVisited.value = true;
  return true;
}
/** „Dokument“ in the switch: back to the page, asking first about an unsaved draft. */
function requestSourceClose() {
  if (!sourceTab.value || sourceConfirmation.value) return;
  if (sourceDirty.value) sourceConfirmation.value = { apply: true, then: closeSource };
  else closeSource();
}
function closeSource() {
  if (!sourceTab.value) return;
  sourceTab.value = false;
  sourceDraft.close();
  // The caret goes back into the text, where it was, unless the reader already went elsewhere in the app.
  void nextTick(() => {
    const surface = editorSurface.value, active = surface?.ownerDocument.activeElement;
    if (!active || active === surface?.ownerDocument.body || surface?.contains(active)) controller?.focus();
  });
}
/**
 * Esc on the switch or in the source's own controls is „Dokument“, unless a dialog has the key. The source editor prevents the
 * default of every Esc and reports the ones it did not use itself through the panel's `escape`.
 */
function sourceEscape(event: KeyboardEvent) {
  if (!sourceTab.value || event.defaultPrevented || (event.target as Element | null)?.closest?.("dialog")) return;
  event.preventDefault(); event.stopPropagation();
  requestSourceClose();
}
async function chooseSource(action: "continue" | "discard" | "apply") {
  const pending = sourceConfirmation.value;
  if (!pending) return;
  if (action === "continue") { settleSourceConfirmation(false); return; }
  if (action === "apply") {
    if (!pending.apply || !(await sourceDraft.apply())) { settleSourceConfirmation(false); return; }
  } else sourceDraft.discard();
  settleSourceConfirmation(true);
  pending.then?.();
}
/**
 * Host guard before saving, publishing or exporting the page: resolves true when no unsaved source draft
 * remains (none or discarded) and false when the reader keeps editing it. It offers no Apply: applying
 * replaces the page, which is not what the page operation meant.
 */
function resolveSourceDraft(): Promise<boolean> {
  if (!sourceDirty.value) return Promise.resolve(true);
  if (sourceConfirmation.value) return Promise.resolve(false);
  return new Promise(resolve => { sourceConfirmation.value = { apply: false, resolve }; });
}
/** The reader already confirmed discarding all unsaved work (leaving the page, restore, ending editing). */
function discardSourceDraft() {
  settleSourceConfirmation(false);
  sourceDraft.discard();
  closeSource();
}
// Ending the editing, losing the permission or the editor failing ends the source view at once; the draft cannot
// be saved any shellMore (the host already asked before discarding it).
watch(sourceAllowed, allowed => { if (!allowed && sourceTab.value) { settleSourceConfirmation(false); closeSource(); } });
function publishStatus() {
  const state = controller?.state;
  presence.update(sessionRequested.value && !joining.value && !joinError.value && state?.connection === "online"
    ? state.peers ?? [] : []);
  // One undo query per publication (a can() chain), sent only when the answer changes.
  const canUndo = !props.readonly && !!state?.editable && !!controller?.query(undoCommand).enabled;
  if (canUndo !== emittedCanUndo) emit("canUndo", emittedCanUndo = canUndo);
  emit("collaborationStatus", {
    enabled: sessionRequested.value,
    generation: lease.value?.generation ?? null,
    canSave: !joining.value && !joinFailure.value && !!adapter.value && !!state && collaborationCanSave(state),
    checkpointReady: !joining.value && !joinFailure.value && !!adapter.value && !!state && collaborationCheckpointReady(state),
    sourceCheckpointReady: sourceAllowed.value && sourcePending.value && !joining.value && !joinFailure.value
      && !!adapter.value && !!state && collaborationCheckpointReady(state),
    connection: joining.value ? "connecting" : joinError.value ? "failed" : (state?.connection ?? "local"),
    pendingChanges: state?.pendingChanges ?? 0,
    error: joinFailure.value ?? state?.error ?? null,
    projectionSave: state?.projectionSave ?? "local",
    currentVersion: lease.value?.currentVersion,
  });
  // The editor renders no toolbar of its own (toolbar-presentation="hidden"); the wiki
  // lists these in its page menu, so there is one row of chrome above the document.
  const live = controller;
  const actions = live && state?.editable ? (live.actions?.toolbar ?? []).filter((action) => !action.variants || action.variants.includes("document")) : [];
  // Every keystroke publishes a state: the menu's list is sent again only when it changes, and whether an action can run is asked
  // when the menu reads it (a query of every action per keystroke cost ~10 ms on a deployment page, perf review 2026-11-02).
  const key = actions.map((action) => action.id).join();
  if (live === emittedActions.controller && key === emittedActions.key) return;
  emittedActions = { controller: live, key };
  emit("pluginActions", actions.map((action) => ({
    id: action.id, label: action.label, description: action.description, ...(action.group ? { group: action.group } : {}),
    icon: action.icon?.startsWith("svg:") ? action.icon.slice(4) : null,
    get enabled() { return live!.query(action.command).enabled; },
    run: () => live!.dispatch(action.command),
  })));
}
let emittedActions: { controller?: MarkdownEditorController<"rich">; key?: string } = {};
const undoCommand = { type: "undo" } as const;
let emittedCanUndo: boolean | undefined; // the first publication always tells the host (a new editor of another page)
function preserveValue() {
  if (controller?.state.lifecycle === 'ready') {
    try {
      value.value = controller.getValue();
      valueStale = false;
    } catch {
      /* Keep the most recent complete value for local export. */
    }
  }
}
/**
 * The live document: the collaborative editor reports only that it changed (`mode="revision"`), since reading its whole value
 * on every keystroke cost ~30 ms on a 2 MB page (UX ED-15). Readers read it here, on demand.
 */
let valueStale = false;
function currentValue() {
  if (valueStale) preserveValue();
  return value.value;
}
function detach() {
  changeHeld = false;
  presence.update([]);
  preserveValue();
  unsubscribe?.();
  unsubscribe = undefined;
  controller = undefined;
  adapter.value = undefined;
  lease.value = undefined;
}
function reloadPage() { window.location.reload(); }
/**
 * A tab of the previous release loads the new one by itself, when nothing typed would be lost by it (`safe`: no change still on its
 * way to the server; no unsent text, no component draft). Once a minute at most, so a reload that does not help (a cached shell)
 * ends on the notice and its button instead of a loop.
 */
function reloadStale(safe = true) {
  if (!safe || unsent || sourceDirty.value || detail.dirty.value) return;
  let last = 0;
  try { last = Number(sessionStorage.getItem("atlas.staleEditorReload")); sessionStorage.setItem("atlas.staleEditorReload", String(Date.now())); } catch { last = Date.now(); }
  if (Date.now() - last > 60_000) reloadPage();
}
/** The bundle's state opens the first session of this editor only; a reconnect or a new generation joins through the server. */
let bundleUsed = false;
/** A session that was online joins again once when it ends because the page moved to a new generation (a save, a restore, a source
 * replace, a stale live state reset on open), and nothing else would tell it: a live page's reader whatever ended it, a writer on
 * `[SUPERSEDED]` or `[FORBIDDEN]` (the hub refuses an old generation like a lost right). A session that ends before it was online does
 * not, so a refused join never loops; a reader then waits for PageChanged. */
let rejoins = false;
/** The failure that ended a writer's session while it joins again quietly: shown as before when the join is refused too (a real loss of
 * access, the trash), so the page is read again (App.vue). */
let rejoinOf: MarkdownEditorState["error"] = null;
async function configure() {
  const request = ++attempt;
  joinFailure.value = null;
  const ended = rejoinOf;
  rejoinOf = null;
  if (!sessionRequested.value) {
    joining.value = false;
    if (adapter.value) detach();
    publishStatus();
    return;
  }
  joining.value = true;
  publishStatus();
  try {
    if (!props.pageId || !props.identity)
      throw new Error(
        t.doc.collabMissing,
      );
    const bundled = !bundleUsed && props.collaboration ? props.collaboration : undefined;
    bundleUsed = true;
    bundleOpening.value = !!bundled;
    const opened = bundled && await openBundledCollaboration(bundled, props.identity);
    // Without the bundle's state the editor joins as without a bundle: the page's own value shows meanwhile, not the skeleton.
    if (!opened) bundleOpening.value = false;
    const result = opened || await joinPageCollaboration(props.pageId, props.identity, readingLive.value);
    if (disposed || request !== attempt) return;
    // Without a controller the page's own value is the baseline.
    collaborationBaseline = controller
      ? captureCollaborationBaseline(controller, props.value)
      : value.value;
    detach();
    lease.value = result.lease;
    adapter.value = result.adapter;
    // Built from the bundle's state the editor holds the document as it mounts: no local stand-in beside it.
    if (opened) hydrated.value = true;
    if (ended) emit("reopened");
  } catch (error) {
    // A live page's reader keeps the page's own value whatever the join says; the next change of the page tries again.
    if (!disposed && request === attempt && readingLive.value) liveIdle.value = true;
    else if (!disposed && request === attempt) {
      joinFailure.value = ended ?? { kind: "collaboration", message: errorMessage(error), recoverable: true };
      // The join was refused before a session began (a page opened during the deploy): nothing was typed into it.
      if (schemaMismatch.value) reloadStale();
    }
  } finally {
    if (!disposed && request === attempt) {
      joining.value = false;
      bundleOpening.value = false;
      publishStatus();
    }
  }
}
// The session follows whether one is asked for, not `readonly` itself: locking and unlocking only flip the editor's `editable`.
watch(
  [
    () => sessionRequested.value,
    () => props.pageId,
    () => props.identity?.id,
  ],
  configure,
  { immediate: true },
);
function observe(instance: MarkdownEditorController<"rich">) {
  if (controller === instance) return;
  unsubscribe?.();
  controller = instance;
  const sessionCheck = collaborationSessionCheck();
  unsubscribe = instance.subscribe(() => {
    const state = instance.state;
    sessionCheck(state.connection);
    if (state.lifecycle === 'failed' && state.error) providerFailed(state.error);
    else { publishStatus(); settleHydration(state); trackUnsent(state); }
  });
  if (instance.state.lifecycle === 'failed' && instance.state.error) providerFailed(instance.state.error);
  else { publishStatus(); settleHydration(instance.state); trackUnsent(instance.state); }
}
/** The shared document as the server acknowledged it last (a ProseMirror node, kept by reference), and what a session that ended
 * (`[SUPERSEDED]`) left unsent: put back into the next session as text once it can be written (reapplyUnsent). */
let acknowledged: { toJSON(): JSONContent } | undefined;
let unsent: { acknowledged: JSONContent; local: JSONContent } | undefined;
function trackUnsent(state: MarkdownEditorState) {
  if (state.binding !== 'collaborative' || state.lifecycle !== 'ready') return;
  if (unsent && state.editable) {
    const pending = unsent;
    unsent = undefined;
    void nextTick(() => {
      const editor = liveEditor();
      const merged = editor && !editor.isDestroyed && editor.isEditable ? reapplyUnsent(pending.acknowledged, pending.local, editor.state.doc.toJSON()) : null;
      if (merged) editor!.commands.setContent(merged, { emitUpdate: true });
    });
  }
  if (state.pendingChanges === 0) acknowledged = liveEditor()?.state.doc;
}
/** The shared editor holds the document once it is ready: the local stand-in goes. */
function settleHydration(state: MarkdownEditorState) {
  if (state.lifecycle === 'ready' && state.binding === 'collaborative') hydrated.value = true;
  if (state.connection === 'online') rejoins = true;
}
const collaborativeEditor = shallowRef<{ controller: MarkdownEditorController<'rich'> }>();
watch(collaborativeEditor, instance => { if (instance) observe(instance.controller); }, { flush: 'post' });
function created(instance: MarkdownEditorController<"rich">) {
  observe(instance);
  publishStatus();
  emit("ready");
  // After the render that sets which editor shows (the shared one built hidden for the hand-over plays nothing).
  void nextTick(playReveal);
  // Hydration can recover acknowledged shared edits that have not yet become a
  // wiki revision. Make that checkpoint available even before another keystroke.
  if (instance.state.binding === "collaborative") {
    const baseline = collaborationBaseline ?? props.value;
    // Assigning would redraw a local stand-in still on screen: marked stale, readers take the editor's value on demand (currentValue).
    valueStale = true;
    // The comparison projects both documents to Markdown (about a second for a 1 MB page) and only decides
    // whether the page counts as changed, so it runs once the editor is usable and the user rests, each side's
    // projection in its own idle period. A change emitted meanwhile has already marked the page changed.
    const changesBefore = changesEmitted;
    // The held value is read once rested too (no change came meanwhile, so it is the one the editor was built with): read as the
    // editor is created, it cost a walk of the whole document before the page could paint (3.6-G open).
    let comparison: ReturnType<typeof hydrationChanges> | undefined;
    const step = () => whenRested(() => {
      if (disposed || controller !== instance || changesEmitted !== changesBefore) return;
      comparison ??= hydrationChanges(baseline, instance.getValue(), contentPlugins);
      const next = comparison.next();
      if (!next.done) step();
      else if (next.value) countChange();
    });
    step();
  }
}
let changesEmitted = 0;
// A change while the editor is read-only (a hydration that recovered shared edits, a repair) counts once the page can be written.
let changeHeld = false;
function countChange() { if (props.readonly) changeHeld = true; else emit("change"); }
watch(() => props.readonly, readonly => {
  if (readonly || !controller) return;
  if (changeHeld) { changeHeld = false; emit("change"); }
  publishStatus();
});
function changed(next: MarkdownRichValue) {
  value.value = next;
  changesEmitted++;
  emit("change");
}
function contentChanged() {
  valueStale = true;
  changesEmitted++;
  // The open „Zdroj“ follows the live page (a collaborator's edit marks its draft stale), so it reads at once.
  if (sourceTab.value) preserveValue();
  countChange();
}
function providerFailed(error: NonNullable<MarkdownEditorState["error"]>) {
  // A reader's session ended (a new generation, the writers left, `[READ_ONLY] … initialize`): back to the page's own value, which a
  // new GET joins again at once after an online session (rejoins), otherwise on the next change of the page.
  const rejoin = rejoins;
  rejoins = false;
  if (readingLive.value) {
    detach(); joining.value = false;
    if (rejoin) void configure(); else liveIdle.value = true;
    publishStatus();
    return;
  }
  // What was typed and never reached the server stays: on screen in the stand-in, and put back when the user joins again.
  const replaced = /\[(SUPERSEDED|FORBIDDEN)\]/.test(error.message);
  const pending = controller?.state.pendingChanges ?? 0;
  const editor = liveEditor();
  if (replaced && acknowledged && editor && (controller?.state.pendingChanges ?? 0) > 0) {
    unsent = { acknowledged: acknowledged.toJSON(), local: editor.state.doc.toJSON() };
    try { value.value = controller!.getValue(); valueStale = false; } catch { /* the stand-in keeps the last value */ }
  }
  acknowledged = undefined;
  detach();
  joining.value = false;
  // A new generation (a colleague restored a version, replaced the source) opens by itself; the host names who did it (`reopened`).
  if (replaced && rejoin) { rejoinOf = error; void configure(); return; }
  joinFailure.value = error;
  publishStatus();
  // The server of the new release refused a session that was open over the deploy: with every change acknowledged, load the new editor.
  if (schemaMismatch.value) reloadStale(!pending);
}
/** `checkpoint`: what a save sends. A live page's is its generation alone: the server saves its own projection of the shared log
 * (SavePageHandler), so a save does not serialize the whole page here (about 200 ms of main thread per save on a 750 KB page). */
type Captured = { contentJson: string; markdown: string; collaborationGeneration?: string };
function capture(): Captured;
function capture(options: { checkpoint: boolean }): Partial<Captured>;
function capture({ checkpoint = false } = {}): Partial<Captured> {
  if (failure.value) throw new Error(failure.value);
  if (checkpoint && controller && lease.value) return { collaborationGeneration: lease.value.generation };
  // No editor yet: project the page's value the way a save does (export). Only on demand, so opening a page never pays for it.
  if (!controller) return { contentJson: JSON.stringify(value.value), markdown: markdownProjection(value.value, contentPlugins()) };
  return {
    ...captureDocument(controller),
    ...(lease.value ? { collaborationGeneration: lease.value.generation } : {}),
  };
}
onErrorCaptured((error) => {
  failure.value = errorMessage(error);
  // Nothing is open yet, so nothing is lost: load the new release at once.
  if (staleEditor.value) reloadStale();
  return false;
});
// See the module-scope comment above: `codeBlock` is baked into the controller at
// creation time. An instance that mounts before the shared extension resolves would
// otherwise be stuck without it for its whole lifetime, so re-key (and so recreate) the
// controller exactly once when the extension arrives late. Instances that mount after
// it has already resolved never see this fire.
watch(sharedCodeBlock, (value, previous) => {
  if (value && !previous) codeBlockGeneration.value++;
});
onBeforeUnmount(() => {
  if (detail.dirty.value) emit("detailDraft", false);
  if (sourceDirty.value) emit("sourceDraft", false);
  settleSourceConfirmation(false);
  detail.dispose();
});
// The live page's reader waits for the page to change while it has no session to join (3.6-I: PageChanged reaches its readers).
let liveWatch: { close(): void } | undefined;
watch(() => (readingLive.value && liveIdle.value ? props.pageId : undefined), pageId => {
  liveWatch?.close();
  liveWatch = undefined;
  if (!pageId) return;
  const channel = `page:${pageId.toLowerCase()}`, live = liveConnection();
  live.route("PageChanged", payload => (payload && typeof payload === "object" && typeof (payload as { pageId?: unknown }).pageId === "string"
    ? `page:${(payload as { pageId: string }).pageId.toLowerCase()}` : undefined));
  liveWatch = live.subscribe(channel, { message: event => { if (event === "PageChanged") liveIdle.value = false; } });
}, { immediate: true });
watch(() => props.pageId, () => { liveIdle.value = false; });
onUnmounted(() => {
  liveWatch?.close();
  disposed = true;
  attempt++;
  unsubscribe?.();
  secrets.dispose();
});
const editorSurface = ref<HTMLElement>();
/** The selection in the read-only editor, as the reading bar takes it. */
function projectRenderedSelection() {
  const surface = editorSurface.value;
  const selection = surface?.ownerDocument.getSelection();
  const root = surface?.querySelector<HTMLElement>('.ProseMirror');
  if (!root || !selection?.rangeCount) return null;
  const range = selection.getRangeAt(0);
  const projected = projectReadonlySelection(root, range);
  if (!projected) return null;
  let heading: HTMLElement | undefined;
  for (const candidate of root.querySelectorAll<HTMLElement>('h2[id],h3[id]')) {
    if (candidate.contains(range.startContainer) || candidate.compareDocumentPosition(range.startContainer) & Node.DOCUMENT_POSITION_FOLLOWING) heading = candidate;
    else break;
  }
  const sectionHref = heading && props.pageId ? `#/pages/${encodeURIComponent(props.pageId)}?heading=${encodeURIComponent(heading.id)}` : undefined;
  return { ...projected, range, sectionHref };
}
// The host's reading bar (App.vue) takes a reader's selection: the editor's own reader bubble stays off, one bar for readers.
const readerBar = computed(() => !!props.readonly && !!props.readonlySelectionEnabled);
function readSelection() {
  const rendered = readerBar.value ? projectRenderedSelection() : null;
  if (!rendered) { emit('readonlySelection', null); return; }
  const { range, sectionHref, ...projected } = rendered;
  emit('readonlySelection', { ...projected, anchorRect: range.getBoundingClientRect(), ...(sectionHref ? { sectionHref } : {}) });
}
onMounted(() => editorSurface.value?.ownerDocument.addEventListener('selectionchange', readSelection));
onBeforeUnmount(() => { editorSurface.value?.ownerDocument.removeEventListener('selectionchange', readSelection); emit('readonlySelection', null); });
watch([() => props.readonly, () => props.readonlySelectionEnabled, () => props.pageId], readSelection);

// Objects of the page drag out into the space tree as reference nodes (pageObjectDrag.ts), reading and editing alike. A contact
// card becomes draggable on the press that may start the drag, and its drag never reaches ProseMirror (which would move an empty
// selection); a requirement block stays ProseMirror's to move within the page and gets the object payload after it.
function contactSnapshots(card: Element): ContactSnapshots | undefined {
  const view = liveEditor()?.view;
  const root = card.closest(".md-contact-list, .md-contact-inline");
  if (!view || !root) return undefined;
  try {
    const at = view.posAtDOM(root, 0);
    for (const position of [at, at - 1]) {
      const node = position >= 0 ? view.state.doc.nodeAt(position) : null;
      if (node?.type.name === "contactCard" || node?.type.name === "contactInline") return node.attrs.data as ContactSnapshots;
    }
  } catch { /* a card outside the document (a preview) drags nothing */ }
  return undefined;
}
// A contact without an id is linked on the press (spec 2026-10-30 §8) and drags once it has one; the node gets it from the plugin's link.
const linkedCards = new WeakMap<Element, string | null>();
function armContactDrag(event: PointerEvent) {
  const card = event.target instanceof Element ? event.target.closest<HTMLElement>(".md-contact-card") : null;
  if (!card || card.draggable) return;
  const data = contactSnapshots(card), contact = contactAt(card, data);
  if (contactObject(card, data)) { card.draggable = true; return; }
  // Linking may create a directory contact: a writer's act, so a reader's press on an unlinked card drags nothing.
  if (!contact?.name || linkedCards.has(card) || props.readonly || !liveEditor()?.isEditable) return;
  linkedCards.set(card, null);
  void linkContact(contact as MarkdownContact).then(linked => {
    if (!linked?.objectId) return;
    linkedCards.set(card, linked.objectId);
    card.draggable = true;
  }, () => linkedCards.delete(card));
}
let draggingContacts = false;
function startContactDrag(event: DragEvent) {
  const card = event.target instanceof Element ? event.target.closest(".md-contact-card") : null;
  const data = card ? contactSnapshots(card) : undefined;
  const object = card ? contactObject(card, data, linkedCards.get(card) ?? undefined) : undefined;
  if (!object || !event.dataTransfer) return;
  event.stopPropagation();
  // A selected contact block drags all of its contacts: several cards on a board.
  const selected = (liveEditor()?.state.selection as { node?: { attrs: { data?: unknown } } } | undefined)?.node?.attrs.data === data;
  const all = selected ? contactObjects(data) : [];
  draggingContacts = true;
  card!.addEventListener("dragend", () => { draggingContacts = false; }, { once: true });
  startObjectDrag(event, all.length > 1 ? all : object, { effect: "copy", bare: true });
}
/** Contacts dropped from a board or a list (spec 2026-10-30 §8): a contact block each, after the block they fell on, by its `objectId`. */
async function dropContacts(event: DragEvent) {
  const editor = liveEditor();
  if (draggingContacts || !editor?.isEditable || !event.dataTransfer?.types.includes(atlasObjectDragType)) return;
  const objects = readObjectsPayload(event.dataTransfer.getData(atlasObjectDragType)).filter(object => object.typeId === "atlas.contact");
  if (!objects.length) return;
  event.preventDefault();
  event.stopPropagation();
  const dropped = editor.view.posAtCoords({ left: event.clientX, top: event.clientY })?.pos ?? editor.state.selection.to;
  const $at = editor.state.doc.resolve(dropped);
  let at = $at.depth ? $at.after(1) : dropped;
  const follow = ({ transaction }: { transaction: { mapping: { map(position: number): number } } }) => { at = transaction.mapping.map(at); };
  editor.on("transaction", follow);
  try {
    const contacts = await Promise.all(objects.map(async object => {
      const live = await contactApi.live(object.id).catch(() => null);
      return normalizeContact(live?.state === "ok" ? { ...live.contact, objectId: live.contact.objectId ?? object.id } : { name: object.title || "Kontakt", objectId: object.id });
    }));
    if (editor.isDestroyed) return;
    if (objects.some(object => contactsIn(editor.state.doc, object.id))) toast.warning(repeatedContact());
    editor.chain().focus().insertContentAt(at, contacts.map(contact => ({ type: "contactCard", attrs: { data: { version: 1, contacts: [contact] } } }))).run();
  } finally {
    editor.off("transaction", follow);
  }
}

const takingInput = (target: EventTarget | null) => target instanceof Element && !!target.closest('[contenteditable="true"]');
const inDocument = (target: EventTarget | null) => target instanceof Element && !!target.closest(".ProseMirror");
/** The writer's own text (a link mark), not a chip in it or a read-only page: the nearest `contenteditable` says which. */
const inWrittenText = (link: Element) => link.closest("[contenteditable]")?.getAttribute("contenteditable") === "true";
const wikiAddress = (url: URL) => url.origin === location.origin && url.pathname === location.pathname && url.hash.startsWith("#/");
/**
 * A link in the page opens on a plain click, for a reader and on a chip, and a link to a wiki page opens in this window (3.6-G N1: a new
 * tab was a cold start of the whole app, 0.5 s to 12 s). Ctrl/Cmd, Shift and the middle button keep what the browser does with them.
 * In the text being written a plain click only puts the caret into the link (4.0): Ctrl/Cmd+click, the middle button and the link's
 * bar open it.
 */
function followLink(event: MouseEvent) {
  const link = event.target instanceof Element ? event.target.closest<HTMLAnchorElement>("a[href]") : null;
  if (!link || event.defaultPrevented || !inDocument(link)) return false;
  const url = new URL(link.href, location.href);
  if (inWrittenText(link) ? event.button !== 1 && !(event.button === 0 && (event.ctrlKey || event.metaKey)) : event.button !== 0) return false;
  if (!inWrittenText(link) && wikiAddress(url) && !event.ctrlKey && !event.metaKey && !event.shiftKey && !event.altKey) { event.preventDefault(); location.hash = url.hash; return true; }
  // In the editor's text a link is no link to the browser (contenteditable): it opens as the static page's does, in a new tab.
  if (takingInput(link)) { event.preventDefault(); window.open(url.href, "_blank", "noopener,noreferrer"); return true; }
  return false;
}
/** The bar of the link under the caret (or its row in „Co s tím jde dělat“) opens it: a wiki page in this window, anything else in a new tab. */
function followAsked(event: Event) {
  const url = new URL((event as CustomEvent<{ href: string }>).detail.href, location.href);
  if (!wikiAddress(url)) return;
  event.preventDefault(); location.hash = url.hash;
}
/** A key, a paste or a drop into the read-only text: an attempt to write the host may answer (a locked page's lock pulses). */
function keyIntent(event: KeyboardEvent) {
  if (!props.readonly || event.defaultPrevented || event.isComposing || event.ctrlKey || event.metaKey || event.altKey || !inDocument(event.target)) return;
  // Space scrolls a read-only page; the arrows and Tab move.
  if ((event.key.length === 1 && event.key !== " ") || ["Enter", "Backspace", "Delete"].includes(event.key)) emit("editIntent");
}
function inputIntent(event: Event) { if (props.readonly && inDocument(event.target)) emit("editIntent"); }
/** A pointer is putting the caret into the text this moment: where it lands is the browser's to decide. */
let pointerFocus = false;
function notePointer() {
  pointerFocus = true;
  setTimeout(() => { pointerFocus = false; });
}
/**
 * Focus given back to the live editor by a closing dialog or a script (Esc from the palette) leaves the browser's caret where it was,
 * and ProseMirror puts its own selection back only 20 ms later: a key typed meanwhile landed at the start of the line (SE3-5).
 */
function selectionOnFocus(event: FocusEvent) {
  const view = liveEditor()?.view;
  if (pointerFocus || !view || event.target !== view.dom || !view.editable) return;
  view.focus();
}
/** The caret at the start of the first top-level block whose bottom is below the workspace bar (or the window's top). */
function focusVisibleBlock() {
  const dom = editorSurface.value?.querySelector<HTMLElement & { editor?: import("@tiptap/core").Editor }>(".ProseMirror");
  const editor = dom?.editor;
  if (!dom || !editor || editor.isDestroyed || !editor.isEditable) return false;
  // The caret takes its current place at once (after a lock, where it was) and moves to the first block on screen after the next
  // paint: measuring now forced a layout of the whole page just opened or unlocked (up to 0.7 s at 4× CPU). A caret moved meanwhile stays.
  editor.view.focus();
  const placed = editor.view.state.selection;
  afterPaint(() => {
    if (editor.isDestroyed || !editor.isEditable || !editor.view.state.selection.eq(placed)) return;
    const block = firstVisibleBlock(editor.view);
    if (!block) return;
    revealLazy(editor.view.nodeDOM(block.from) as Element | null);
    try {
      // The block may hold no inline content of its own (a requirement holds its title paragraph, C3-11): the nearest place for a
      // caret forward from its start.
      const { state } = editor.view;
      // A component (an image, a chart) is never selected: the next key would replace it (UX ED-17); text after it takes the caret.
      const at = state.doc.resolve(block.from);
      editor.view.dispatch(state.tr.setSelection(TextSelection.findFrom(at, 1, true) ?? Selection.near(at, 1)));
    } catch { /* the caret stays where it is */ }
  });
  return true;
}
/** A heading's id for `id`: its own, else the heading's whose former ids hold it (a converted requirement block's id or key, its aliases). */
function headingFor(root: HTMLElement, id: string) {
  return [...root.querySelectorAll<HTMLElement>("[data-heading-aliases]")].find(element => element.dataset.headingAliases!.split(",").includes(id))?.dataset.headingId ?? id;
}
/** The page anchor (`?heading=`): a heading by its id, or by a former id (an old requirement block's link, the deployment overview's). */
function navigateToHeading(anchor: string) {
  const root = editorSurface.value;
  if (!root) return false;
  const id = headingFor(root, anchor);
  // A folded section (headingFold.ts) opens before the page goes there.
  const heading = root.querySelector(`[data-heading-id="${CSS.escape(id)}"]`);
  if (heading) { unfoldTo(heading); revealLazy(heading); }
  return navigateHeading(root, id);
}
/** `?block=` (an old requirement block's bookmark): its heading by its former id, once the page has rendered it, kept in view while the page lays out (U4-2). */
function revealBlock(id: string) {
  const root = editorSurface.value;
  if (!root) return;
  revealSettled(() => root.querySelector<HTMLElement>(`[data-heading-id="${CSS.escape(headingFor(root, id))}"]`) ?? undefined, block => {
    if (!block.hasAttribute("tabindex")) block.tabIndex = -1;
    block.focus({ preventScroll: true });
    glow(block);
  });
}
/** Selects a place of the live document, scrolls to it, lets it glow and focuses the editor; `false` when it is gone. */
function reveal(target: RevealTarget) {
  const live = controller;
  // The command clones its payload (structuredClone), which refuses a reactive proxy: the findings of the panel are one.
  if (!live || !live.dispatch(revealCommand(JSON.parse(JSON.stringify(target)) as RevealTarget))) return false;
  live.focus();
  return true;
}
defineExpose({
  /** „Kde se to ještě zmiňuje“ and „Kopírovat odkaz na sekci“ for the current selection, for the palette. */
  selectionActions: () => selectionActions(),
  capture, navigateToHeading, revealBlock, reveal, navigateToPeer: (clientId: number) => presence.navigateToPeer(clientId),
  /**
   * Puts the caret into the document without scrolling; `false` while it cannot take it (read-only, or the shared session still seeding).
   * Where its selection is, or with `visible` at the start of the first block the reader sees (W2-4).
   */
  focus: (at?: "visible") => !props.readonly && (!adapter.value || hydrated.value)
    && (at === "visible" && focusVisibleBlock() || !!controller?.focus()),
  /** What Ctrl+Z does (the page's „Zpět“); the caret stays in the text. */
  undo: () => { if (controller?.dispatch(undoCommand)) controller.focus(); },
  /** Apply / Discard / Continue for an unapplied YAML draft; `true` when none remains. */
  resolveDetailDraft: () => detail.resolveDraft(),
  /** Drops the YAML draft after the author confirmed discarding all unsaved work. */
  discardDetailDraft: () => detail.discardDraft(),
  /** Opens the „Zdroj“ tab; `false` when not editing, or the source editor is unavailable or not allowed. */
  openSource,
  /** Discard / Continue for an unsaved page source draft; `true` when none remains. */
  resolveSourceDraft,
  /** Drops the page source draft after the reader confirmed discarding all unsaved work. */
  discardSourceDraft,
  /** „Přesunout do trezoru“ for a password the publication check found (a `text` target); `true` once replaced. */
  moveSecretToVault: (target: RevealTarget) => moveToVault({ controller: () => controller, reveal, pick: pickSecret,
    notify: message => toast.success(message), warn: message => toast.error(message) }, target),
});
</script>
<template>
  <div ref="editorSurface" class="document-editor-surface" :class="{ hydrating: hydrating && markdownDialectSettled && codeBlockSettled }" :data-atlas-reveal="revealing || undefined"@pointerdown.capture="event => { notePointer(); armContactDrag(event); }" @focusin="selectionOnFocus" @dragstart.capture="startContactDrag" @drop.capture="dropContacts"
    @click="followLink" @auxclick="followLink" @markdown-follow-link="followAsked"@keydown="keyIntent" @keydown.esc="sourceEscape" @paste="inputIntent" @drop="inputIntent">
  <div v-if="joinError" role="alert" class="notice error editor-connection-error">
    <!-- After a deploy the server moves documents to the new schema and an open tab of the previous
         release gets a permanent SCHEMA_MISMATCH: reconnecting cannot help, loading the new editor does. -->
    <p>{{ schemaMismatch ? shellMore.doc.schemaMismatch : collaborationErrorText(joinError) }}</p>
    <details v-if="schemaMismatch"><summary>{{ shellMore.doc.technicalDetails }}</summary><p>{{ joinError }}</p></details>
    <Button v-if="schemaMismatch" type="button" @click="reloadPage"><AtlasIcon name="refresh" :size="15" />{{ shellMore.doc.refreshPage }}</Button>
    <Button v-else variant="outline" type="button" @click="configure">{{ shellMore.doc.reconnect }}</Button>
  </div>
  <div v-if="failure" role="alert" class="notice error">
    <template v-if="staleEditor">
      <p>{{ shellMore.doc.schemaMismatch }}</p>
      <details><summary>{{ shellMore.doc.technicalDetails }}</summary><p>{{ failure }}</p></details>
      <Button type="button" @click="reloadPage"><AtlasIcon name="refresh" :size="15" />{{ shellMore.doc.refreshPage }}</Button>
    </template>
    <template v-else>{{ shellMore.doc.openFailed(failure) }}</template>
  </div>
  <!-- Dokument | Zdroj: a plain switch between two panes. The document stays mounted (hidden) while the source is shown,
       so switching never remounts the editor or resets its undo history. -->
  <div v-if="sourceOffered" ref="sourceSwitch" v-bind="sourceThumb" class="source-switch" :class="{ 'is-locked': !canEdit }" :inert="!canEdit || undefined" role="group" :aria-label="shellMore.doc.pageView">
    <button type="button" :aria-pressed="!sourceTab" @click="requestSourceClose">{{ shellMore.doc.documentTab }}</button>
    <button type="button" :aria-pressed="sourceTab" @pointerenter="prefetchPageSource" @focus="prefetchPageSource" @click="!sourceTab && emit('requestSource')">{{ shellMore.doc.sourceTab }}</button>
  </div>
  <!-- The enter class goes with its animation: a finished one still holds a transform on the pane, and the editor's fixed layers
       (fullscreen, toolbars) would then be placed from the pane, not from the window. -->
  <div v-show="!sourceTab" class="document-pane" :class="{ 'atlas-view-enter': sourceVisited }" @animationend.self="sourceVisited = false">
  <!-- Each controller has exactly one content source. Changing appearance never remounts it. -->
  <!-- @vue-generic {'rich'} --><MarkdownEditor
    v-if="markdownDialectSettled && codeBlockSettled && adapter && !failure"
    ref="collaborativeEditor"
    :lexicon="placeLexicon"
    :key="`collaboration:${lease!.generation}:cb${codeBlockGeneration}`"
    @vue:unmounted="releaseLivePlugins"
    :collaboration="adapter"
    mode="revision"
    profile="rich"
    :documentSchemaVersion="2"
    :ariaLabel="shellMore.doc.pageContent"
    :readonly="readonly"
    :plugins="livePlugins.for(`collaboration:${lease!.generation}:cb${codeBlockGeneration}`)"
    :code-block="sharedCodeBlock"
    :imageUpload="uploads.image"
    :class="[theme, 'atlas-document-editor', editorClass, { 'editor-hydrating': hydrating }]"
    :outline="outline"
    :document-title="documentTitle"
    :fullscreenable="!sourceTab"
    :reader-bubble="!readerBar"
    v-bind="{ ...editorChrome, ...blockInsertion, ...importHooks }"
    @created="created"
    @content-change="contentChanged"
  />
  <LoadingSkeleton v-if="editorPending" :rows="6" :label="t.nav.loadingPage" :instant="skeletonShown" />
  <!-- The page's own value: a reader's editor, or the stand-in while a shared session without bundled state seeds (`hydrating`). -->
  <!-- @vue-generic {'rich'} --><MarkdownEditor
    v-if="markdownDialectSettled && !failure && !bundleOpening && (!adapter || hydrating)"
    :key="`local:cb${codeBlockGeneration}`"
    :lexicon="placeLexicon"
    @vue:unmounted="releaseLivePlugins"
    v-model="value"
    profile="rich"
    :documentSchemaVersion="2"
    :ariaLabel="shellMore.doc.pageContent"
    :readonly="readonly || sessionRequested"
    :plugins="livePlugins.for(`local:cb${codeBlockGeneration}`)"
    :code-block="sharedCodeBlock"
    :imageUpload="uploads.image"
    :class="[theme, 'atlas-document-editor', editorClass]"
    :outline="outline"
    :document-title="documentTitle"
    :fullscreenable="!sourceTab"
    :reader-bubble="!readerBar"
    v-bind="{ ...editorChrome, ...blockInsertion, ...importHooks }"
    @created="created"
    @change="changed"
  />
  </div>
  <div v-if="sourceTab" ref="sourcePane" class="source-pane" :style="{ '--source-height': sourceHeight }" :aria-label="shellMore.doc.pageSource" role="region">
    <!-- The panel rises in, not the pane: the pane is measured where it stands. -->
    <component :is="MarkdownSourcePanel" :draft="sourceDraft" class="atlas-view-enter" @escape="requestSourceClose" />
    <!-- The shared unsaved-changes question (Zůstat / Zahodit / Použít a zavřít); a host operation offers no Apply. -->
    <UnsavedChangesDialog :open="!!sourceConfirmation" :can-save="!!sourceConfirmation?.apply" :saving="sourceDraft.busy.value"
      :message="sourceConfirmation?.apply ? shellMore.doc.sourceUnsaved : shellMore.doc.sourceUnsavedExcluded" :save-label="shellMore.doc.applyAndClose"
      @stay="chooseSource('continue')" @discard="chooseSource('discard')" @save="chooseSource('apply')" />
  </div>
  <OpenApiDetailPanels :detail="detail" :change="detailChange" />
  <component :is="SecretReauthDialog" v-if="secretReauth.pending.value" :controller="secretReauth" />
  </div>
</template>

<style scoped>
.editor-connection-error { display: grid; gap: 12px; }
.editor-connection-error p { margin: 0; overflow-wrap: anywhere; }
.editor-connection-error button { justify-self: start; }
.editor-connection-error details { font-size: 12px; }
.editor-connection-error summary { cursor: pointer; }
/* Refresh 2.5 (board 04): a quiet segmented control, the chosen view on a raised white segment. The header above it ends
   at its metadata row with no padding, so the switch keeps its own distance: at -2px it sat on the state chip. */
.source-switch { --thumb-inset: 2px; --thumb-radius: calc(9px - 2px); align-self: center; display: flex; gap: 2px; margin: 12px 0 8px; padding: 2px; border-radius: 9px; background: var(--hover); }
/* A locked page keeps the row's place (J7.1): the switch is only invisible, so no heading moves when the lock is toggled. */
.source-switch.is-locked { visibility: hidden; }
/* A phone has no room for an invisible row (30 px above the first block): there it leaves the layout. */
@media (max-width: 640px) { .source-switch.is-locked { display: none; } }
.source-switch button { height: 26px; padding: 0 12px; border: 0; border-radius: 7px; background: transparent; color: var(--muted); font: inherit; font-size: 13px; white-space: nowrap; cursor: pointer;
  transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), color var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) { .source-switch button:hover:not([aria-pressed="true"]) { color: var(--text); } }
.source-switch button[aria-pressed="true"] { background: var(--surface); color: var(--text); font-weight: 600; box-shadow: 0 1px 2px color-mix(in srgb, var(--text) 10%, transparent); cursor: default; }
/* While the thumb is drawn it carries the raised segment; without it (reduced motion) the chosen view fades its own. */
.source-switch[data-thumb] button[aria-pressed="true"] { background: transparent; box-shadow: none; }
.source-switch button:focus-visible { outline: var(--focus-ring); outline-offset: -2px; }
.document-editor-surface {
  display: flex;
  flex-direction: column;
  flex: 1;
  width: 100%;
  min-width: 0;
  min-height: 0;
}
/* A shared session without bundled state builds hidden over the local stand-in and then takes its place (a class, not `:has()`, which
   restyled the page on every edit). */
.document-editor-surface.hydrating { position: relative; }
.document-editor-surface :deep(.atlas-document-editor.md-editor.paper.editor-hydrating) {
  position: absolute;
  inset: 0 0 auto;
  visibility: hidden;
  pointer-events: none;
}
/* The document pane is a transparent wrapper: the editor keeps the surface's flex sizing. */
.document-pane {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-width: 0;
  min-height: inherit;
}
/* The source takes the document's place and fills what is left of the window below the switch (fitSource), less the
   distance it keeps from the window's edge; in a low window it keeps a height one can still work in. */
.source-pane {
  --source-gap: 16px;
  height: max(320px, calc(var(--source-height, 100dvh) - var(--source-gap)));
  min-width: 0;
  overflow: hidden;
  border: 1px solid var(--line);
  /* The source panel's tokens follow the wiki theme. */
  --paper: var(--surface);
  --text-dim: var(--muted);
}

/* The footer's Alt+F10 hint means nothing on a touch screen (UX PG4-7). */
@media not (pointer: fine) { .document-editor-surface :deep(.md-keyboard-hint) { display: none; } }

/* Návrhy při psaní: the assist module’s motion comes from the editor’s core tokens, which src/motion.css maps
   onto the Atlas tokens once (:root and .md-editor); only the warning tone is the wiki’s. */
.document-editor-surface :deep(.atlas-document-editor.md-editor) {
  --md-assist-warning-bg: var(--warning-bg);
}
.document-editor-surface :deep(.atlas-document-editor.md-editor.paper) {
  --font-sans: "DM Sans", system-ui, sans-serif;
  --z-sticky: 3;
  flex: 1;
  width: 100%;
  min-height: inherit;
  border: 0;
  border-radius: 0;
  background: transparent;
  box-shadow: none;
}
/* content-visibility is the editor's own (content.css), the same for a reader and a writer (one-editor contract J7.1): readers
   alone (3.3) made a reader's page shorter than the writer's and it jumped on a change of the right to write. */
</style>
