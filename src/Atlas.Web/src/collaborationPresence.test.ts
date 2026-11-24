import { Extension, Editor } from "@tiptap/core";
import Collaboration from "@tiptap/extension-collaboration";
import CollaborationCaret from "@tiptap/extension-collaboration-caret";
import Document from "@tiptap/extension-document";
import Paragraph from "@tiptap/extension-paragraph";
import Text from "@tiptap/extension-text";
import { Decoration, DecorationSet } from "@tiptap/pm/view";
import { Plugin } from "@tiptap/pm/state";
import { absolutePositionToRelativePosition, yCursorPluginKey, ySyncPluginKey } from "@tiptap/y-tiptap";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { Awareness, applyAwarenessUpdate, encodeAwarenessUpdate } from "y-protocols/awareness";
import * as Y from "yjs";
import type { MarkdownEditorState } from "@atlas/markdown/core";
import { createCollaborationPresence, pointerScale } from "./collaborationPresence";

type Peer = MarkdownEditorState["peers"][number];

const peer = (clientId: number, name = "Ada"): Peer => ({
  clientId,
  id: `user-${clientId}`,
  name,
  color: clientId === 1 ? "#d946ef" : "#0284c7",
});

function editorWithCursors(initial: Record<number, number>) {
  const cursorState = Extension.create({
    name: "test-cursors",
    addProseMirrorPlugins() {
      return [new Plugin({
        key: yCursorPluginKey,
        state: {
          init: (_, state) => decorations(state.doc, initial),
          apply: (transaction, current) => {
            const next = transaction.getMeta(yCursorPluginKey) as Record<number, number> | undefined;
            return next ? decorations(transaction.doc, next) : current.map(transaction.mapping, transaction.doc);
          },
        },
        props: { decorations: state => yCursorPluginKey.getState(state) },
      })];
    },
  });
  const host = document.createElement("div");
  document.body.append(host);
  const editor = new Editor({
    element: host,
    extensions: [Document, Paragraph, Text, cursorState],
    content: "abcdef",
  });
  vi.spyOn(editor.view, "coordsAtPos").mockImplementation(pos => ({
    left: 100 + pos * 8, right: 101 + pos * 8, top: 60, bottom: 80,
  }));
  return { editor, host, move: (positions: Record<number, number>) => editor.view.dispatch(editor.state.tr.setMeta(yCursorPluginKey, positions)) };
}

function decorations(doc: Parameters<typeof DecorationSet.create>[0], positions: Record<number, number>) {
  return DecorationSet.create(doc, Object.entries(positions).map(([id, pos]) =>
    Decoration.widget(pos, () => document.createElement("span"), { key: id }),
  ));
}

// Cursors are drawn in animation frames; the tests run each frame by hand.
let frames = new Map<number, FrameRequestCallback>();
let frameIds = 0;
const frame = (at = performance.now()) => {
  const due = [...frames.values()];
  frames = new Map();
  due.forEach(callback => callback(at));
};
beforeEach(() => {
  vi.spyOn(globalThis, "requestAnimationFrame").mockImplementation(callback => { frames.set(++frameIds, callback); return frameIds; });
  vi.spyOn(globalThis, "cancelAnimationFrame").mockImplementation(id => { frames.delete(id); });
});

const attached: Array<() => void> = [];
afterEach(() => {
  frames.clear();
  Reflect.deleteProperty(Element.prototype, "animate");
  attached.splice(0).reverse().forEach(cleanup => cleanup());
  document.body.replaceChildren();
  vi.restoreAllMocks();
});

function attach(initial: Record<number, number>, peers: readonly Peer[]) {
  const source = editorWithCursors(initial);
  const changes: unknown[] = [];
  const presence = createCollaborationPresence({ onChange: value => changes.push(value) });
  const abort = new AbortController();
  const removeAbortListener = vi.spyOn(abort.signal, "removeEventListener");
  const cleanup = presence.plugin.create({ profile: "rich", signal: abort.signal }).onAttach!({
    editor: source.editor,
    controller: {} as never,
    signal: abort.signal,
  }) as () => void;
  attached.push(() => { cleanup(); source.editor.destroy(); source.host.remove(); });
  presence.update(peers);
  frame();
  return { ...source, presence, changes, cleanup, removeAbortListener };
}

describe("collaboration cursor presentation", () => {
  it("publishes peers before a view exists and resolves keyed cursor decorations after attachment", () => {
    const changes: Array<readonly { clientId: number; cursorAvailable: boolean }[]> = [];
    const presence = createCollaborationPresence({ onChange: value => changes.push(value) });
    presence.update([peer(1)]);
    expect(changes.at(-1)).toEqual([expect.objectContaining({ clientId: 1, cursorAvailable: false })]);

    const source = editorWithCursors({ 1: 3 });
    const abort = new AbortController();
    const cleanup = presence.plugin.create({ profile: "rich", signal: abort.signal }).onAttach!({ editor: source.editor, controller: {} as never, signal: abort.signal }) as () => void;
    attached.push(() => { cleanup(); source.editor.destroy(); source.host.remove(); });

    expect(changes.at(-1)).toEqual([expect.objectContaining({ clientId: 1, cursorAvailable: true })]);
    frame();
    expect(source.host.parentElement!.querySelector('[data-client-id="1"]')).not.toBeNull();
  });

  it("draws an agent's caret in the AI look with the AI glyph, and fades it out where it wrote", () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const agent: Peer = { clientId: 3, id: "ai:0f3c", name: "Jana · AI (MCP)", color: "#7c3aed" };
    const { host, presence } = attach({ 1: 2, 3: 5 }, [peer(1), agent]);
    const caret = host.querySelector<HTMLElement>('[data-client-id="3"]')!;
    expect(caret.classList).toContain("is-agent");
    expect(caret.style.getPropertyValue("--collaboration-color")).toBe("");
    // Its stars write while the text lands, then play „done“ once.
    expect(caret.querySelector("[data-ai-glyph]")!.getAttribute("data-state")).toBe("writing");
    vi.advanceTimersByTime(1600);
    frame();
    expect(caret.querySelector("[data-ai-glyph]")!.getAttribute("data-state")).toBe("done");
    expect(caret.textContent).toBe("Jana · AI (MCP)");
    // A person's caret goes at once, the agent's fades out first.
    presence.update([]);
    expect(host.querySelector('[data-client-id="1"]')).toBeNull();
    expect(caret.classList).toContain("is-leaving");
    vi.advanceTimersByTime(400);
    expect(caret.isConnected).toBe(false);
    vi.useRealTimers();
  });

  it("publishes nothing when the editor republishes the same peers (perf guard)", () => {
    const changes: unknown[] = [];
    const presence = createCollaborationPresence({ onChange: value => changes.push(value) });
    const peers = [peer(1)];
    presence.update(peers);
    presence.update(peers);
    presence.update([]);
    presence.update([]);
    expect(changes).toHaveLength(2);
  });

  it("shows an agent writing again and again as one avatar, its latest write's", () => {
    const agent = (clientId: number): Peer => ({ clientId, id: "ai:jana", name: "Jana · AI (MCP)", color: "#7c3aed" });
    const { changes } = attach({}, [agent(7), peer(1), agent(8), agent(9)]);
    expect((changes.at(-1) as { clientId: number }[]).map(entry => entry.clientId)).toEqual([1, 9]);
  });

  it("keeps equal names distinct by client id and removes peers that leave", () => {
    const { host, presence } = attach({ 1: 2, 2: 4 }, [peer(1, "Sam"), peer(2, "Sam")]);
    expect(host.parentElement!.querySelectorAll(".collaboration-presence__cursor")).toHaveLength(2);
    presence.update([peer(2, "Sam")]);
    frame();
    expect(host.parentElement!.querySelector('[data-client-id="1"]')).toBeNull();
    expect(host.parentElement!.querySelector('[data-client-id="2"]')!.textContent).toBe("Sam");
  });

  it("moves overlays without mutating the editor selection and navigation halos the known cursor", () => {
    const halo = Object.assign(new EventTarget(), { cancel: vi.fn(), finished: Promise.resolve() }) as unknown as Animation;
    const animate = vi.fn((_frames: Keyframe[], _options?: KeyframeAnimationOptions) => halo);
    Object.defineProperty(Element.prototype, "animate", { configurable: true, writable: true, value: animate });
    const { editor, move, presence, host } = attach({ 1: 2 }, [peer(1)]);
    const selection = editor.state.selection;
    move({ 1: 5 });
    expect(editor.state.selection.eq(selection)).toBe(true);
    expect(presence.navigateToPeer(1)).toBe(true);
    expect(editor.state.selection.eq(selection)).toBe(true);
    const cursor = host.parentElement!.querySelector('[data-client-id="1"]')!;
    expect(cursor.classList).toContain("is-highlighted");
    // The halo is one animation of the cursor, and the highlight ends with it, not with a timer.
    expect(animate).toHaveBeenCalledTimes(1);
    expect(animate.mock.calls[0]![0]).toEqual([
      expect.objectContaining({ boxShadow: expect.stringContaining("0 0 0 0") }),
      expect.objectContaining({ boxShadow: "0 0 0 14px transparent" }),
    ]);
    halo.dispatchEvent(new Event("finish"));
    expect(cursor.classList).not.toContain("is-highlighted");
    expect(presence.navigateToPeer(99)).toBe(false);
  });

  it("scrolls the exact caret within a long block and measures its real box", () => {
    let reducedListener: ((event: MediaQueryListEvent) => void) | undefined;
    vi.mocked(window.matchMedia).mockReturnValue({
      matches: false,
      addEventListener: (_: string, next: EventListenerOrEventListenerObject) => {
        reducedListener = next as (event: MediaQueryListEvent) => void;
      },
      removeEventListener: vi.fn(),
    } as unknown as MediaQueryList);
    const { editor, host, presence } = attach({ 1: 4 }, [peer(1)]);
    const scroller = document.createElement("div");
    scroller.style.overflowY = "auto";
    document.body.append(scroller);
    scroller.append(host);
    Object.defineProperties(scroller, {
      clientHeight: { value: 400 },
      scrollHeight: { value: 1600 },
    });
    vi.spyOn(scroller, "getBoundingClientRect").mockReturnValue({
      left: 0, right: 700, top: 100, bottom: 500, width: 700, height: 400,
      x: 0, y: 100, toJSON: () => ({}),
    });
    const scrollBy = vi.fn();
    scroller.scrollBy = scrollBy;
    vi.mocked(editor.view.coordsAtPos).mockReturnValue({ left: 200, right: 202, top: 900, bottom: 944 });
    Object.defineProperties(host, {
      clientLeft: { value: 2 }, clientTop: { value: 3 },
      scrollLeft: { value: 5 }, scrollTop: { value: 7 },
    });
    vi.spyOn(host, "getBoundingClientRect").mockReturnValue({
      left: 20, right: 720, top: 30, bottom: 1030, width: 700, height: 1000,
      x: 20, y: 30, toJSON: () => ({}),
    });
    editor.view.dispatch(editor.state.tr.setMeta("remeasure", true));
    const selection = editor.state.selection;
    const focus = document.activeElement;

    expect(presence.navigateToPeer(1)).toBe(true);
    expect(scrollBy).toHaveBeenCalledWith({ top: 468, behavior: "smooth" });
    expect(editor.state.selection.eq(selection)).toBe(true);
    expect(document.activeElement).toBe(focus);
    const cursor = host.querySelector<HTMLElement>('[data-client-id="1"]')!;
    expect(cursor.style.transform).toBe("translate3d(183px, 874px, 0px)");
    expect(cursor.style.height).toBe("44px");

    scrollBy.mockClear();
    reducedListener!({ matches: true } as MediaQueryListEvent);
    presence.navigateToPeer(1);
    expect(scrollBy).toHaveBeenCalledWith({ top: 468, behavior: "auto" });
  });

  it("scrolls the viewport when the caret is outside the visible root", () => {
    const { editor, presence } = attach({ 1: 4 }, [peer(1)]);
    vi.mocked(editor.view.coordsAtPos).mockReturnValue({ left: 200, right: 202, top: 1200, bottom: 1220 });
    Object.defineProperty(window, "innerHeight", { value: 800, configurable: true });
    const scrollBy = vi.spyOn(window, "scrollBy").mockImplementation(() => {});

    expect(presence.navigateToPeer(1)).toBe(true);
    expect(scrollBy).toHaveBeenCalledWith({ top: 444, behavior: "smooth" });
  });

  it("keeps an upward caret below its workspace's sticky header", () => {
    const { editor, host, presence } = attach({ 1: 4 }, [peer(1)]);
    const unrelated = document.createElement("header");
    unrelated.className = "workspace-bar";
    vi.spyOn(unrelated, "getBoundingClientRect").mockReturnValue({
      left: 0, right: 100, top: 0, bottom: 300, width: 100, height: 300, x: 0, y: 0, toJSON: () => ({}),
    });
    const workspace = document.createElement("main");
    workspace.className = "workspace";
    const header = document.createElement("header");
    header.className = "workspace-bar";
    vi.spyOn(header, "getBoundingClientRect").mockReturnValue({
      left: 0, right: 800, top: 0, bottom: 56, width: 800, height: 56, x: 0, y: 0, toJSON: () => ({}),
    });
    document.body.append(unrelated, workspace);
    workspace.append(header, host);
    vi.mocked(editor.view.coordsAtPos).mockReturnValue({ left: 200, right: 202, top: -100, bottom: -80 });
    const scrollBy = vi.spyOn(window, "scrollBy").mockImplementation(() => {});

    presence.navigateToPeer(1);

    expect(scrollBy).toHaveBeenCalledWith({ top: -180, behavior: "smooth" });
  });

  it("suppresses transitions for layout movement and responds to reduced-motion changes", () => {
    let reduced = false;
    let listener: ((event: MediaQueryListEvent) => void) | undefined;
    vi.mocked(window.matchMedia).mockReturnValue({
      matches: reduced,
      addEventListener: (_: string, next: EventListenerOrEventListenerObject) => {
        listener = next as (event: MediaQueryListEvent) => void;
      },
      removeEventListener: vi.fn(),
    } as unknown as MediaQueryList);
    const { host } = attach({ 1: 2 }, [peer(1)]);
    const overlay = host.parentElement!.querySelector(".collaboration-presence")!;
    window.dispatchEvent(new Event("scroll"));
    frame();
    expect(overlay.classList).toContain("is-layout-moving");
    frame();
    frame();
    expect(overlay.classList).not.toContain("is-layout-moving");
    reduced = true;
    listener!({ matches: reduced } as MediaQueryListEvent);
    expect(overlay.classList).toContain("is-reduced-motion");
  });

  it("measures, observes and republishes nothing while no peer is present", () => {
    const computedStyle = vi.spyOn(window, "getComputedStyle");
    const addListener = vi.spyOn(window, "addEventListener");
    const { editor, host, changes } = attach({}, []);
    const geometry = vi.spyOn(host, "getBoundingClientRect");
    const published = changes.length;

    editor.view.dispatch(editor.state.tr.insertText("x", 1));
    editor.view.dispatch(editor.state.tr.setMeta("remeasure", true));

    expect(computedStyle).not.toHaveBeenCalled();
    expect(geometry).not.toHaveBeenCalled();
    expect(addListener).not.toHaveBeenCalledWith("scroll", expect.any(Function), true);
    expect(changes).toHaveLength(published);
    expect(host.querySelector(".collaboration-presence")!.childElementCount).toBe(0);
  });

  it("draws at most once per frame for a burst of transactions and awareness changes (perf guard)", () => {
    const { editor, host, move, presence } = attach({ 1: 2, 2: 4 }, [peer(1), peer(2)]);
    const geometry = vi.spyOn(host, "getBoundingClientRect");
    const coords = vi.mocked(editor.view.coordsAtPos);
    coords.mockClear();

    for (let i = 0; i < 20; i++) editor.view.dispatch(editor.state.tr.insertText("x", 1));
    move({ 1: 3, 2: 5 });
    presence.update([peer(1), peer(2, "Bob")]);
    window.dispatchEvent(new Event("scroll"));

    // Nothing is measured while the burst runs, and one frame is waiting.
    expect(geometry).not.toHaveBeenCalled();
    expect(coords).not.toHaveBeenCalled();
    expect(frames.size).toBe(1);
    frame();
    expect(geometry).toHaveBeenCalledTimes(1);
    expect(coords).toHaveBeenCalledTimes(2);
    expect(host.querySelector('[data-client-id="2"]')!.textContent).toBe("Bob");
  });

  it("draws a cursor where the latest document puts it, mapped through edits before it", () => {
    const { editor, host, move } = attach({ 1: 2 }, [peer(1)]);
    const coords = vi.mocked(editor.view.coordsAtPos);
    move({ 1: 5 });
    editor.view.dispatch(editor.state.tr.insertText("abc", 1));
    coords.mockClear();
    frame();
    expect(coords.mock.calls.map(([position]) => position)).toEqual([8]);
    expect(host.querySelector<HTMLElement>('[data-client-id="1"]')!.style.transform).toBe(`translate3d(${100 + 8 * 8}px, 60px, 0px)`);
  });

  it("publishes a cursor's availability at once, without waiting for its frame", () => {
    const { changes, move } = attach({}, [peer(1)]);
    expect(changes.at(-1)).toEqual([expect.objectContaining({ clientId: 1, cursorAvailable: false })]);
    move({ 1: 3 });
    expect(changes.at(-1)).toEqual([expect.objectContaining({ clientId: 1, cursorAvailable: true })]);
  });

  it("cancels a waiting frame on detach", () => {
    const { editor, cleanup } = attach({ 1: 2 }, [peer(1)]);
    editor.view.dispatch(editor.state.tr.insertText("x", 1));
    expect(frames.size).toBe(1);
    cleanup();
    expect(frames.size).toBe(0);
    attached.splice(0);
    editor.destroy();
  });

  it("measures nothing in a frame that was already running when it detached", () => {
    const { editor, host, cleanup } = attach({ 1: 2 }, [peer(1)]);
    editor.view.dispatch(editor.state.tr.insertText("x", 1));
    const running = [...frames.values()];
    const coords = vi.mocked(editor.view.coordsAtPos);
    coords.mockClear();
    cleanup();
    running.forEach(callback => callback(performance.now()));
    expect(coords).not.toHaveBeenCalled();
    expect(host.classList).not.toContain("collaboration-presence-active");
    attached.splice(0);
    editor.destroy();
  });

  it("restores native carets and removes the overlay on detach", () => {
    const { host, cleanup, removeAbortListener } = attach({ 1: 2 }, [peer(1)]);
    const container = host;
    expect(container.classList).toContain("collaboration-presence-active");
    cleanup();
    expect(container.classList).not.toContain("collaboration-presence-active");
    expect(container.querySelector(".collaboration-presence")).toBeNull();
    expect(removeAbortListener).toHaveBeenCalledWith("abort", expect.any(Function));
    attached.splice(0);
  });
});

describe("co-editor pointers", () => {
  // Two editors of one page: Jana (writer) moves her mouse, the other side draws her pointer.
  function pointerEditor(ydoc: Y.Doc) {
    const awareness = new Awareness(ydoc);
    const caret = Extension.create({ name: "collaborationCaret", addOptions: () => ({ provider: { awareness } }) });
    const host = document.createElement("div");
    document.body.append(host);
    const editor = new Editor({ element: host, extensions: [Document, Paragraph, Text, Collaboration.configure({ document: ydoc }), caret] });
    vi.spyOn(editor.view, "coordsAtPos").mockImplementation(pos => ({ left: 100 + pos * 8, right: 101 + pos * 8, top: 60, bottom: 80 }));
    const presence = createCollaborationPresence({ onChange: () => {}, pointers: true });
    const abort = new AbortController();
    const cleanup = presence.plugin.create({ profile: "rich", signal: abort.signal }).onAttach!({ editor, controller: {} as never, signal: abort.signal }) as () => void;
    attached.push(() => { cleanup(); editor.destroy(); host.remove(); awareness.destroy(); });
    return { editor, host, awareness, presence };
  }
  function pair() {
    const docA = new Y.Doc(), docB = new Y.Doc();
    docA.on("update", (update: Uint8Array) => Y.applyUpdate(docB, update));
    docB.on("update", (update: Uint8Array) => Y.applyUpdate(docA, update));
    const jana = pointerEditor(docA), reader = pointerEditor(docB);
    jana.editor.commands.insertContent("abcdef");
    // The relay: every change of Jana's awareness reaches the other side.
    jana.awareness.on("update", ({ added, updated, removed }: Record<string, number[]>) =>
      applyAwarenessUpdate(reader.awareness, encodeAwarenessUpdate(jana.awareness, [...added!, ...updated!, ...removed!]), "remote"));
    reader.presence.update([{ clientId: jana.awareness.clientID, id: "jana", name: "Jana", color: "#d946ef" }]);
    return { jana, reader };
  }
  const move = (host: HTMLElement, type: string, clientX: number, clientY: number, pointerType = "mouse") =>
    host.dispatchEvent(Object.assign(new MouseEvent(type, { clientX, clientY, bubbles: true }), { pointerType }));
  const pointerOf = (host: HTMLElement) => host.querySelector<HTMLElement>(".collaboration-pointer");

  beforeEach(() => { vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "Date"] }); });
  afterEach(() => { vi.useRealTimers(); });

  it("anchors the pointer to the text, so an edit before it carries it along", () => {
    const { jana, reader } = pair();
    vi.spyOn(jana.editor.view, "posAtCoords").mockReturnValue({ pos: 4, inside: -1 });
    move(jana.host, "pointermove", 140, 70);
    move(jana.host, "pointermove", 135, 75);
    expect(jana.awareness.getLocalState()?.pointer).toBeUndefined();
    vi.advanceTimersByTime(50);
    // One sample per 50 ms, the latest: 135 − (100 + 4 · 8) = 3 px right of the character, 15 px under its top.
    expect(jana.awareness.getLocalState()?.pointer).toMatchObject({ x: 3, y: 15 });
    frame();
    expect(pointerOf(reader.host)!.style.transform).toBe("translate3d(135px, 75px, 0px)");
    expect(pointerOf(reader.host)!.textContent).toBe("Jana");

    jana.editor.chain().insertContentAt(1, "XY").run();
    frame();
    // It glides along (pointerGlide.ts), never jumps: part of the way after one frame, all of it once settled.
    const start = performance.now();
    frame(start + 16);
    const halfway = Number(/translate3d\(([\d.]+)px/.exec(pointerOf(reader.host)!.style.transform)![1]);
    expect(halfway).toBeGreaterThan(135);
    expect(halfway).toBeLessThan(151);
    for (let i = 2; i <= 60; i++) frame(start + i * 16);
    expect(pointerOf(reader.host)!.style.transform).toBe("translate3d(151px, 75px, 0px)");
    expect(pointerOf(jana.host)).toBeNull();
  });

  it("scales the offset to the reader's text column, so a laptop and a portrait monitor see the pointer at the same place of the page", () => {
    const { jana, reader } = pair();
    // Jana's text column is 800 px wide (a wide monitor), the reader's 400 px (a narrow window).
    vi.spyOn(jana.editor.view.dom, "getBoundingClientRect").mockReturnValue(new DOMRect(0, 0, 800, 600));
    vi.spyOn(reader.editor.view.dom, "getBoundingClientRect").mockReturnValue(new DOMRect(0, 0, 400, 600));
    vi.spyOn(jana.editor.view, "posAtCoords").mockReturnValue({ pos: 4, inside: -1 });
    // 200 px right of the character and 40 px under its top on Jana's screen (over a picture, in the margin)…
    move(jana.host, "pointermove", 100 + 4 * 8 + 200, 100);
    vi.advanceTimersByTime(50);
    expect(jana.awareness.getLocalState()?.pointer).toMatchObject({ x: 200, y: 40, w: 800 });
    frame();
    // …is 100 px and 20 px on the reader's half as wide column: the same place relative to the page, not the same pixels.
    expect(pointerOf(reader.host)!.style.transform).toBe("translate3d(232px, 80px, 0px)");
  });

  it("keeps an offset as it is from a client that sends no width, and bounds an absurd ratio", () => {
    expect(pointerScale(undefined, 400)).toBe(1);
    expect(pointerScale(800, 0)).toBe(1);
    expect(pointerScale(800, 400)).toBe(0.5);
    expect(pointerScale(10, 4000)).toBe(4);
  });

  it("sends a press at once, nothing for a finger, and takes the pointer away when the mouse leaves", () => {
    const { jana, reader } = pair();
    vi.spyOn(jana.editor.view, "posAtCoords").mockReturnValue({ pos: 2, inside: -1 });
    move(jana.host, "pointermove", 120, 70, "touch");
    vi.advanceTimersByTime(50);
    expect(jana.awareness.getLocalState()?.pointer).toBeUndefined();
    move(jana.host, "pointerdown", 120, 70);
    expect(jana.awareness.getLocalState()?.pointer).toMatchObject({ down: true });
    frame();
    expect(pointerOf(reader.host)!.classList.contains("is-pressed")).toBe(true);

    jana.host.dispatchEvent(new MouseEvent("pointerleave"));
    expect(jana.awareness.getLocalState()?.pointer).toBeNull();
    frame();
    expect(pointerOf(reader.host)!.classList.contains("is-leaving")).toBe(true);
    vi.runAllTimers();
    expect(pointerOf(reader.host)).toBeNull();
  });

  it("draws 40 co-editors' pointers moving 20 times a second in one paint a frame, writing only their positions (perf guard)", () => {
    const ydoc = new Y.Doc();
    const reader = pointerEditor(ydoc);
    reader.editor.commands.insertContent("Čtyřicet lidí ukazuje na tenhle odstavec najednou.");
    const sync = ySyncPluginKey.getState(reader.editor.state) as { type: Y.XmlFragment; binding: { mapping: never } };
    const at = (pos: number) => Y.relativePositionToJSON(absolutePositionToRelativePosition(pos, sync.type, sync.binding.mapping));
    const peers = Array.from({ length: 40 }, (_, index) => {
      const peer = new Awareness(new Y.Doc());
      peer.setLocalState({ user: { id: `u${index}`, name: `Kolega ${index}`, color: "#0284c7" }, pointer: { anchor: at(1 + index % 40), x: 2, y: 3 } });
      applyAwarenessUpdate(reader.awareness, encodeAwarenessUpdate(peer, [peer.clientID]), "remote");
      return peer;
    });
    reader.presence.update(peers.map((peer, index) => ({ clientId: peer.clientID, id: `u${index}`, name: `Kolega ${index}`, color: "#0284c7" })));
    frame();
    const overlay = reader.host.querySelector(".collaboration-presence")!;
    expect(overlay.querySelectorAll(".collaboration-pointer")).toHaveLength(40);
    const coords = vi.mocked(reader.editor.view.coordsAtPos);
    let transactions = 0;
    reader.editor.on("transaction", () => { transactions++; });

    const records: MutationRecord[] = [];
    const observer = new MutationObserver(found => records.push(...found));
    observer.observe(overlay, { subtree: true, childList: true, characterData: true, attributes: true });
    let worst = 0;
    for (let sample = 0; sample < 20; sample++) {
      // A frame's worth of samples from everyone, as the server relays them: one paint is waiting.
      for (const [index, peer] of peers.entries()) {
        peer.setLocalStateField("pointer", { anchor: at(1 + (index + sample) % 40), x: sample, y: 3 });
        applyAwarenessUpdate(reader.awareness, encodeAwarenessUpdate(peer, [peer.clientID]), "remote");
      }
      expect(frames.size).toBeLessThanOrEqual(2);
      coords.mockClear();
      const started = performance.now();
      frame();
      worst = Math.max(worst, performance.now() - started);
      // One measurement per pointer, nothing per sample.
      expect(coords.mock.calls.length).toBeLessThanOrEqual(40);
    }
    records.push(...observer.takeRecords());
    observer.disconnect();
    // Nothing but positions: no element, text or colour written for a pointer that only moved.
    expect(records.filter(record => record.type !== "attributes" || record.attributeName !== "style")).toEqual([]);
    expect(transactions).toBe(0);
    console.info(`pointers: 40 co-editors x 20 samples, 20 paints, worst paint ${worst.toFixed(1)} ms, ${records.length} style writes`);
    peers.forEach(peer => peer.destroy());
  });

  it("shows nothing for an anchor that resolves to nothing", () => {
    const { jana, reader } = pair();
    jana.awareness.setLocalStateField("pointer", { anchor: { type: { client: 1, clock: 999 }, tname: null, item: { client: 7, clock: 999 }, assoc: 0 }, x: 1, y: 1 });
    frame();
    expect(pointerOf(reader.host)).toBeNull();
    jana.awareness.setLocalStateField("pointer", { anchor: "garbage", x: "1", y: 1 });
    frame();
    expect(pointerOf(reader.host)).toBeNull();
  });
});

describe("an agent's caret from the server", () => {
  it("lands right after the text the agent wrote, from the state the server sends (LiveDocumentWriter.ShowAsync)", () => {
    const ydoc = new Y.Doc();
    const awareness = new Awareness(ydoc);
    const host = document.createElement("div");
    document.body.append(host);
    const editor = new Editor({ element: host, extensions: [Document, Paragraph, Text, Collaboration.configure({ document: ydoc }),
      CollaborationCaret.configure({ provider: { awareness }, user: { name: "Petr", color: "#0284c7" } })] });
    vi.spyOn(editor.view, "coordsAtPos").mockImplementation(pos => ({ left: 100 + pos * 8, right: 101 + pos * 8, top: 60, bottom: 80 }));
    const presence = createCollaborationPresence({ onChange: () => {} });
    const abort = new AbortController();
    const cleanup = presence.plugin.create({ profile: "rich", signal: abort.signal }).onAttach!({ editor, controller: {} as never, signal: abort.signal }) as () => void;
    attached.push(() => { cleanup(); editor.destroy(); host.remove(); awareness.destroy(); });
    editor.commands.insertContent("abcdef");
    // The agent's update, as the server's authority writes it: its own client appends " agent" (its clocks 0–5).
    const agentDoc = new Y.Doc();
    agentDoc.clientID = 777;
    Y.applyUpdate(agentDoc, Y.encodeStateAsUpdate(ydoc));
    ((agentDoc.getXmlFragment("default").get(0) as Y.XmlElement).get(0) as Y.XmlText).insert(6, " agent");
    Y.applyUpdate(ydoc, Y.encodeStateAsUpdate(agentDoc, Y.encodeStateVector(ydoc)));
    // Then its presence, in exactly the server's JSON: a collapsed cursor after its last letter (assoc −1).
    const server = new Awareness(new Y.Doc());
    const at = { item: { client: 777, clock: 5 }, assoc: -1 };
    server.setLocalState({ user: { id: "ai:0f3c", name: "Jana · AI (MCP)", color: "#7c3aed" }, cursor: { anchor: at, head: at } });
    applyAwarenessUpdate(awareness, encodeAwarenessUpdate(server, [server.clientID]), "remote");
    presence.update([{ clientId: server.clientID, id: "ai:0f3c", name: "Jana · AI (MCP)", color: "#7c3aed" }]);
    frame();
    const caret = host.querySelector<HTMLElement>(`[data-client-id="${server.clientID}"]`)!;
    expect(caret.classList).toContain("is-agent");
    // "abcdef agent" is positions 1–12 of the paragraph: the caret stands at 13, after "agent".
    expect(caret.style.transform).toBe(`translate3d(${100 + 13 * 8}px, 60px, 0px)`);
    server.destroy();
  });
});

describe("an agent's write typed in", () => {
  it("shows the agent's text word by word with its caret on the front, then all of it (live/agentReveal.ts)", () => {
    let now = 1000;
    vi.spyOn(performance, "now").mockImplementation(() => now);
    const ydoc = new Y.Doc();
    const awareness = new Awareness(ydoc);
    const host = document.createElement("div");
    document.body.append(host);
    const editor = new Editor({ element: host, extensions: [Document, Paragraph, Text, Collaboration.configure({ document: ydoc }),
      CollaborationCaret.configure({ provider: { awareness }, user: { name: "Petr", color: "#0284c7" } })] });
    vi.spyOn(editor.view, "coordsAtPos").mockImplementation(pos => ({ left: 100 + pos * 8, right: 101 + pos * 8, top: 60, bottom: 80 }));
    const presence = createCollaborationPresence({ onChange: () => {} });
    const abort = new AbortController();
    const cleanup = presence.plugin.create({ profile: "rich", signal: abort.signal }).onAttach!({ editor, controller: {} as never, signal: abort.signal }) as () => void;
    attached.push(() => { cleanup(); editor.destroy(); host.remove(); awareness.destroy(); });
    editor.commands.insertContent("abcdef");
    // The agent shows before it writes, and writes as the Yjs client of its presence (777, LivePageWrites).
    presence.update([{ clientId: 777, id: "ai:0f3c", name: "Jana · AI (MCP)", color: "#7c3aed" }]);
    const agentDoc = new Y.Doc();
    agentDoc.clientID = 777;
    Y.applyUpdate(agentDoc, Y.encodeStateAsUpdate(ydoc));
    ((agentDoc.getXmlFragment("default").get(0) as Y.XmlElement).get(0) as Y.XmlText).insert(6, " agent píše dál");
    Y.applyUpdate(ydoc, Y.encodeStateAsUpdate(agentDoc, Y.encodeStateVector(ydoc)));
    frame(now);
    const unwritten = () => [...host.querySelectorAll(".atlas-agent-unwritten")].map(span => span.textContent).join("");
    const caretX = () => host.querySelector<HTMLElement>('[data-client-id="777"]')?.style.transform;
    // The text is in the document at once, still invisible; the caret stands where the typing starts, its glyph writing.
    expect(editor.getText()).toBe("abcdef agent píše dál");
    expect(unwritten()).toBe(" agent píše dál");
    expect(caretX()).toBe(`translate3d(${100 + 7 * 8}px, 60px, 0px)`);
    expect(host.querySelector('[data-client-id="777"] [data-ai-glyph]')!.getAttribute("data-state")).toBe("writing");
    // 15 letters at the even pace take 300 ms; after 160 ms the front has passed „píše“ (its word is shown whole).
    now += 160;
    frame(now);
    frame(now);
    expect(unwritten()).toBe(" dál");
    expect(caretX()).toBe(`translate3d(${100 + 18 * 8}px, 60px, 0px)`);
    now += 400;
    frame(now);
    frame(now);
    expect(unwritten()).toBe("");
  });

  it("types a long write in at most one step a frame, within its bounded time, while a person keeps typing (perf guard)", () => {
    let now = 1000;
    vi.spyOn(performance, "now").mockImplementation(() => now);
    const ydoc = new Y.Doc();
    const awareness = new Awareness(ydoc);
    const host = document.createElement("div");
    document.body.append(host);
    const editor = new Editor({ element: host, extensions: [Document, Paragraph, Text, Collaboration.configure({ document: ydoc }),
      CollaborationCaret.configure({ provider: { awareness }, user: { name: "Petr", color: "#0284c7" } })] });
    vi.spyOn(editor.view, "coordsAtPos").mockImplementation(pos => ({ left: 100 + pos * 8, right: 101 + pos * 8, top: 60, bottom: 80 }));
    const presence = createCollaborationPresence({ onChange: () => {} });
    const abort = new AbortController();
    const cleanup = presence.plugin.create({ profile: "rich", signal: abort.signal }).onAttach!({ editor, controller: {} as never, signal: abort.signal }) as () => void;
    attached.push(() => { cleanup(); editor.destroy(); host.remove(); awareness.destroy(); });
    editor.commands.insertContent("Petr píše.");
    presence.update([{ clientId: 777, id: "ai:0f3c", name: "Jana · AI (MCP)", color: "#7c3aed" }]);
    // The agent's write: 40 paragraphs of 20 words (about 6 000 letters) in one update, as LivePageWrites appends it.
    const agentDoc = new Y.Doc();
    agentDoc.clientID = 777;
    Y.applyUpdate(agentDoc, Y.encodeStateAsUpdate(ydoc));
    const fragment = agentDoc.getXmlFragment("default");
    fragment.insert(1, Array.from({ length: 40 }, (_, block) => {
      const paragraph = new Y.XmlElement("paragraph");
      paragraph.insert(0, [new Y.XmlText(Array.from({ length: 20 }, (_, word) => `slovo${block}x${word}`).join(" "))]);
      return paragraph;
    }));
    Y.applyUpdate(ydoc, Y.encodeStateAsUpdate(agentDoc, Y.encodeStateVector(ydoc)));
    let transactions = 0;
    editor.on("transaction", () => { transactions++; });
    let frameCount = 0;
    const started = performance.now();
    let worst = 0;
    while (host.querySelector(".atlas-agent-unwritten") && frameCount < 600) {
      now += 16;
      // The person types on in their own paragraph meanwhile.
      if (frameCount % 10 === 0) editor.chain().insertContentAt(2, "x").run();
      const before = Date.now();
      frame(now);
      worst = Math.max(worst, Date.now() - before);
      frameCount++;
    }
    // All of it is there within one and a half of the AI's loops, at most one step a frame, the person's letters too.
    expect(host.querySelector(".atlas-agent-unwritten")).toBeNull();
    expect(now - started).toBeLessThanOrEqual(1.5 * 1600 + 32);
    expect(transactions).toBeLessThanOrEqual(frameCount + Math.ceil(frameCount / 10));
    expect(editor.getText()).toContain("slovo39x19");
    expect(editor.getText().startsWith("P" + "x".repeat(Math.ceil(frameCount / 10)))).toBe(true);
    console.info(`agent reveal: 800 words typed in over ${frameCount} frames (${now - started} ms of motion), ${transactions} transactions, worst frame ${worst} ms`);
  });
});
