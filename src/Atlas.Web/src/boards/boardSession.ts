import { api, ApiError, errorMessage } from "../api";
import type { CollaborationPeer } from "../collaborationPresence";
import { isCommentsChange } from "../comments/commentThread";
import type { LiveStatus } from "../live/liveConnection";
import { createPresenceRoster, presenceActivity } from "../live/objectPresence";
import { throttleLatest } from "../live/throttleLatest";
import { viewSessions, type ObjectListener, type ViewHandle, type ViewSessions } from "../live/viewSession";
import { liveObjects, type LiveObjects } from "../objects/liveObjects";
import { boardApi, defaultBoardSettings, isObjectEdit, maxBatchChanges, type BoardApi, type BoardBatch, type BoardEdit, type BoardSettings, type BoardSide, type Geometry } from "./boardApi";
import t from "../i18n/boards";
import { createBoardModel, fromBoardDto, type BoardItem, type BoardModel, type UndoStep } from "./boardModel";

/**
 * One open board: loads it, keeps the model in step with the server (spec §6, §7) and tells the view when to render again.
 * Local batches go out one at a time in their order, so the server orders them as the view already shows them. A batch that
 * cannot reach the server (no answer, 408, 429, 5xx) stays pending and is sent again with the same operation id; a refused
 * one is dropped and the board reloaded. A batch the server keeps failing on (5xx) stops the queue after a few attempts and
 * leaves the choice to the user: send again (everything kept) or reload the board, which drops it and those behind it. A 401 stops sending and keeps the
 * batches until the shell has checked the session. A gap in the sequence is read from `operations?after=` (tried again until
 * it works), `resync_required` reloads. A revoked board is only a hint: the board is loaded again and HTTP says whether access
 * is gone (403, 404: read-only, the unsaved changes stay visible); the view session joins the view again by itself.
 *
 * Live, it is the board's view session `board:{id}` (viewSession.ts): the board object's changes are its `BoardChanged`
 * batches, its presence the cursors and drag previews. The presence at the objects of its cards (a colleague editing the page
 * in its page) shows on those cards (`editors`), in the server's colour of each person.
 */
export interface PageTargetInfo { title: string | null; available: boolean; spaceTitle?: string | null; typeId?: string; excerpt?: string | null; canEdit?: boolean;
  /** In the trash, readable by this reader (not the same as no access); `spaceId` and `canRestore`: where and whether this reader restores it. */
  trashed?: boolean; spaceId?: string; canRestore?: boolean }
/** An item as the canvas draws it: a page card carries what the reader may see of its page, undefined while it is looked up. */
export type BoardViewItem = BoardItem & { target?: PageTargetInfo; editors?: CollaborationPeer[] };
export interface DraggedItem extends Geometry { itemId: string }
/** Another user on the board: their pointer (board coordinates) and what they drag right now. */
/** A connector someone is drawing: its fixed end (and side), the item its free end would land at, and where that end is. */
export interface ConnectingPresence { from: string; side?: BoardSide; to?: string; x: number; y: number }
export interface RemoteDrag { userId: string; displayName: string; color: string; dragging: DraggedItem[]; cursor?: { x: number; y: number }; connecting?: ConnectingPresence }
export interface PresencePayload { cursor?: { x: number; y: number }; dragging?: DraggedItem[]; connecting?: ConnectingPresence }

export interface BoardSessionState {
  status: "loading" | "ready" | "missing" | "failed" | "revoked";
  error: string;
  title: string;
  /** Background and snapping, shared by everyone on the board (spec §3.7). */
  settings: BoardSettings;
  spaceId: string;
  /** The page whose own board this is (a deployment board), or "". */
  ownerPageId: string;
  canEdit: boolean;
  items: readonly BoardViewItem[];
  /** Local batches the server has not confirmed yet. */
  pending: number;
  saving: boolean;
  /** The last send did not reach the server; it is retried. */
  offline: boolean;
  /** Why the last batch was refused (the board has been reloaded), or an undo's or redo's contact edit (the contact was read again). */
  rejected: string;
  /** The server keeps answering one batch with a failure: how many local batches (it and those behind it) wait for the user's choice, 0 when none. */
  blocked: number;
  /** The server answered 401: nothing is sent until the board is loaded again (`resume`); the batches stay pending. */
  signedOut: boolean;
  connection: LiveStatus;
  /** The server refused the board's live view (not_found, forbidden, unsupported) while the connection is up: the board saves over
   * HTTP, but others' changes and presence do not arrive until it is opened again. Not offline (UX1-1). */
  liveRefused: boolean;
  canUndo: boolean;
  canRedo: boolean;
  remote: readonly RemoteDrag[];
}

export interface BoardSessionDeps {
  api?: Pick<BoardApi, "get" | "submit" | "after">;
  views?: Pick<ViewSessions, "open">;
  /** The titles of the pages the reader may open among `ids` (POST /api/pages/references); the others are unavailable. */
  references?: (ids: string[]) => Promise<{ id: string; title: string }[]>;
  /** The tab's live objects, which the object cards draw. */
  objects?: Pick<LiveObjects, "get" | "apply" | "refresh" | "revoke" | "edit">;
  retryMs?: (attempt: number) => number;
  /** A preview without news for this long is gone (the other tab closed mid-drag). */
  presenceTtlMs?: number;
  /** A colleague at a card's object without news for this long is gone (a page repeats its presence while edited). */
  objectPresenceTtlMs?: number;
  /** Runs once before the next frame is drawn (requestAnimationFrame): what others do is told to the view at that pace. */
  frame?: (run: () => void) => void;
}

const rejections: Record<string, string> = t.session.rejections;

/** What the server accepts of POST /api/pages/references. */
const referencesChunk = 100;
/** Attempts of one batch that the server answers with a 5xx before the user is asked what to do. */
const maxServerFailures = 6;
const maxRetryAfterMs = 300_000;
// shortcut: the server relays at most 2048 characters of presence, so a drag of more items previews its first ones only;
// send the dragged selection's bounds instead if large drags need a faithful preview.
const maxPreviewed = 18;

const sides: readonly (BoardSide | undefined)[] = ["top", "right", "bottom", "left"];
/** Presence within the server's size: whole board pixels and the first dragged items. */
function compact({ cursor, dragging, connecting }: PresencePayload): Record<string, unknown> {
  const round = Math.round;
  return {
    ...(cursor && { cursor: { x: round(cursor.x), y: round(cursor.y) } }),
    ...(connecting && { connecting: { ...connecting, x: round(connecting.x), y: round(connecting.y) } }),
    ...(dragging && { dragging: dragging.slice(0, maxPreviewed).map(({ itemId, x, y, width, height }) => ({ itemId, x: round(x), y: round(y), width: round(width), height: round(height) })) }),
  };
}

/** What a failed request asks for: try again later (no answer, 408, 429, 5xx), a signed-in session (401), or nothing more (the other 4xx). */
function outcomeOf(failure: unknown): "retry" | "signedOut" | "refused" {
  const status = failure instanceof ApiError ? failure.status : 0;
  return status === 401 ? "signedOut" : status >= 400 && status < 500 && status !== 408 && status !== 429 ? "refused" : "retry";
}

async function pageReferences(ids: string[]) {
  return (await api<{ items: { id: string; title: string }[] }>("/pages/references", { method: "POST", body: JSON.stringify({ ids }) })).items;
}
const isBatch = (payload: unknown): payload is BoardBatch =>
  !!payload && typeof (payload as BoardBatch).sequence === "number" && typeof (payload as BoardBatch).operationId === "string" && Array.isArray((payload as BoardBatch).changes);

export function createBoardSession(boardId: string, changed: () => void, deps: BoardSessionDeps = {}) {
  const http = deps.api ?? boardApi;
  const views = deps.views ?? viewSessions();
  const references = deps.references ?? pageReferences;
  const objects = deps.objects ?? liveObjects;
  const retryMs = deps.retryMs ?? ((attempt: number) => Math.min(30_000, 1000 * 2 ** attempt));
  const presenceTtl = deps.presenceTtlMs ?? 5000;
  // Lower case, as the server names the view (the address may spell the id in capitals).
  const viewKey = `board:${boardId.toLowerCase()}`;

  // shortcut: pending batches live in memory only, so closing the tab while offline loses them; move them to the WorkspaceSession's durable queue (plan §11).
  let model: BoardModel | undefined;
  let status: BoardSessionState["status"] = "loading";
  let error = "", rejected = "", spaceId = "", ownerPageId = "";
  let canEdit = false, offline = false, disposed = false, signedOut = false, blocked = false;
  /** The board object was revoked: it is read again until HTTP answers. */
  let revokedReload = false;
  let connection: LiveStatus = "offline";
  let liveRefused = false;
  let view: ViewHandle | undefined;
  let inFlight: string | undefined;
  /** Batches sent while the page was hidden, beside the one in flight (flushOnHide); the queue sends them again in turn. */
  const sentEarly = new Set<string>();
  let attempt = 0, failures = 0, catchAttempt = 0, reloadAttempt = 0;
  let retry: ReturnType<typeof setTimeout> | undefined, catchRetry: ReturnType<typeof setTimeout> | undefined, reloadRetry: ReturnType<typeof setTimeout> | undefined;
  let loading: Promise<void> | undefined;
  let catching = false, catchAgain = false;
  const targets = new Map<string, PageTargetInfo>();
  const asked = new Set<string>();
  /** Page targets to read again (their page changed, was revoked or restored); the card keeps what it shows meanwhile. */
  const stale = new Set<string>();
  // A page edited live saves often: its cards' titles are read at most once a second.
  const restale = throttleLatest<void>(1000, () => resolveTargets());
  const remote = new Map<string, RemoteDrag & { timer: ReturnType<typeof setTimeout> }>();
  const emit = () => { if (!disposed) changed(); };
  const frame = deps.frame ?? (typeof requestAnimationFrame === "function" ? requestAnimationFrame : (run: () => void) => setTimeout(run, 16));
  let framed = false;
  /** What others do (their batches, drag previews, presence) is told once per frame: 30 people send more often than a screen draws (COL2-1). */
  const emitSoon = () => {
    if (framed || disposed) return;
    framed = true;
    frame(() => { framed = false; emit(); });
  };
  const roster = createPresenceRoster(emitSoon, deps.objectPresenceTtlMs);
  /** The objects of the cards whose presence is listened to, with the stop of each. */
  const watched = new Map<string, () => void>();
  /** The backoff before the next attempt, and no less than the server's Retry-After. */
  const wait = (failure: unknown, attempts: number) => Math.max(retryMs(attempts), Math.min(failure instanceof ApiError ? failure.retryAfterMs ?? 0 : 0, maxRetryAfterMs));
  const unsent = () => model?.pending.filter(batch => batch.acknowledged === undefined) ?? [];

  /** Listens to the objects of the live cards (presence, changes), and stops for those gone. */
  function watchObjects() {
    if (!view) return;
    const ids = new Set((model?.items ?? []).flatMap(item => (item.targetResourceId && !item.deleted ? [item.targetResourceId.toLowerCase()] : [])));
    for (const [id, stop] of watched) if (!ids.has(id)) { stop(); watched.delete(id); }
    for (const id of ids) if (!watched.has(id)) watched.set(id, view.watch(id, sourceListener(id)));
  }
  /** A card's source changed, was missed, revoked or restored (COL2-8): a page card's target is read again (its title, whether the
   * reader may still open it), an object card's live object follows; what the reader may not read any more arrives as nothing. */
  function sourceListener(id: string): ObjectListener {
    const target = (kind: BoardItem["kind"]) => (model?.items ?? []).find(item => item.kind === kind && !item.deleted && item.targetResourceId?.toLowerCase() === id)?.targetResourceId;
    const page = () => {
      const resourceId = target("page");
      // Not resolved yet: the first lookup is on its way.
      if (!resourceId || !targets.has(resourceId)) return;
      asked.delete(resourceId);
      stale.add(resourceId);
      restale.push();
    };
    return {
      presence: presence => roster.receive(presence),
      changed(change, version) {
        if (isCommentsChange(change)) return;
        page();
        const objectId = target("object");
        if (objectId) objects.apply({ objectId, version, change });
      },
      missed() {
        page();
        const objectId = target("object");
        if (objectId && objects.get(objectId)) void objects.refresh(objectId);
      },
      revoked() {
        page();
        const objectId = target("object");
        if (objectId) objects.revoke(objectId);
      },
    };
  }
  /** Through which view a colleague works at a card's object, as this reader can name it; null: elsewhere. */
  function viewTitle(via: string | null) {
    if (!via) return null;
    if (via === viewKey) return t.session.thisBoard;
    const [kind, id] = via.split(":");
    if (kind === "page") { const title = id ? targets.get(id)?.title : null; return t.session.page(title ?? ""); }
    return kind === "board" ? t.session.otherBoard : null;
  }
  function editorsOf(objectId: string): CollaborationPeer[] | undefined {
    const people = roster.of(objectId);
    return people.length ? people.map(person => {
      const name = person.displayName || t.session.colleague;
      return { clientId: person.clientId, id: person.userId, name, color: person.color, cursorAvailable: false, activity: presenceActivity(name, viewTitle(person.via)) };
    }) : undefined;
  }

  /**
   * The items as the canvas draws them, the same array while none of them changed and the same object for each that did not (COL2-1):
   * a colleague's drag preview or a batch that moved one card must not hand the canvas every card anew.
   */
  let shownItems: readonly BoardViewItem[] = [];
  const decorated = new WeakMap<BoardItem, { view: BoardViewItem; key: string }>();
  function viewItems(): readonly BoardViewItem[] {
    const next = (model?.items ?? []).map(item => {
      if (!item.targetResourceId) return item;
      const editors = editorsOf(item.targetResourceId);
      const target = item.kind === "page" ? targets.get(item.targetResourceId) : undefined;
      if (item.kind !== "page" && !editors) return item;
      const known = decorated.get(item);
      const key = editors?.map(peer => `${peer.clientId}|${peer.id}|${peer.name}|${peer.color}|${peer.activity}`).join("\n") ?? "";
      if (known && known.view.target === target && known.key === key) return known.view;
      const view: BoardViewItem = { ...item, ...(item.kind === "page" && { target }), ...(editors && { editors }) };
      decorated.set(item, { view, key });
      return view;
    });
    if (next.length !== shownItems.length || next.some((item, index) => item !== shownItems[index])) shownItems = next;
    return shownItems;
  }

  function resolveTargets() {
    watchObjects();
    const unknown = [...new Set((model?.items ?? []).flatMap(item => item.kind === "page" && item.targetResourceId && (!targets.has(item.targetResourceId) || stale.has(item.targetResourceId)) && !asked.has(item.targetResourceId) ? [item.targetResourceId] : []))];
    if (!unknown.length) return;
    unknown.forEach(id => { asked.add(id); stale.delete(id); });
    // A chunk that fails is asked again by the next trigger (news, reload), never by itself.
    for (let start = 0; start < unknown.length; start += referencesChunk) {
      const chunk = unknown.slice(start, start + referencesChunk);
      void references(chunk).then(found => {
        const titles = new Map(found.map(page => [page.id, page.title]));
        // A page still readable keeps its space and excerpt; one no longer readable keeps nothing.
        for (const id of chunk) {
          const known = targets.get(id);
          targets.set(id, titles.has(id) ? { ...(known?.available && known), title: titles.get(id)!, available: true } : { title: null, available: false });
        }
        emit();
      }).catch(() => chunk.forEach(id => asked.delete(id)));
    }
  }

  function load(): Promise<void> {
    loading ??= (async () => {
      try {
        clearTimeout(reloadRetry);
        const board = await http.get(boardId);
        if (disposed) return;
        reloadAttempt = 0;
        signedOut = false;
        for (const { target } of board.items) {
          if (target) targets.set(target.resourceId, {
            title: target.title, available: target.available,
            ...(target.spaceTitle != null && { spaceTitle: target.spaceTitle }), ...(target.excerpt != null && { excerpt: target.excerpt }),
            ...(target.canEdit && { canEdit: true }), ...(target.trashed && { trashed: true, ...(target.spaceId && { spaceId: target.spaceId }), ...(target.canRestore && { canRestore: true }) }),
          });
        }
        canEdit = board.canEdit;
        spaceId = board.spaceId;
        ownerPageId = board.ownerPageId ?? "";
        if (model) model.reset(fromBoardDto(board));
        else model = createBoardModel(fromBoardDto(board), { readObject: (id, key) => (objects.get(id)?.data as Record<string, unknown> | undefined)?.[key] ?? null });
        status = "ready";
        revokedReload = false;
        if (!view) {
          view = views.open(viewKey, viewListener);
          view.watch(boardId, boardListener, model.sequence);
        }
        resolveTargets();
        void flush();
      } catch (failure) {
        if (disposed) return;
        const gone = failure instanceof ApiError && (failure.status === 404 || failure.status === 403);
        error = errorMessage(failure);
        if (!model) status = gone ? "missing" : "failed";
        else if (gone) {
          // The board is gone or no longer theirs: read-only, the changes that never reached the server stay in the view as unsaved.
          status = "revoked";
          canEdit = false;
          closeView();
        } else if (outcomeOf(failure) === "signedOut") signedOut = true;
        // Revoked: the board is read until HTTP says whether access is gone.
        else if (revokedReload) reloadRetry = setTimeout(() => void load(), wait(failure, reloadAttempt++));
      } finally {
        loading = undefined;
        emit();
      }
    })();
    return loading;
  }

  function receive(batch: BoardBatch) {
    if (!model) return;
    const result = model.receive(batch);
    if (result === "gap") void catchUp();
    else if (result === "reload") void load();
    resolveTargets();
    emitSoon();
  }

  async function catchUp() {
    if (!model) return;
    if (catching) { catchAgain = true; return; }
    catching = true;
    clearTimeout(catchRetry);
    try {
      do {
        catchAgain = false;
        const answer = await http.after(boardId, model.sequence);
        if (disposed) return;
        for (const batch of answer.batches) if (model.receive(batch) === "reload") { await load(); break; }
      } while (catchAgain);
      catchAttempt = 0;
    } catch (failure) {
      if (disposed) return;
      const outcome = outcomeOf(failure);
      // resync_required (409), or the board is gone (403, 404): the board is loaded again and HTTP decides.
      if (outcome === "refused") await load();
      else if (outcome === "signedOut") signedOut = true;
      else catchRetry = setTimeout(() => void catchUp(), wait(failure, catchAttempt++));
    } finally {
      catching = false;
      resolveTargets();
      emit();
    }
  }

  /** Sends the queue in its order. Left (`disposed`) the page lives on, so the queue still runs to its end, quietly: no reload, no retry. */
  async function flush() {
    if (inFlight || !model || status !== "ready" || signedOut || blocked) return;
    const batch = model.pending.find(item => item.acknowledged === undefined);
    if (!batch) return;
    inFlight = batch.operationId;
    emit();
    try {
      const ack = await http.submit(boardId, batch.operationId, batch.changes);
      attempt = 0;
      failures = 0;
      offline = false;
      const result = model.acknowledge(batch.operationId, ack);
      if (result === "gap" && !disposed) void catchUp();
      else if (result === "reload" && !disposed) void load();
    } catch (failure) {
      const outcome = outcomeOf(failure);
      if (failure instanceof ApiError && outcome === "refused") {
        failures = 0;
        model.reject(batch.operationId);
        rejected = `${(failure.code && rejections[failure.code]) || errorMessage(failure)} ${t.session.reloaded}`;
        if (!disposed) void load();
      } else {
        // shortcut: a left board does not retry a batch that did not get through; retry it after dispose too if losing it shows up.
        if (disposed) return;
        inFlight = undefined;
        if (outcome === "signedOut") { signedOut = true; offline = false; }
        else if (failure instanceof ApiError && failure.status >= 500 && ++failures >= maxServerFailures) { blocked = true; offline = false; }
        else {
          // Not reached, 408, 429 or a server failure: the same operation id again later; the server answers a repeat as a duplicate.
          offline = true;
          clearTimeout(retry);
          retry = setTimeout(() => void flush(), wait(failure, attempt++));
        }
        emit();
        return;
      }
    }
    inFlight = undefined;
    sentEarly.delete(batch.operationId);
    emit();
    void flush();
  }

  /**
   * The page is going away (hidden, a reload, a closed tab): what waits behind the batch in flight would wait for its answer,
   * which may never come (W4-3). It goes now as one batch (a keepalive request, boardApi.submit) without waiting; the queue still
   * sends it in its turn, which the server answers as a duplicate. shortcut: sent beside the one in flight, it may reach the
   * server first and be refused (a change of an item the earlier batch creates); the page was leaving, and the reload says so.
   */
  function flushOnHide() {
    if (!model || status !== "ready" || signedOut || blocked || disposed) return;
    const waiting = unsent().filter(batch => batch.operationId !== inFlight && !sentEarly.has(batch.operationId)).map(batch => batch.operationId);
    const merged = model.coalesce(waiting, maxBatchChanges);
    // Nothing in flight: the queue sends its head itself (setting `inFlight` before it awaits).
    if (!inFlight) void flush();
    for (const batch of merged.filter(entry => entry.operationId !== inFlight)) {
      sentEarly.add(batch.operationId);
      http.submit(boardId, batch.operationId, batch.changes).catch(() => { /* the queue sends it again in its turn */ });
    }
  }

  function closeView() {
    watched.forEach(stop => stop());
    watched.clear();
    view?.close();
    view = undefined;
  }

  function presenceOf(userId: string, displayName: string, color: string, payload: PresencePayload | undefined) {
    clearTimeout(remote.get(userId)?.timer);
    const dragging = Array.isArray(payload?.dragging) ? payload.dragging : [];
    const point = payload?.cursor;
    const cursor = point && Number.isFinite(point.x) && Number.isFinite(point.y) ? { x: point.x, y: point.y } : undefined;
    const line = payload?.connecting;
    const connecting: ConnectingPresence | undefined = line && typeof line.from === "string" && Number.isFinite(line.x) && Number.isFinite(line.y) ? {
      from: line.from, x: line.x, y: line.y, ...(typeof line.to === "string" && { to: line.to }), ...(sides.includes(line.side!) && { side: line.side }),
    } : undefined;
    if (!dragging.length && !cursor && !connecting) remote.delete(userId);
    else remote.set(userId, { userId, displayName, color, dragging, ...(cursor && { cursor }), ...(connecting && { connecting }), timer: setTimeout(() => { remote.delete(userId); emitSoon(); }, presenceTtl) });
    emitSoon();
  }

  const viewListener = {
    // Every new connection state joins the view again; a refusal of that join comes after it.
    status(next: LiveStatus) { connection = next; liveRefused = false; emit(); },
    refused() { liveRefused = true; emit(); },
  };
  /** The board object: its changes are BoardChanged batches (a summary only says it changed), its presence the cursors. */
  const boardListener: ObjectListener = {
    // A comment notice is no change of the board: the comment threads read it themselves.
    changed(change) { if (isBatch(change)) receive(change); else if (!isCommentsChange(change)) void catchUp(); },
    missed() { void catchUp(); },
    presence({ userId, displayName, color, payload }) {
      if (typeof userId === "string") presenceOf(userId, typeof displayName === "string" ? displayName : "", typeof color === "string" ? color : "", (payload ?? undefined) as PresencePayload | undefined);
    },
    revoked() {
      // Which does not say the board is lost: load it again and let HTTP decide (see `load`).
      revokedReload = true;
      void load();
    },
  };
  void load();

  const editable = () => !!model && canEdit && status === "ready";
  /** An undo or redo: its board changes go as a batch, its object edits through the object path. A refusal reads the object again
   * (liveObjects) and is said as a refused batch is, in the words the card says it with; the board's undo has moved on. */
  function travel(step: (() => UndoStep | null) | undefined) {
    const done = editable() ? step?.() : null;
    if (!done) return;
    for (const edit of done.changes) if (isObjectEdit(edit)) objects.edit(edit.objectId, edit.after).catch(failure => { rejected = errorMessage(failure); emit(); });
    emit();
    if (done.changes.some(edit => !isObjectEdit(edit))) void flush();
  }
  return {
    get state(): BoardSessionState {
      return {
        status, error, spaceId, ownerPageId, canEdit, offline, rejected, connection, liveRefused, signedOut,
        blocked: blocked ? unsent().length : 0,
        title: model?.title ?? "",
        settings: model?.settings ?? defaultBoardSettings,
        items: viewItems(),
        pending: model?.pending.length ?? 0,
        saving: !!inFlight,
        canUndo: editable() && !!model?.canUndo,
        canRedo: editable() && !!model?.canRedo,
        remote: [...remote.values()].map(({ timer: _timer, ...user }) => user),
      };
    },
    /**
     * One gesture; `coalesce` names an editing whose saves are one undo step. On a hidden page it goes at once (flushOnHide). An
     * object edit (a contact card's field) is written by the object path already: here it is only a step of the board's undo (P6).
     */
    apply(changes: readonly BoardEdit[], coalesce?: string) {
      if (!editable() || !changes.length) return;
      const batch = model!.apply(changes, coalesce);
      emit();
      if (!batch) return;
      watchObjects();
      if (document.visibilityState === "hidden") flushOnHide(); else void flush();
    },
    flushOnHide,
    undo() { travel(model?.undo); },
    redo() { travel(model?.redo); },
    presence(payload: PresencePayload) { if (editable()) view?.presence(boardId, compact(payload)); },
    /** The objects of the cards on screen (BoardCanvas), which get their changes and presence in detail. */
    viewport(objectIds: readonly string[]) { view?.viewport(objectIds); },
    /** The board's view session, for what its cards show live (a page preview). */
    get view() { return view; },
    /** The title the picker showed for a page the user just placed, so the card does not wait for a lookup. */
    notePage(resourceId: string, title: string) { targets.set(resourceId, { title, available: true }); },
    dismissRejection() { rejected = ""; emit(); },
    /** After a batch the server keeps failing on: sends again from the head batch, every pending batch kept, the attempts counted anew. */
    sendAgain() {
      blocked = false;
      failures = attempt = 0;
      clearTimeout(retry);
      emit();
      void flush();
    },
    /** After a batch the server keeps failing on: the failed batch and those behind it are dropped and the board is loaded as the server has it. */
    discardQueued() {
      if (!model) return;
      for (const batch of unsent()) model.reject(batch.operationId);
      blocked = false;
      failures = attempt = 0;
      offline = false;
      emit();
      void load();
    },
    /** After a 401 the shell could not settle: loads the board again, which also sends what waits. */
    resume() { signedOut = false; emit(); void load(); },
    /** Loads the board again (after a failed first load). */
    retry() { void load(); },
    /** The board is left: what waits goes now as on a hidden page (UX-B-2); the queue still sends it all in its order after the batch in
     * flight, so a batch that reached the server too early (and was refused) goes again in its turn. */
    dispose() {
      flushOnHide();
      disposed = true;
      clearTimeout(retry);
      clearTimeout(catchRetry);
      clearTimeout(reloadRetry);
      remote.forEach(entry => clearTimeout(entry.timer));
      roster.dispose();
      restale.cancel();
      closeView();

    },
  };
}
export type BoardSession = ReturnType<typeof createBoardSession>;
