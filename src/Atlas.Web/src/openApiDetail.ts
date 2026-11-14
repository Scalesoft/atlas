import { computed, nextTick, ref, shallowRef, watch, type Ref } from "vue";
import shellMore from "./i18n/shell-more";
import { downloadMarkdownFile, type MarkdownModuleNodeContext } from "@atlas/markdown/core";
import {
  mountOpenApiReference,
  mountOpenApiSourceEditor,
  loadOpenApiContent,
  normalizeOpenApiShape,
  prefetchOpenApiSourceEditor,
  type OpenApiData,
  type OpenApiSourceEditor,
} from "@atlas/markdown/plugins/openapi";

/** What the OpenAPI plugin hands to `openDetail`: the block's node context (stable `anchor`, node-lifetime `signal`). */
export type OpenApiDetailContext = MarkdownModuleNodeContext<OpenApiData>;
export type OpenApiDetailView = "api" | "yaml";
/** Why an unapplied YAML draft can no longer be written: the block is gone, or editing is not allowed. */
export type OpenApiDetailBlock = "lost" | "readonly";
export type OpenApiDetailChoice = "continue" | "discard" | "apply";

export interface OpenApiDetailOptions {
  /** The page is not editable (reading, permission loss). Must be reactive. */
  readonly: () => boolean;
  /**
   * Identity of the editor instance currently hosting the document (collaboration lease generation,
   * code-block re-key, local vs. shared binding). A change means every block context is stale.
   */
  session?: () => unknown;
  /** The host is moving the detail to the same block in another view (reading → editing): a lost block keeps it open. */
  holding?: () => boolean;
  /** Injection points for tests; default to the lazy editor entry points. */
  mountSource?: typeof mountOpenApiSourceEditor;
  mountReference?: typeof mountOpenApiReference;
  prefetchSource?: () => void;
}

interface Confirmation {
  /** The draft cannot be applied any more; the dialog offers a download instead. */
  unapplicable: boolean;
  resolve?: (resolved: boolean) => void;
  then?: () => void;
}

export const openApiDetailMessages = {
  get checking() { return shellMore.openapi.checking; },
  get stale() { return shellMore.openapi.stale; },
  get unavailable() { return shellMore.openapi.unavailable; },
  get lost() { return shellMore.openapi.lost; },
  // Mirrors App.vue's permission-change notice for the page draft.
  get readonly() { return shellMore.openapi.readonly; },
  get detached() { return shellMore.openapi.detached; },
} as const;

/**
 * An unapplied YAML draft outlives the dialog (the page is left, the editor is replaced): kept here for the same definition
 * (file name and stored source) and offered again when its detail opens, so the draft needs no save or publish guard of the page.
 */
const keptDrafts = new Map<string, string>();
const draftKey = (filename: string, source: string) => `${filename}
${source}`;

const viewOrder: OpenApiDetailView[] = ["api", "yaml"];
const viewLabels: Record<OpenApiDetailView, string> = { api: "Dokumentace", yaml: "YAML" };

/**
 * Host side of the OpenAPI `openDetail` (OpenApiDetailPanels, a full-height dialog over the page):
 * one active block, runtime-only tabs, a local YAML draft with an explicit Apply, and generation guards
 * so a late Monaco/Scalar mount never lands in a closed or re-targeted detail. Monaco loads only when the
 * YAML view is shown, Scalar only when the API view is shown.
 */
export function useOpenApiDetail(options: OpenApiDetailOptions) {
  const mountSource = options.mountSource ?? mountOpenApiSourceEditor;
  const mountReference = options.mountReference ?? mountOpenApiReference;
  const prefetch = options.prefetchSource ?? (() => { prefetchOpenApiSourceEditor(); });

  const context = shallowRef<OpenApiDetailContext>();
  const open = ref(false);
  const view = ref<OpenApiDetailView>("api");
  const visited = ref<OpenApiDetailView[]>([]);
  const draft = ref<string>();
  const baseline = ref("");
  const snapshot = shallowRef<OpenApiData>();
  const detached = ref(false);
  const revision = ref(0);
  const loading = ref(false);
  const error = ref("");
  const confirmation = shallowRef<Confirmation>();
  const sourceHost = ref<HTMLElement>() as Ref<HTMLElement | undefined>;
  const referenceHost = ref<HTMLElement>() as Ref<HTMLElement | undefined>;

  /** The block's current data, read from the live document at the node's current position. */
  const current = computed<OpenApiData | undefined>(() => {
    void revision.value; // page updates, including undo and edits made outside this detail
    const ctx = context.value;
    if (!ctx || detached.value) return undefined;
    if (!ctx.editor) return ctx.data;
    try {
      const position = ctx.getPos?.();
      if (position === undefined) return undefined;
      const node = ctx.editor.state.doc.nodeAt(position);
      return node?.type.name === "openapi" ? normalizeOpenApiShape(node.attrs.data) : undefined;
    } catch { return undefined; }
  });
  const dirty = computed(() => draft.value !== undefined && draft.value !== baseline.value);
  const blocked = computed<OpenApiDetailBlock | undefined>(() =>
    options.readonly() ? "readonly" : detached.value ? "lost" : undefined);
  /** An unapplied draft that can no longer be written; kept for download until explicitly discarded. */
  const retained = computed(() => dirty.value ? blocked.value : undefined);
  /** What the block offers now: Dokumentace unless download-only, YAML while it can be edited (or holds a draft to download). */
  const tabs = computed(() => viewOrder.filter(id => usable(id)).map(id => ({ id, label: viewLabels[id] })));
  const data = computed(() => current.value ?? snapshot.value);
  const title = computed(() => data.value?.filename ?? "OpenAPI");

  let sourceEditor: OpenApiSourceEditor | undefined;
  let generation = 0;
  let referenceGeneration = 0;
  let disposeReference: (() => void) | undefined;
  let stopListening: (() => void) | undefined;
  let sessionAtBegin: unknown;

  function disposeSource() { generation++; sourceEditor?.destroy(); sourceEditor = undefined; loading.value = false; }
  function disposePreview() { referenceGeneration++; disposeReference?.(); disposeReference = undefined; }
  function settleConfirmation(resolved: boolean) {
    const pending = confirmation.value;
    confirmation.value = undefined;
    pending?.resolve?.(resolved);
  }
  function cleanup() {
    if (dirty.value && snapshot.value) keptDrafts.set(draftKey(snapshot.value.filename, baseline.value), draft.value!);
    stopListening?.(); stopListening = undefined;
    disposeSource(); disposePreview();
    settleConfirmation(false);
    context.value = undefined; snapshot.value = undefined; draft.value = undefined; baseline.value = "";
    visited.value = []; error.value = ""; detached.value = false;
  }
  function attach(ctx: OpenApiDetailContext) {
    stopListening?.();
    context.value = ctx;
    const aborted = () => { if (context.value === ctx) detached.value = true; };
    const changed = () => { revision.value++; };
    ctx.signal.addEventListener("abort", aborted, { once: true });
    ctx.editor?.on("update", changed);
    stopListening = () => { ctx.signal.removeEventListener("abort", aborted); ctx.editor?.off("update", changed); };
    revision.value++;
  }
  function sameBlock(a: OpenApiDetailContext, b: OpenApiDetailContext) {
    if (a.anchor && a.anchor === b.anchor) return true;
    try { return !!a.editor && a.editor === b.editor && a.getPos?.() !== undefined && a.getPos?.() === b.getPos?.(); }
    catch { return false; }
  }
  function usable(target: OpenApiDetailView, value = data.value) {
    if (!value) return false;
    if (target === "api") return value.mode !== "download";
    return !blocked.value || (retained.value !== undefined && visited.value.includes("yaml"));
  }
  function activate(target: OpenApiDetailView) {
    if (!usable(target)) return false;
    if (!visited.value.includes(target)) visited.value = [...visited.value, target];
    view.value = target;
    return true;
  }
  let content: Awaited<ReturnType<typeof loadOpenApiContent>> | undefined;
  /** Where the page was scrolled when the detail opened: the scrolling ancestor of the block and its offset. */
  let place: { scroller: Element; top: number } | undefined;
  function capturePlace(anchor: HTMLElement) {
    let scroller: Element | null = anchor.parentElement;
    while (scroller && !(scroller.scrollHeight > scroller.clientHeight + 1 && /(auto|scroll)/.test(getComputedStyle(scroller).overflowY))) scroller = scroller.parentElement;
    const target = scroller ?? document.scrollingElement;
    place = target ? { scroller: target, top: target.scrollTop } : undefined;
  }
  function begin(ctx: OpenApiDetailContext, target: OpenApiDetailView) {
    void loadOpenApiContent().then(module => { content = module; }, () => {});
    if (ctx.anchor) capturePlace(ctx.anchor);
    cleanup();
    if (ctx.signal.aborted) { open.value = false; return; }
    attach(ctx);
    sessionAtBegin = options.session?.();
    snapshot.value = current.value ?? ctx.data;
    baseline.value = snapshot.value.source;
    const kept = keptDrafts.get(draftKey(snapshot.value.filename, baseline.value));
    keptDrafts.delete(draftKey(snapshot.value.filename, baseline.value));
    if (kept !== undefined && kept !== baseline.value) draft.value = kept;
    open.value = true;
    if (!activate(target)) open.value = false;
  }
  function ask(request: Confirmation) {
    settleConfirmation(false);
    confirmation.value = request;
  }

  /** The `openDetail` option of the OpenAPI plugin. `true` hands the detail to this dialog. */
  function openDetail(ctx: OpenApiDetailContext, target: OpenApiDetailView): boolean {
    if (ctx.signal.aborted) return false;
    if (target === "yaml" && (options.readonly() || !ctx.update || (ctx.editor && !ctx.editor.isEditable))) return false;
    if (target === "api" && ctx.data.mode === "download") return false;
    if (open.value && context.value && !detached.value && sameBlock(context.value, ctx)) {
      // Another entry point of the same block (card button vs. selection toolbar): keep draft and tabs.
      attach(ctx);
      activate(target);
      return true;
    }
    if (dirty.value) { ask({ unapplicable: !!blocked.value, then: () => begin(ctx, target) }); return true; }
    begin(ctx, target);
    return true;
  }

  function apply(): boolean {
    const ctx = context.value, value = current.value;
    if (!dirty.value) return true;
    if (!ctx || !value || blocked.value || !ctx.update || !ctx.editor?.isEditable || ctx.signal.aborted) {
      error.value = openApiDetailMessages.unavailable; return false;
    }
    if (value.source !== baseline.value) { error.value = openApiDetailMessages.stale; return false; }
    try {
      // The full check (the YAML parser) is loaded when the detail opens; the server judges the source again on write.
      const candidate = { ...value, source: sourceEditor?.getValue() ?? draft.value };
      if (!content) { void loadOpenApiContent().then(module => { content = module; }, () => {}); error.value = openApiDetailMessages.checking; return false; }
      const next = content.normalizeOpenApi(candidate);
      ctx.update(next);
      // `update` silently ignores a stale or readonly target; never drop the draft unless it landed.
      if (current.value?.source !== next.source) { error.value = openApiDetailMessages.unavailable; return false; }
      baseline.value = next.source; draft.value = undefined; error.value = "";
      // The button that applied disables itself: the keyboard stays in the source editor, so the Esc ladder still works.
      sourceEditor?.focus();
      return true;
    } catch (cause) { error.value = cause instanceof Error ? cause.message : String(cause); return false; }
  }
  function discard() {
    draft.value = undefined; error.value = "";
    const value = current.value;
    if (value) { baseline.value = value.source; sourceEditor?.setValue(value.source, value.filename); }
    // The button that discarded disables itself: the keyboard stays in the source editor.
    sourceEditor?.focus();
  }
  function requestClose() {
    if (confirmation.value) return;
    if (dirty.value) ask({ unapplicable: !!blocked.value, then: () => { open.value = false; } });
    else open.value = false;
  }
  /** Outcome of the Apply / Discard / Continue confirmation. */
  function choose(choice: OpenApiDetailChoice) {
    const pending = confirmation.value;
    if (!pending) return;
    if (choice === "continue") { settleConfirmation(false); return; }
    if (choice === "apply" && (pending.unapplicable || !apply())) {
      settleConfirmation(false);
      if (visited.value.includes("yaml")) view.value = "yaml";
      return;
    }
    if (choice === "discard") discard();
    confirmation.value = undefined;
    pending.resolve?.(true);
    pending.then?.();
  }
  /**
   * Host guard before saving/publishing the page: resolves `true` when no unapplied draft remains
   * (none, applied or discarded) and `false` when the author keeps editing.
   */
  function resolveDraft(): Promise<boolean> {
    if (!dirty.value) return Promise.resolve(true);
    if (confirmation.value) return Promise.resolve(false);
    return new Promise(resolve => ask({ unapplicable: !!blocked.value, resolve }));
  }
  /** The author already confirmed discarding all unsaved work (leaving the page, restore). */
  function discardDraft() { settleConfirmation(false); discard(); }
  function downloadDraft() {
    const value = sourceEditor?.getValue() ?? draft.value;
    if (value === undefined) return;
    const doc = sourceHost.value?.ownerDocument ?? document;
    downloadMarkdownFile(doc, data.value?.filename ?? "openapi.yaml", new Blob([value], { type: "text/plain;charset=utf-8" }));
  }
  function prefetchSource() { if (!options.readonly()) prefetch(); }
  /** The dialog finished its close animation. */
  function closed() {
    if (open.value) return;
    const ctx = context.value;
    cleanup();
    // The place is kept: the block the detail belonged to is selected again and the keyboard is in the editor, not on the page body.
    const position = ctx && !ctx.signal.aborted && ctx.editor && !ctx.editor.isDestroyed && ctx.editor.isEditable ? ctx.getPos?.() : undefined;
    if (position === undefined || ctx?.editor?.state.doc.nodeAt(position)?.type.name !== "openapi" || typeof ctx.editor.chain !== "function") return;
    const editor = ctx.editor, saved = place;
    // The dialog gives the focus back to what opened it (gone by now) after this runs, and the page scrolls to the top with it: take both back a frame later.
    const restore = () => {
      if (editor.isDestroyed) return;
      editor.chain().setNodeSelection(position).focus(undefined, { scrollIntoView: false }).run();
      if (saved) saved.scroller.scrollTop = saved.top;
    };
    restore();
    requestAnimationFrame(() => requestAnimationFrame(restore));
  }

  async function ensureSource() {
    if (!open.value || view.value !== "yaml" || blocked.value || !current.value) return;
    await nextTick();
    if (sourceEditor) { sourceEditor.layout(); return; }
    if (loading.value || !open.value || view.value !== "yaml" || blocked.value || !sourceHost.value || !current.value) return;
    const value = current.value, token = ++generation;
    loading.value = true; error.value = "";
    try {
      const mounted = await mountSource(sourceHost.value, {
        source: draft.value ?? value.source, filename: value.filename,
        onChange: source => { if (token === generation) { draft.value = source; error.value = ""; } },
      });
      if (token !== generation) { mounted.destroy(); return; }
      sourceEditor = mounted;
      // After the YAML tab or Enter on the block the focus was on the control that left; the keyboard goes on typing in the editor.
      const held = document.activeElement;
      if (!held || held === document.body || sourceHost.value?.closest("dialog")?.contains(held)) mounted.focus();
    } catch (cause) {
      if (token === generation) error.value = shellMore.src.loadFailed(cause instanceof Error ? cause.message : String(cause));
    } finally { if (token === generation) loading.value = false; }
  }
  async function ensureReference() {
    if (!open.value || view.value !== "api" || disposeReference) return;
    const token = ++referenceGeneration;
    await nextTick();
    const value = data.value;
    if (token !== referenceGeneration || !open.value || view.value !== "api" || !referenceHost.value || !value || value.mode === "download") return;
    // Try It stays off in Atlas: the Scalar frame is same-origin and would reach Atlas with the reader's cookies.
    disposeReference = mountReference(referenceHost.value, value, { allowRequests: false });
  }

  watch([view, context, open, blocked], () => { void ensureSource(); void ensureReference(); });
  watch(current, value => {
    if (value) snapshot.value = value;
    else if (open.value && context.value) detached.value = true;
  });
  watch(() => current.value?.source, source => {
    if (source === undefined) return;
    if (!dirty.value) {
      baseline.value = source; draft.value = undefined;
      sourceEditor?.setValue(source, current.value?.filename);
    }
    // Scalar shows the stored definition; rebuild it for the new source.
    disposePreview(); void ensureReference();
  });
  watch(() => current.value?.mode, mode => {
    if (mode !== "download" || !visited.value.includes("api")) return;
    disposePreview();
    visited.value = visited.value.filter(id => id !== "api");
    if (view.value === "api") { if (visited.value.includes("yaml")) view.value = "yaml"; else requestClose(); }
  });
  if (options.session) watch(options.session, value => { if (open.value && context.value && value !== sessionAtBegin) detached.value = true; });
  // Block lost or editing no longer allowed: Monaco closes; a dirty draft stays downloadable in the YAML
  // tab until the author discards it. Without a draft the YAML tab goes away; a lost block closes the detail.
  watch([blocked, dirty], ([reason]) => {
    if (!reason || !open.value) return;
    disposeSource();
    if (dirty.value) return;
    if (reason === "lost" && !options.readonly()) { if (!options.holding?.()) { settleConfirmation(false); open.value = false; } return; }
    if (visited.value.includes("yaml")) {
      visited.value = visited.value.filter(id => id !== "yaml");
      if (view.value === "yaml") {
        if (visited.value.includes("api") && usable("api")) view.value = "api";
        else { settleConfirmation(false); open.value = false; }
      }
    }
  });

  return {
    // state
    /** The open block's element in the document (the reading rendering while it reads). */
    anchor: computed(() => context.value?.anchor),
    open, view, visited, tabs, title, current, data, draft, baseline, dirty, blocked, retained, detached,
    loading, error, sourceHost, referenceHost,
    confirmation: computed(() => confirmation.value),
    // actions
    openDetail, activate, apply, discard, requestClose, choose, resolveDraft, discardDraft, downloadDraft,
    prefetchSource, closed, dispose: cleanup,
  };
}

export type OpenApiDetail = ReturnType<typeof useOpenApiDetail>;
