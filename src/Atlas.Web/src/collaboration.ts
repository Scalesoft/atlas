import type {
  MarkdownCollaborationAdapter,
  MarkdownEditorState,
  MarkdownEditorController,
  MarkdownPlugin,
  MarkdownRichValue,
} from "@atlas/markdown/core";
import { api, checkSessionAfterLiveFailure } from "./api";
import t from "./i18n/shell";
import { markdownProjection } from "./pageSourceDraft";
import { userColor } from "./live/objectPresence";
/**
 * The page editor's own hub (/hubs/markdown, Atlas.Markdown's transport) knows nothing of the shell's session: a session that ended
 * shows there only as a lost or failed connection. Each time its connection is lost or fails, the session is checked as the live
 * connection's is, so an ended one signs out instead of leaving the editor reconnecting (ARCH3-7).
 */
export function collaborationSessionCheck(check: (error: unknown) => Promise<void> = checkSessionAfterLiveFailure) {
  let last: MarkdownEditorState["connection"] | undefined;
  return (connection: MarkdownEditorState["connection"]) => {
    if (connection !== last && (connection === "reconnecting" || connection === "offline" || connection === "failed")) void check(undefined);
    last = connection;
  };
}
/** The value a new collaboration session is compared with: the ready local editor's, otherwise the stored one. */
export function captureCollaborationBaseline(
  controller: MarkdownEditorController<"rich"> | undefined,
  fallback: MarkdownRichValue,
): MarkdownRichValue {
  return controller?.state.binding === "local" &&
    controller.state.lifecycle === "ready"
    ? controller.getValue()
    : fallback;
}
/**
 * Whether the hydrated shared document holds content the baseline does not (e.g. another writer's
 * acknowledged but unsaved updates). Both sides go through the same Markdown projection as a save, so
 * attribute order, schema defaults and seeding normalizations (chart references, heading ids, the empty
 * paragraph after a final table) never count as changes. `plugins` returns fresh content plugin instances.
 *
 * The projection mounts an editor per side, about 1.4 s for a 500-block page, so it is the last resort (review
 * cycle 2, P2-3): blocks equal after a cheap normalization (key order, null attributes, the trailing empty paragraph)
 * are equal, and only the blocks that still differ are projected, side by side.
 */
export function hasHydrationChanges(
  baseline: MarkdownRichValue,
  hydrated: MarkdownRichValue,
  plugins: () => readonly MarkdownPlugin[],
) {
  const steps = hydrationChanges(baseline, hydrated, plugins);
  for (;;) {
    const step = steps.next();
    if (step.done) return step.value;
  }
}
/** `hasHydrationChanges` in steps: it pauses between the two sides' projections, so each can run in its own idle period. */
export function* hydrationChanges(
  baseline: MarkdownRichValue,
  hydrated: MarkdownRichValue,
  plugins: () => readonly MarkdownPlugin[],
): Generator<void, boolean> {
  if (JSON.stringify(baseline.document) === JSON.stringify(hydrated.document)) return false;
  const before = topBlocks(baseline), after = topBlocks(hydrated);
  // Another number of blocks: the whole documents are compared (a block may have been split or joined).
  const differing = before.length === after.length ? before.flatMap((block, index) => (block === after[index] ? [] : [index])) : null;
  if (differing?.length === 0) return false;
  try {
    const only = (value: MarkdownRichValue) => (differing
      ? { ...value, document: { ...value.document, content: differing.map(index => blocksOf(value)[index]) } } as MarkdownRichValue
      : value);
    const comparable = (value: MarkdownRichValue) => markdownProjection(only(value), plugins()).trimEnd();
    const projected = comparable(baseline);
    yield;
    return projected !== comparable(hydrated);
  } catch {
    // Without a projection keep the conservative answer: offer the hydrated document for saving.
    return true;
  }
}
/** Each top-level block as normalized JSON; trailing empty paragraphs (the seeding's one after a final table) are no blocks. */
function topBlocks(value: MarkdownRichValue): string[] {
  const blocks = blocksOf(value).map(block => JSON.stringify(normalized(block)));
  while (blocks.length && blocks.at(-1) === emptyParagraph) blocks.pop();
  return blocks;
}
const blocksOf = (value: MarkdownRichValue) => (value.document.content ?? []) as readonly unknown[];
const emptyParagraph = JSON.stringify({ type: "paragraph" });
/** Keys in one order, null and undefined attributes dropped, an attribute object left empty dropped, the marks of a page reference
 *  dropped (its schema takes none: a stored bold one is lost on load, which is no edit of the page's). */
function normalized(node: unknown): unknown {
  if (Array.isArray(node)) return node.map(normalized);
  if (!node || typeof node !== "object") return node;
  const entries = Object.entries(node as Record<string, unknown>).sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0));
  const result: Record<string, unknown> = {};
  for (const [key, child] of entries) {
    if (key === "attrs" && child && typeof child === "object" && !Array.isArray(child)) {
      const attrs = Object.fromEntries(Object.entries(child).filter(([, attr]) => attr !== null && attr !== undefined).sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0)).map(([name, attr]) => [name, normalized(attr)]));
      if (Object.keys(attrs).length) result.attrs = attrs;
    } else if (child !== undefined && !(key === "marks" && (node as { type?: unknown }).type === "pageReference")) result[key] = normalized(child);
  }
  return result;
}
export interface CollaborationIdentity {
  id: string;
  displayName: string;
}
export interface CollaborationLease {
  documentId: string;
  generation: string;
  currentVersion: number;
}
export interface CollaborationStatus {
  enabled: boolean;
  generation: string | null;
  canSave: boolean;
  /** The session can be checkpointed (`collaborationCheckpointReady`), editable or not: a locked writer still saves and publishes. */
  checkpointReady?: boolean;
  /** Synchronization readiness for an explicit source checkpoint; not write permission. */
  sourceCheckpointReady?: boolean;
  connection: MarkdownEditorState["connection"];
  pendingChanges: number;
  error: MarkdownEditorState["error"];
  projectionSave: MarkdownEditorState["projectionSave"];
  currentVersion?: number;
}
export function collaborationCanSave(state: MarkdownEditorState) {
  return state.editable && collaborationCheckpointReady(state);
}
/** The source save endpoint still authorizes the writer and fences the checkpoint's version/generation. */
export function collaborationCheckpointReady(state: MarkdownEditorState) {
  // In V1 only the elected snapshotter receives a projection acknowledgement.
  // The server still fences every save against its current durable projection.
  return (
    state.lifecycle === "ready" &&
    state.connection === "online" &&
    state.pendingChanges === 0 &&
    state.documentSave === "saved" &&
    state.projectionSave !== "degraded" &&
    !state.error
  );
}
// The transport loads with the page editor, alongside the page itself, not after it.
const signalrTransport = () => import("@atlas/markdown/collaboration/signalr");
void signalrTransport().catch(() => {});
export type BundledCollaboration = NonNullable<NonNullable<import("./api").Page["bundle"]>["collaboration"]>;
const presenceOf = (identity: CollaborationIdentity) => ({ id: identity.id, name: identity.displayName, color: userColor(identity.id) });
/**
 * The shared session from the page bundle's state (3.6, one editor): the editor builds from it at once and takes input before the hub
 * joins; the join only adds what came after. No lease request: the bundle carries the state exactly when the lease would return this
 * same generation untouched. A reader of a live page gets it read-only. Undefined without the state (none to serve, or one of another
 * generation, read across a reset): the editor then joins through the server.
 */
export async function openBundledCollaboration(bundled: BundledCollaboration, identity: CollaborationIdentity): Promise<{
  lease: CollaborationLease;
  adapter: MarkdownCollaborationAdapter;
} | undefined> {
  const live = await bundled.liveState;
  if (!live?.tag.includes(`"${bundled.generation}.`)) return undefined;
  const [state, { createSignalRMarkdownAdapter }] = await Promise.all([
    new Response(new Blob([live.gzip]).stream().pipeThrough(new DecompressionStream("gzip"))).arrayBuffer(),
    signalrTransport(),
  ]);
  return {
    lease: { documentId: bundled.documentId, generation: bundled.generation, currentVersion: bundled.currentVersion },
    adapter: createSignalRMarkdownAdapter({
      hubUrl: "/hubs/markdown", documentId: bundled.documentId, presence: presenceOf(identity), allowOfflineEditing: false,
      initialState: new Uint8Array(state), initialWritable: !bundled.readOnly, ...(bundled.readOnly ? { reader: true } : {}),
    }),
  };
}
/**
 * `read`: a reader of a page published live (3.6-I, Publikace Živě) joins the session that writers keep (`GET`, never creating one) and
 * the server takes nothing from it. Without such a session the server answers 404 `no_live_session`: the page stays static.
 * `signal` gives up the lease's request (the page left before the join was used).
 */
export async function joinPageCollaboration(
  pageId: string,
  identity: CollaborationIdentity,
  read = false,
  signal?: AbortSignal,
): Promise<{
  lease: CollaborationLease;
  adapter: MarkdownCollaborationAdapter;
}> {
  if (!pageId || !identity.id || !identity.displayName)
    throw new Error(
      t.collab.missing,
    );
  const transport = signalrTransport();
  const path = `/pages/${encodeURIComponent(pageId)}/collaboration`;
  const lease = await api<CollaborationLease>(path, { ...(read ? {} : { method: "POST" }), ...(signal ? { signal } : {}) });
  if (
    !lease.documentId ||
    !lease.generation ||
    !Number.isInteger(lease.currentVersion)
  )
    throw new Error(t.collab.invalidSession);
  const { createSignalRMarkdownAdapter } = await transport;
  return {
    lease,
    adapter: createSignalRMarkdownAdapter({
      hubUrl: "/hubs/markdown",
      documentId: lease.documentId,
      // The server's colour of the user, so their caret here matches their pointer and avatars on boards.
      presence: presenceOf(identity),
      allowOfflineEditing: false,
    }),
  };
}
