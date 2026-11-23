import type { MarkdownEditorState, MarkdownPlugin } from "@atlas/markdown/core";
import type { Editor } from "@tiptap/core";
import type { DecorationSet } from "@tiptap/pm/view";
import { absolutePositionToRelativePosition, relativePositionToAbsolutePosition, yCursorPluginKey, ySyncPluginKey } from "@tiptap/y-tiptap";
import type { Awareness } from "y-protocols/awareness";
import * as Y from "yjs";
import { animateAtlas, literalMs, motionMs, PROTOTYPE_MS, scrollBehavior } from "./motion/aiMotion";
import { COLLABORATION_POINTER_PATH } from "./live/collaborationPointer";
import { createPointerGlide } from "./live/pointerGlide";
import "./collaborationPresence.css";

type StatePeer = MarkdownEditorState["peers"][number];

export interface CollaborationPeer extends StatePeer {
  readonly cursorAvailable: boolean;
  /** What the person does, said instead of the cursor's state ("Jana upravuje přes stránku Provoz" on a board's card). */
  readonly activity?: string;
}

export interface CollaborationPresence {
  readonly plugin: MarkdownPlugin;
  update(peers: MarkdownEditorState["peers"]): void;
  navigateToPeer(clientId: number): boolean;
}

export function createCollaborationPresence(options: {
  onChange(peers: CollaborationPeer[]): void;
  /** Share the mouse pointer over the text and show the co-editors' (a page; a board has its own, BoardPresence). */
  pointers?: boolean;
}): CollaborationPresence {
  let peers: readonly StatePeer[] = [];
  let attachment: Attachment | undefined;

  const publish = () => options.onChange(peers.map(peer => ({
    ...peer,
    cursorAvailable: attachment?.positions.has(peer.clientId) ?? false,
  })));

  return {
    plugin: {
      id: "atlas-collaboration-presence",
      apiVersion: 1,
      create: () => ({
        onAttach: ({ editor, signal }) => {
          attachment?.destroy();
          attachment = attach(editor, () => publish(), signal, options.pointers === true);
          attachment.render(peers);
          publish();
          return () => {
            attachment?.destroy();
            attachment = undefined;
            publish();
          };
        },
      }),
    },
    update(next) {
      // The editor republishes its state for every remote change; the same peers need no new list (the avatars measure themselves).
      if (next === peers || (next.length === 0 && peers.length === 0)) return;
      peers = next;
      attachment?.render(peers);
      publish();
    },
    navigateToPeer(clientId) {
      return attachment?.navigate(clientId) ?? false;
    },
  };
}

interface Attachment {
  readonly positions: ReadonlyMap<number, number>;
  render(peers: readonly StatePeer[]): void;
  navigate(clientId: number): boolean;
  destroy(): void;
}

function attach(editor: Editor, changed: () => void, signal: AbortSignal, pointers: boolean): Attachment {
  const editable = editor.view.dom as HTMLElement;
  const container = editable.parentElement ?? editable;
  const overlay = document.createElement("div");
  overlay.className = "collaboration-presence";
  overlay.setAttribute("aria-hidden", "true");
  container.classList.add("collaboration-presence-active");
  const previousPosition = container.style.position;
  container.append(overlay);
  // Reading computed style or geometry forces a synchronous layout of the whole freshly mounted document.
  // Without peers (read mode, solo editing) nothing is measured, positioned or observed at all.
  let positioned = false;
  const ensurePositioned = () => {
    if (positioned) return;
    positioned = true;
    if (getComputedStyle(container).position === "static") container.style.position = "relative";
  };

  const media = window.matchMedia("(prefers-reduced-motion: reduce)");
  let reducedMotion = media.matches;
  const setReducedMotion = (event?: MediaQueryListEvent) => {
    reducedMotion = event?.matches ?? media.matches;
    overlay.classList.toggle("is-reduced-motion", reducedMotion);
  };
  setReducedMotion();
  media.addEventListener("change", setReducedMotion);

  let currentPeers: readonly StatePeer[] = [];
  let positions = new Map<number, number>();
  let layoutFrame = 0;
  let settleFrame = 0;
  const halos = new Set<Animation>();

  let published = "";
  const publishIfChanged = () => {
    const signature = currentPeers.map(peer => `${peer.clientId}:${positions.has(peer.clientId)}`).join("|");
    if (signature === published) return;
    published = signature;
    changed();
  };

  // Measuring forces a layout, so cursors are drawn once per frame, in its animation-frame callback: a burst of
  // transactions and awareness changes costs one layout, and the move still shows in the frame that brought it.
  let drawFrame = 0;
  let pendingLayoutMovement = false;
  const draw = (layoutMovement = false) => {
    // Positions come from the cursor decorations, which every transaction maps; cheap and exact for publish and navigate.
    positions = cursorPositions(editor);
    if (currentPeers.length === 0) {
      cancelAnimationFrame(drawFrame);
      drawFrame = 0;
      overlay.querySelectorAll(".collaboration-presence__cursor").forEach(cursor => cursor.remove());
      placePointers(new Map(), 0, 0);
      publishIfChanged();
      return;
    }
    publishIfChanged();
    pendingLayoutMovement ||= layoutMovement;
    drawFrame ||= requestAnimationFrame(paint);
  };
  const paint = () => {
    drawFrame = 0;
    // A frame already running when the editor went (destroy cancels only the one requested): nothing to measure.
    if (destroyed || currentPeers.length === 0) return;
    ensurePositioned();
    // The editor may have changed since the request; draw where the latest state puts each cursor.
    positions = cursorPositions(editor);
    publishIfChanged();
    // All reads first (one layout), then only writes.
    const containerRect = container.getBoundingClientRect();
    const originX = containerRect.left + container.clientLeft - container.scrollLeft;
    const originY = containerRect.top + container.clientTop - container.scrollTop;
    const measured = new Map(currentPeers.flatMap(peer => {
      const position = positions.get(peer.clientId);
      return position === undefined ? [] : [[peer.clientId, editor.view.coordsAtPos(position)] as const];
    }));
    const pointed = new Map(currentPeers.flatMap(peer => {
      const pointer = remotePointer(peer.clientId);
      return pointer ? [[peer.clientId, pointer] as const] : [];
    }));
    if (pendingLayoutMovement) {
      pendingLayoutMovement = false;
      overlay.classList.add("is-layout-moving");
      cancelAnimationFrame(layoutFrame);
      cancelAnimationFrame(settleFrame);
      layoutFrame = requestAnimationFrame(() => {
        settleFrame = requestAnimationFrame(() => overlay.classList.remove("is-layout-moving"));
      });
    }
    const wanted = new Set(currentPeers.map(peer => peer.clientId));
    overlay.querySelectorAll<HTMLElement>(".collaboration-presence__cursor").forEach(cursor => {
      if (!wanted.has(Number(cursor.dataset.clientId))) cursor.remove();
    });
    for (const peer of currentPeers) {
      const coords = measured.get(peer.clientId);
      let cursor = overlay.querySelector<HTMLElement>(`[data-client-id="${peer.clientId}"]`);
      if (!coords) {
        cursor?.remove();
        continue;
      }
      if (!cursor) {
        cursor = document.createElement("span");
        cursor.className = "collaboration-presence__cursor";
        cursor.dataset.clientId = String(peer.clientId);
        const label = document.createElement("span");
        label.className = "collaboration-presence__label";
        cursor.append(label);
        overlay.append(cursor);
      }
      cursor.querySelector<HTMLElement>(".collaboration-presence__label")!.textContent = peer.name;
      cursor.style.setProperty("--collaboration-color", safeColor(peer.color));
      cursor.style.transform = `translate3d(${coords.left - originX}px, ${coords.top - originY}px, 0)`;
      cursor.style.height = `${Math.max(1, coords.bottom - coords.top)}px`;
    }
    placePointers(pointed, originX, originY);
  };

  // Pointers (spec of the promo film's page scene): each editor publishes where its mouse is in the awareness state, anchored
  // to the nearest document position (a Yjs relative position) plus its offset in px, so it lands on the same word at any
  // window width or scroll. A reader of a live page has no caret extension and so no awareness here: it sends and shows none.
  const awareness = pointers ? caretAwareness(editor) : undefined;
  const binding = () => (ySyncPluginKey.getState(editor.state) as { binding?: SyncBinding } | undefined)?.binding;
  const peer = (clientId: number) => currentPeers.find(entry => entry.clientId === clientId);
  const leavingPointers = new Map<HTMLElement, ReturnType<typeof setTimeout>>();
  const glide = createPointerGlide(() => overlay, (element, x, y) => { element.style.transform = `translate3d(${x}px, ${y}px, 0)`; });
  function remotePointer(clientId: number) {
    const pointer = awareness?.getStates().get(clientId)?.pointer as Partial<PointerState> | null | undefined;
    const sync = binding();
    if (!pointer || !sync || !Number.isFinite(pointer.x) || !Number.isFinite(pointer.y)) return undefined;
    try {
      // Another client's state: an anchor that resolves to nothing (deleted text, garbage) shows no pointer.
      const position = relativePositionToAbsolutePosition(sync.doc, sync.type, Y.createRelativePositionFromJSON(pointer.anchor), sync.mapping as never);
      if (position === null || position < 0 || position > editor.state.doc.content.size) return undefined;
      const at = editor.view.coordsAtPos(position);
      return { left: at.left + clampOffset(pointer.x!), top: at.top + clampOffset(pointer.y!), down: pointer.down === true };
    } catch { return undefined; }
  }
  function placePointers(pointed: ReadonlyMap<number, { left: number; top: number; down: boolean }>, originX: number, originY: number) {
    const shown = new Map([...overlay.querySelectorAll<HTMLElement>(".collaboration-pointer")].map(element => [Number(element.dataset.pointerClientId), element]));
    for (const [clientId, element] of shown) {
      if (pointed.has(clientId) || leavingPointers.has(element)) continue;
      // It fades out where it was last, as on a board.
      element.classList.add("is-leaving");
      leavingPointers.set(element, setTimeout(() => { leavingPointers.delete(element); glide.forget(element); element.remove(); }, motionMs(overlay, "--atlas-motion-surface")));
    }
    for (const [clientId, at] of pointed) {
      const person = peer(clientId)!;
      let element = shown.get(clientId);
      if (!element) {
        element = document.createElement("div");
        element.className = "collaboration-pointer";
        element.dataset.pointerClientId = String(clientId);
        element.innerHTML = `<svg viewBox="0 0 22 26"><path d="${COLLABORATION_POINTER_PATH}"/></svg><span class="collaboration-pointer__name"></span>`;
        overlay.append(element);
      } else if (leavingPointers.has(element)) {
        clearTimeout(leavingPointers.get(element));
        leavingPointers.delete(element);
        element.classList.remove("is-leaving");
      }
      element.querySelector(".collaboration-pointer__name")!.textContent = person.name;
      element.style.setProperty("--collaboration-color", safeColor(person.color));
      // Glued to the text while the page itself moves (scroll anchoring, a dock opening); gliding otherwise.
      glide.to(element, at.left - originX, at.top - originY, overlay.classList.contains("is-layout-moving"));
      element.classList.toggle("is-pressed", at.down);
    }
  }

  // The own pointer: sampled at most every POINTER_MS (the board's 20 a second), the last position always sent.
  let pointerAt: { x: number; y: number } | null = null;
  let pointerDown = false;
  let pointerTimer: ReturnType<typeof setTimeout> | undefined;
  const publishPointer = () => {
    clearTimeout(pointerTimer);
    pointerTimer = undefined;
    const sync = binding();
    if (destroyed || !awareness || !pointerAt || !sync) return;
    // Beside the text (a margin, under the last line) the position is the nearest edge's; the offset keeps the distance.
    const box = editable.getBoundingClientRect();
    const hit = editor.view.posAtCoords({
      left: Math.min(Math.max(pointerAt.x, box.left + 1), box.right - 1),
      top: Math.min(Math.max(pointerAt.y, box.top + 1), box.bottom - 1),
    });
    if (!hit) return;
    const at = editor.view.coordsAtPos(hit.pos);
    const state: PointerState = {
      anchor: Y.relativePositionToJSON(absolutePositionToRelativePosition(hit.pos, sync.type, sync.mapping as never)),
      x: Math.round(pointerAt.x - at.left),
      y: Math.round(pointerAt.y - at.top),
      ...(pointerDown ? { down: true } : {}),
    };
    awareness.setLocalStateField("pointer", state);
  };
  const hidePointer = () => {
    clearTimeout(pointerTimer);
    pointerTimer = undefined;
    pointerAt = null;
    pointerDown = false;
    if (awareness?.getLocalState()?.pointer) awareness.setLocalStateField("pointer", null);
  };
  const onPointer = (event: PointerEvent) => {
    // A finger is no pointer anyone follows; a reader or a read-only view does not point either.
    if (event.pointerType === "touch" || !editor.isEditable) return;
    pointerAt = { x: event.clientX, y: event.clientY };
    if (event.type === "pointerdown" || event.type === "pointerup") {
      // A click is shorter than a sample: the press goes out at once.
      pointerDown = event.type === "pointerdown";
      publishPointer();
    } else pointerTimer ??= setTimeout(publishPointer, POINTER_MS);
  };
  const onVisibility = () => { if (document.visibilityState === "hidden") hidePointer(); };
  const onRemotePointers = ({ added, updated, removed }: { added: number[]; updated: number[]; removed: number[] }) => {
    if (currentPeers.length && [...added, ...updated, ...removed].some(id => id !== awareness?.clientID)) draw();
  };
  if (awareness) {
    for (const type of ["pointermove", "pointerdown", "pointerup"] as const) container.addEventListener(type, onPointer);
    container.addEventListener("pointerleave", hidePointer);
    window.addEventListener("blur", hidePointer);
    document.addEventListener("visibilitychange", onVisibility);
    awareness.on("change", onRemotePointers);
  }

  const onTransaction = () => draw();
  const onLayout = () => draw(true);
  editor.on("transaction", onTransaction);
  // Scroll, resize and size observation only move drawn cursors; keep them off while nobody else is here.
  const resizeObserver = new ResizeObserver(onLayout);
  let observing = false;
  const observeLayout = (active: boolean) => {
    if (active === observing) return;
    observing = active;
    if (active) {
      window.addEventListener("scroll", onLayout, true);
      window.addEventListener("resize", onLayout);
      resizeObserver.observe(container);
    } else {
      window.removeEventListener("scroll", onLayout, true);
      window.removeEventListener("resize", onLayout);
      resizeObserver.disconnect();
    }
  };

  let destroyed = false;
  const destroy = () => {
    if (destroyed) return;
    destroyed = true;
    editor.off("transaction", onTransaction);
    observeLayout(false);
    media.removeEventListener("change", setReducedMotion);
    cancelAnimationFrame(drawFrame);
    cancelAnimationFrame(layoutFrame);
    cancelAnimationFrame(settleFrame);
    halos.forEach(halo => halo.cancel());
    if (awareness) {
      hidePointer();
      for (const type of ["pointermove", "pointerdown", "pointerup"] as const) container.removeEventListener(type, onPointer);
      container.removeEventListener("pointerleave", hidePointer);
      window.removeEventListener("blur", hidePointer);
      document.removeEventListener("visibilitychange", onVisibility);
      awareness.off("change", onRemotePointers);
    }
    leavingPointers.forEach(timer => clearTimeout(timer));
    glide.stop();
    signal.removeEventListener("abort", destroy);
    overlay.remove();
    container.classList.remove("collaboration-presence-active");
    container.style.position = previousPosition;
  };
  signal.addEventListener("abort", destroy, { once: true });

  return {
    get positions() { return positions; },
    render(next) {
      currentPeers = next;
      observeLayout(next.length > 0);
      draw();
    },
    navigate(clientId) {
      // A click may land between a change and its frame: draw now so the halo finds the cursor.
      if (drawFrame) {
        cancelAnimationFrame(drawFrame);
        paint();
      }
      const position = positions.get(clientId);
      const cursor = overlay.querySelector<HTMLElement>(`[data-client-id="${clientId}"]`);
      if (position === undefined || !cursor) return false;
      scrollCaretIntoView(editor, position, reducedMotion ? "auto" : scrollBehavior(cursor));
      // The halo is one ripple of the cursor (prototype `rippleOnce`): the class lives exactly as long as the animation.
      cursor.classList.add("is-highlighted");
      const halo = animateAtlas(cursor, [
        { boxShadow: "0 0 0 0 color-mix(in srgb, var(--collaboration-color) 65%, transparent)" },
        { boxShadow: "0 0 0 14px transparent" },
      ], { duration: literalMs(cursor, PROTOTYPE_MS.ripple), fill: "none" });
      if (halo) {
        halos.add(halo);
        const done = () => { halos.delete(halo); cursor.classList.remove("is-highlighted"); };
        halo.addEventListener("finish", done);
        halo.addEventListener("cancel", done);
      } else cursor.classList.remove("is-highlighted");
      return true;
    },
    destroy,
  };
}

const POINTER_MS = 50;
/** Beyond this a remote offset is no pointer over this page (the server passes the field through as the client wrote it). */
const POINTER_REACH = 4096;
interface PointerState { anchor: unknown; x: number; y: number; down?: true }
interface SyncBinding { doc: Y.Doc; type: Y.XmlFragment; mapping: unknown }

/** The awareness the caret extension publishes to (useCollab binds it to the provider's). */
function caretAwareness(editor: Editor) {
  const caret = editor.extensionManager.extensions.find(extension => extension.name === "collaborationCaret");
  return (caret?.options as { provider?: { awareness?: Awareness } } | undefined)?.provider?.awareness;
}

function clampOffset(value: number) {
  return Math.min(POINTER_REACH, Math.max(-POINTER_REACH, value));
}

function cursorPositions(editor: Editor) {
  const result = new Map<number, number>();
  const decorations = yCursorPluginKey.getState(editor.state) as DecorationSet | undefined;
  decorations?.find().forEach(decoration => {
    const clientId = Number(decoration.spec.key);
    if (decoration.from === decoration.to && Number.isInteger(clientId)) result.set(clientId, decoration.from);
  });
  return result;
}

function safeColor(color: string) {
  return CSS.supports("color", color) ? color : "#64748b";
}

function scrollCaretIntoView(editor: Editor, position: number, behavior: ScrollBehavior) {
  const coords = editor.view.coordsAtPos(position);
  let top = coords.top;
  let bottom = coords.bottom;
  for (let ancestor = editor.view.dom.parentElement; ancestor; ancestor = ancestor.parentElement) {
    if (ancestor === document.body || ancestor === document.documentElement || ancestor === document.scrollingElement) continue;
    const style = getComputedStyle(ancestor);
    if (!/(auto|scroll|overlay)/.test(style.overflowY) || ancestor.scrollHeight <= ancestor.clientHeight) continue;
    const rect = ancestor.getBoundingClientRect();
    const delta = bottom > rect.bottom - 24
      ? bottom - (rect.bottom - 24)
      : top < rect.top + 24 ? top - (rect.top + 24) : 0;
    if (!delta) continue;
    ancestor.scrollBy({ top: delta, behavior });
    top -= delta;
    bottom -= delta;
  }
  const safeTop = viewportSafeTop(editor);
  const viewportDelta = bottom > window.innerHeight - 24
    ? bottom - (window.innerHeight - 24)
    : top < safeTop ? top - safeTop : 0;
  if (viewportDelta) window.scrollBy({ top: viewportDelta, behavior });
}

function viewportSafeTop(editor: Editor) {
  const workspace = editor.view.dom.closest(".workspace");
  const header = workspace?.querySelector<HTMLElement>(":scope > .workspace-bar");
  return header ? Math.max(24, header.getBoundingClientRect().bottom + 24) : 24;
}
