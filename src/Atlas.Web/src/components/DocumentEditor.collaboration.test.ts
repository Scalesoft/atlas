import { afterEach, describe, it, expect, vi } from "vitest";
import { defineComponent, h } from 'vue';
import { mount, shallowMount, flushPromises, type VueWrapper } from "@vue/test-utils";
import type { MarkdownCollaborationAdapter, MarkdownEditorState, MarkdownRichValue } from "@atlas/markdown/core";
import DocumentEditor from "./DocumentEditor.vue";
import { emptyDocument } from "../document";
import { joinPageCollaboration, openBundledCollaboration } from "../collaboration";
// The hydration comparison runs when the browser is idle; here it runs at once, so its outcome is checked directly.
// A test may hold the comparison back (`idle.held`) to run it later.
const idle = vi.hoisted(() => ({ held: null as (() => void)[] | null, painted: null as (() => void)[] | null }));
vi.mock("../idle", () => ({
  whenIdle: (work: () => void) => work(),
  afterPaint: (work: () => void) => (idle.painted ? idle.painted.push(work) : work()),
  whenRested: (work: () => void) => (idle.held ? idle.held.push(work) : work()),
}));
vi.mock("../collaboration", async (original) => ({
  ...(await original<object>()),
  joinPageCollaboration: vi.fn(),
  openBundledCollaboration: vi.fn(),
}));
// The live hub of a live page's reader: the test announces PageChanged on its channel.
const hub = vi.hoisted(() => ({ listeners: new Map<string, { message?(event: string, payload: unknown): void }>() }));
vi.mock("../live/liveConnection", () => ({
  liveConnection: () => ({
    route: () => {},
    subscribe: (channel: string, listener: { message?(event: string, payload: unknown): void }) => {
      hub.listeners.set(channel, listener);
      return { close: () => hub.listeners.delete(channel) };
    },
  }),
}));
let wrapper: VueWrapper | undefined;
afterEach(() => {
  wrapper?.unmount();
  vi.clearAllMocks();
});
const props = {
  value: emptyDocument(),
  readonly: true,
  dark: false,
  accent: "blue" as const,
  spaceId: "space",
  pageId: "page",
  identity: { id: "user", displayName: "User" },
  collaborationAllowed: true,
};
describe("document collaboration lifecycle", () => {
  // 3.6-I: a reader of a page published live joins the writers' session read-only, says nothing while there is none, and tries again when the page changes.
  it("joins a live page read-only for its reader, stays local without a session and tries again on PageChanged", async () => {
    vi.mocked(joinPageCollaboration).mockRejectedValueOnce(Object.assign(new Error("no_live_session"), { status: 404 }));
    wrapper = mount(DocumentEditor, { props: { ...props, collaborationAllowed: false, liveReading: true }, attachTo: document.body });
    await flushPromises();
    expect(joinPageCollaboration).toHaveBeenCalledExactlyOnceWith("page", props.identity, true);
    expect(wrapper.find(".editor-connection-error").exists()).toBe(false);
    await vi.waitFor(() => expect(wrapper!.get(".tiptap").attributes("contenteditable")).toBe("false"));
    vi.mocked(joinPageCollaboration).mockReturnValueOnce(new Promise(() => {}));
    hub.listeners.get("page:page")!.message!("PageChanged", { pageId: "page" });
    await flushPromises();
    expect(joinPageCollaboration).toHaveBeenCalledTimes(2);
  });
  const ready = { state: { lifecycle: "ready", binding: "collaborative", editable: true, connection: "online", peers: [], pendingChanges: 0, error: null },
    query: () => ({ enabled: false }), getValue: emptyDocument, subscribe: () => () => {} };
  // A writer's save or a stale live state starts a new generation; the hub then ends the reader's session (DocumentAccessChanged).
  it("joins a live page again at once when its reader's online session ends, and waits for PageChanged when the new one never got online", async () => {
    const session = () => ({ lease: { documentId: "page", generation: "g", currentVersion: 3 }, adapter: {} as MarkdownCollaborationAdapter });
    vi.mocked(joinPageCollaboration).mockResolvedValue(session());
    wrapper = shallowMount(DocumentEditor, { props: { ...props, collaborationAllowed: false, liveReading: true } });
    const ended = { lifecycle: "failed", binding: "collaborative", connection: "failed", peers: [], pendingChanges: 0,
      error: { kind: "collaboration", message: "[FORBIDDEN] Document access denied.", recoverable: false } };
    const fail = async (connection: string) => {
      await vi.waitFor(() => expect(wrapper!.findAllComponents({ name: "MarkdownEditor" }).some(editor => editor.classes().includes("editor-hydrating"))).toBe(true));
      let listener!: () => void;
      const controller = { state: { ...ready.state, editable: false, connection } as unknown, query: () => ({ enabled: false }), getValue: emptyDocument,
        subscribe: (next: () => void) => { listener = next; return () => {}; } };
      wrapper!.findAllComponents({ name: "MarkdownEditor" }).find(editor => editor.classes().includes("editor-hydrating"))!.vm.$emit("created", controller);
      await flushPromises();
      controller.state = ended;
      listener();
      await flushPromises();
    };
    await fail("online");
    expect(joinPageCollaboration).toHaveBeenCalledTimes(2);
    expect(joinPageCollaboration).toHaveBeenLastCalledWith("page", props.identity, true);
    await fail("connecting");
    expect(joinPageCollaboration).toHaveBeenCalledTimes(2);
    expect(hub.listeners.has("page:page")).toBe(true);
  });
  it("shows the value in a local read-only editor while the join runs and lets the collaborative editor replace it once ready", async () => {
    vi.mocked(joinPageCollaboration).mockResolvedValue({ lease: { documentId: "page", generation: "warm", currentVersion: 3 }, adapter: {} as MarkdownCollaborationAdapter });
    wrapper = shallowMount(DocumentEditor, { props: { ...props, readonly: false } });
    await vi.waitFor(() => expect(wrapper!.findAllComponents({ name: "MarkdownEditor" })).toHaveLength(2));
    const [shared, local] = wrapper.findAllComponents({ name: "MarkdownEditor" });
    expect(shared!.classes()).toContain("editor-hydrating");
    expect(local!.props("readonly")).toBe(true);
    shared!.vm.$emit("created", ready);
    await flushPromises();
    expect(wrapper.findAllComponents({ name: "MarkdownEditor" })).toHaveLength(1);
    expect(wrapper.getComponent({ name: "MarkdownEditor" }).classes()).not.toContain("editor-hydrating");
  });
  it("hands over from the stand-in to the shared editor built hidden: the same node, and always a document on screen", async () => {
    let join!: (value: { lease: { documentId: string; generation: string; currentVersion: number }; adapter: MarkdownCollaborationAdapter }) => void;
    vi.mocked(joinPageCollaboration).mockReturnValue(new Promise(resolve => { join = resolve; }));
    wrapper = shallowMount(DocumentEditor, { props: { ...props, readonly: false } });
    // Shown: an editor that is not the hidden one, or the page's skeleton; never neither.
    const onScreen = () => wrapper!.findAllComponents({ name: "MarkdownEditor" }).some(editor => !editor.classes().includes("editor-hydrating"))
      || wrapper!.findComponent({ name: "LoadingSkeleton" }).exists();
    await flushPromises();
    expect(onScreen()).toBe(true);
    join({ lease: { documentId: "page", generation: "warm", currentVersion: 3 }, adapter: {} as MarkdownCollaborationAdapter });
    await flushPromises();
    expect(onScreen()).toBe(true);
    const hidden = wrapper.findAllComponents({ name: "MarkdownEditor" }).find(editor => editor.classes().includes("editor-hydrating"))!;
    const node = hidden.element;
    hidden.vm.$emit("created", ready);
    await flushPromises();
    expect(onScreen()).toBe(true);
    expect(wrapper.findAllComponents({ name: "MarkdownEditor" })).toHaveLength(1);
    expect(wrapper.getComponent({ name: "MarkdownEditor" }).element).toBe(node);
  });
  it("opens the shared editor from the page bundle at once, without a lease request", async () => {
    const adapter = {} as MarkdownCollaborationAdapter;
    vi.mocked(openBundledCollaboration).mockResolvedValue({ lease: { documentId: "page", generation: "bundle", currentVersion: 4 }, adapter });
    const collaboration = { documentId: "page", generation: "bundle", currentVersion: 4, readOnly: false };
    wrapper = shallowMount(DocumentEditor, { props: { ...props, readonly: false, collaboration } });
    await flushPromises();
    expect(openBundledCollaboration).toHaveBeenCalledExactlyOnceWith(collaboration, props.identity);
    expect(joinPageCollaboration).not.toHaveBeenCalled();
    expect(wrapper.getComponent({ name: "MarkdownEditor" }).props("collaboration")).toBe(adapter);
  });
  it("joins through the server when the bundle's state cannot be had (none served, or of another generation)", async () => {
    vi.mocked(openBundledCollaboration).mockResolvedValue(undefined);
    vi.mocked(joinPageCollaboration).mockResolvedValue({ lease: { documentId: "page", generation: "joined", currentVersion: 4 }, adapter: {} as MarkdownCollaborationAdapter });
    wrapper = shallowMount(DocumentEditor, { props: { ...props, readonly: false, collaboration: { documentId: "page", generation: "bundle", currentVersion: 4, readOnly: false } } });
    await flushPromises();
    expect(joinPageCollaboration).toHaveBeenCalledExactlyOnceWith("page", props.identity, false);
  });
  // UX open blink: the body showed the page with nothing in it for the editor's whole build, and the joining line pushed the page down.
  it("keeps the page's skeleton until the bundled editor stands, then keeps that very editor through the session's arrival", async () => {
    const adapter = {} as MarkdownCollaborationAdapter;
    let open!: (value: { lease: { documentId: string; generation: string; currentVersion: number }; adapter: MarkdownCollaborationAdapter }) => void;
    vi.mocked(openBundledCollaboration).mockReturnValue(new Promise(resolve => { open = resolve; }));
    const collaboration = { documentId: "page", generation: "bundle", currentVersion: 4, readOnly: false };
    wrapper = shallowMount(DocumentEditor, { props: { ...props, readonly: false, collaboration } });
    await flushPromises();
    expect(wrapper.findComponent({ name: "MarkdownEditor" }).exists()).toBe(false);
    // It fades in after the defer token like any skeleton, unless the page's arrival skeleton is already on screen (then it stays as is).
    expect(wrapper.getComponent({ name: "LoadingSkeleton" }).props("instant")).toBe(false);
    await wrapper.setProps({ skeletonShown: true });
    expect(wrapper.getComponent({ name: "LoadingSkeleton" }).props("instant")).toBe(true);
    open({ lease: { documentId: "page", generation: "bundle", currentVersion: 4 }, adapter });
    await flushPromises();
    expect(wrapper.findComponent({ name: "LoadingSkeleton" }).exists()).toBe(false);
    const editor = wrapper.getComponent({ name: "MarkdownEditor" });
    const element = editor.element;
    let state: MarkdownEditorState = { lifecycle: "ready", binding: "collaborative", editable: true, focused: false, connection: "connecting", link: null,
      documentSave: "saved", projectionSave: "current", pendingChanges: 0, error: null, peers: [], contentRevision: 0, changeOrigin: "remote" };
    let notify!: () => void;
    editor.vm.$emit("created", { get state() { return state; }, query: () => ({ enabled: false }), getValue: emptyDocument, subscribe(listener: () => void) { notify = listener; return () => {}; } });
    state = { ...state, connection: "online" }; notify();
    await flushPromises();
    expect(wrapper.findAllComponents({ name: "MarkdownEditor" })).toHaveLength(1);
    expect(wrapper.getComponent({ name: "MarkdownEditor" }).element).toBe(element);
  });
  it("puts nothing into or over the document however long the join takes (the page's status chip says a slow one)", async () => {
    vi.useFakeTimers();
    try {
      vi.mocked(joinPageCollaboration).mockReturnValue(new Promise(() => {}));
      wrapper = shallowMount(DocumentEditor, { props: { ...props, readonly: false } });
      await vi.advanceTimersByTimeAsync(0);
      const before = wrapper.element.innerHTML;
      await vi.advanceTimersByTimeAsync(5000);
      expect(wrapper.element.innerHTML).toBe(before);
      expect(wrapper.text()).not.toContain("Připojujeme");
      expect(wrapper.emitted<[{ connection: string }]>("collaborationStatus")!.at(-1)![0].connection).toBe("connecting");
    } finally { vi.useRealTimers(); }
  });
  // The server saves a live page from its own projection of the shared log: a checkpoint never serializes the whole page here.
  it("captures a live checkpoint as its generation alone, without reading the document", async () => {
    vi.mocked(joinPageCollaboration).mockResolvedValueOnce({ lease: { documentId: "page", generation: "session", currentVersion: 3 }, adapter: {} as MarkdownCollaborationAdapter });
    wrapper = shallowMount(DocumentEditor, { props: { ...props, readonly: false } });
    await flushPromises();
    const getValue = vi.fn(emptyDocument), getProjection = vi.fn(() => ({ markdown: "" }));
    wrapper.getComponent({ name: "MarkdownEditor" }).vm.$emit("created", { ...ready, getValue, getProjection });
    getValue.mockClear(); // the hydration check reads it once
    const exposed = wrapper.vm as unknown as { capture(options?: { checkpoint?: boolean }): object };
    expect(exposed.capture({ checkpoint: true })).toEqual({ collaborationGeneration: "session" });
    expect(getValue).not.toHaveBeenCalled();
    expect(getProjection).not.toHaveBeenCalled();
    expect(exposed.capture()).toEqual({ contentJson: JSON.stringify(emptyDocument()), markdown: "", collaborationGeneration: "session" });
  });
  it("publishes remote editors and clears them during reconnect and page replacement", async () => {
    vi.mocked(joinPageCollaboration).mockResolvedValueOnce({
      lease: { documentId: "page", generation: "session", currentVersion: 3 },
      adapter: {} as MarkdownCollaborationAdapter,
    });
    wrapper = shallowMount(DocumentEditor, { props: { ...props, readonly: false } });
    await flushPromises();
    const peers = [{ clientId: 23, id: "other", name: "Jana Nováková", color: "#5274bd" }];
    let state: MarkdownEditorState = {
      lifecycle: "ready", binding: "collaborative", editable: true, focused: false,
      connection: "online", link: null, documentSave: "saved", projectionSave: "current",
      pendingChanges: 0, error: null, peers, contentRevision: 0, changeOrigin: "remote",
    };
    let notify!: () => void;
    wrapper.getComponent({ name: "MarkdownEditor" }).vm.$emit("created", {
      get state() { return state; }, query: () => ({ enabled: false }), getValue: emptyDocument,
      subscribe(listener: () => void) { notify = listener; return () => {}; },
    });
    expect(wrapper.emitted("collaborationPeers")?.at(-1)?.[0]).toEqual([{ ...peers[0], cursorAvailable: false }]);
    expect((wrapper.vm as unknown as { navigateToPeer(id: number): boolean }).navigateToPeer(999)).toBe(false);
    state = { ...state, connection: "reconnecting" }; notify();
    expect(wrapper.emitted("collaborationPeers")?.at(-1)?.[0]).toEqual([]);
    state = { ...state, connection: "online" }; notify();
    expect(wrapper.emitted("collaborationPeers")?.at(-1)?.[0]).toHaveLength(1);
    vi.mocked(joinPageCollaboration).mockReturnValueOnce(new Promise(() => {}));
    await wrapper.setProps({ pageId: "another-page" });
    expect(wrapper.emitted("collaborationPeers")?.at(-1)?.[0]).toEqual([]);
  });
  it.each([
    ["normalized only", "Uložený text.", false],
    ["another writer's unsaved text", "Neuložený text kolegy.", true],
  ])("marks the hydrated document changed only for real content (%s)", async (_case, hydratedText, changed) => {
    vi.mocked(joinPageCollaboration).mockResolvedValueOnce({
      lease: { documentId: "page", generation: "session", currentVersion: 3 },
      adapter: {} as MarkdownCollaborationAdapter,
    });
    const stored: MarkdownRichValue = { ...emptyDocument(), plugins: [{ id: "heading-anchors", version: 1 }], document: { type: "doc", content: [
      { type: "heading", attrs: { level: 1 }, content: [{ type: "text", text: "Nadpis" }] },
      { type: "paragraph", content: [{ type: "text", text: "Uložený text." }] },
    ] } };
    const hydrated: MarkdownRichValue = { ...emptyDocument(), plugins: [{ id: "heading-anchors", version: 1 }], document: { type: "doc", content: [
      { type: "heading", attrs: { headingId: "nadpis", textAlign: null, level: 1 }, content: [{ type: "text", text: "Nadpis" }] },
      { type: "paragraph", attrs: { textAlign: null }, content: [{ type: "text", text: hydratedText }] },
      { type: "paragraph", attrs: { textAlign: null } },
    ] } };
    wrapper = shallowMount(DocumentEditor, { props: { ...props, value: stored, readonly: false } });
    await flushPromises();
    const state: MarkdownEditorState = {
      lifecycle: "ready", binding: "collaborative", editable: true, focused: false,
      connection: "online", link: null, documentSave: "saved", projectionSave: "current",
      pendingChanges: 0, error: null, peers: [], contentRevision: 0, changeOrigin: "remote",
    };
    wrapper.getComponent({ name: "MarkdownEditor" }).vm.$emit("created", {
      state, query: () => ({ enabled: false }), getValue: () => hydrated, subscribe: () => () => {},
    });
    expect(!!wrapper.emitted("change")).toBe(changed);
  });
  it("skips the hydration comparison once an edit has already marked the page changed", async () => {
    vi.mocked(joinPageCollaboration).mockResolvedValueOnce({
      lease: { documentId: "page", generation: "session", currentVersion: 3 },
      adapter: {} as MarkdownCollaborationAdapter,
    });
    const stored: MarkdownRichValue = { ...emptyDocument(), document: { type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: "Uložený text." }] }] } };
    const hydrated: MarkdownRichValue = { ...emptyDocument(), document: { type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: "Neuložený text kolegy." }] }] } };
    wrapper = shallowMount(DocumentEditor, { props: { ...props, value: stored, readonly: false } });
    await flushPromises();
    idle.held = [];
    try {
      const editor = wrapper.getComponent({ name: "MarkdownEditor" });
      editor.vm.$emit("created", {
        state: { lifecycle: "ready", binding: "collaborative", editable: true, focused: false, connection: "online", link: null, documentSave: "saved",
          projectionSave: "current", pendingChanges: 0, error: null, peers: [], contentRevision: 0, changeOrigin: "remote" } satisfies MarkdownEditorState,
        query: () => ({ enabled: false }), getValue: () => hydrated, subscribe: () => () => {},
      });
      expect(wrapper.emitted("change")).toBeUndefined();
      editor.vm.$emit("content-change");
      expect(wrapper.emitted("change")).toHaveLength(1);
      idle.held.forEach(work => work());
      expect(wrapper.emitted("change")).toHaveLength(1);
    } finally { idle.held = null; }
  });
  it("forwards controller failures and blocks saving while an existing session is being replaced", async () => {
    vi.mocked(joinPageCollaboration).mockResolvedValueOnce({
      lease: { documentId: "page", generation: "session", currentVersion: 3 },
      adapter: {} as MarkdownCollaborationAdapter,
    });
    wrapper = shallowMount(DocumentEditor, { props: { ...props, readonly: false } });
    await flushPromises();
    let state: MarkdownEditorState = {
      lifecycle: "ready", binding: "collaborative", editable: true, focused: false,
      connection: "online", link: null, documentSave: "saved", projectionSave: "current",
      pendingChanges: 0, error: null, peers: [], contentRevision: 0, changeOrigin: "remote",
    };
    let notify!: () => void;
    wrapper.getComponent({ name: "MarkdownEditor" }).vm.$emit("created", {
      get state() { return state; }, query: () => ({ enabled: false }), getValue: emptyDocument,
      subscribe(listener: () => void) { notify = listener; return () => {}; },
    });
    expect(wrapper.emitted("collaborationStatus")!.at(-1)![0]).toMatchObject({ canSave: true });
    const error = { kind: "projection" as const, message: "Uložení obsahu selhalo.", recoverable: true };
    state = { ...state, projectionSave: "degraded", error };
    notify();
    expect(wrapper.emitted("collaborationStatus")!.at(-1)![0]).toMatchObject({
      canSave: false, projectionSave: "degraded", error,
    });
    state = { ...state, projectionSave: "current", error: null };
    notify();
    vi.mocked(joinPageCollaboration).mockReturnValueOnce(new Promise(() => {}));
    await wrapper.setProps({ pageId: "another-page" });
    expect(wrapper.emitted("collaborationStatus")!.at(-1)![0]).toMatchObject({
      connection: "connecting", canSave: false,
    });
  });
  it("keeps a stable offline status across provider retries and enables saving only once online and synced", async () => {
    // The provider stays `offline` for the whole outage (retries no longer flip to `connecting`).
    vi.mocked(joinPageCollaboration).mockResolvedValueOnce({
      lease: { documentId: "page", generation: "session", currentVersion: 3 }, adapter: {} as MarkdownCollaborationAdapter,
    });
    wrapper = shallowMount(DocumentEditor, { props: { ...props, readonly: false } });
    await flushPromises();
    let state: MarkdownEditorState = {
      lifecycle: "ready", binding: "collaborative", editable: false, focused: false,
      connection: "offline", link: null, documentSave: "pending", projectionSave: "current",
      pendingChanges: 1, error: null, peers: [], contentRevision: 0, changeOrigin: "remote",
    };
    let notify!: () => void;
    wrapper.getComponent({ name: "MarkdownEditor" }).vm.$emit("created", {
      get state() { return state; }, query: () => ({ enabled: false }), getValue: emptyDocument,
      subscribe(listener: () => void) { notify = listener; return () => {}; },
    });
    const statuses = () => wrapper!.emitted("collaborationStatus")!.map(([status]) => status);
    const before = statuses().length;
    for (let retry = 0; retry < 3; retry++) { state = { ...state }; notify(); }
    const offline = statuses().slice(before - 1);
    expect(offline).toHaveLength(4);
    for (const status of offline)
      expect(status).toEqual({ enabled: true, generation: "session", canSave: false, checkpointReady: false, sourceCheckpointReady: false, connection: "offline",
        pendingChanges: 1, error: null, projectionSave: "current", currentVersion: 3 });
    // Nobody was here: an empty list again is no news (d8a06fbd1, the avatars stopped re-measuring per change).
    expect(wrapper.emitted("collaborationPeers")).toBeUndefined();
    // Transport back but the outbox not yet acknowledged: still not savable.
    state = { ...state, connection: "online", editable: true }; notify();
    expect(statuses().at(-1)).toMatchObject({ connection: "online", canSave: false, pendingChanges: 1 });
    state = { ...state, pendingChanges: 0, documentSave: "saved" }; notify();
    expect(statuses().at(-1)).toMatchObject({ connection: "online", canSave: true });
    // A colleague who shows while online is cleared once the connection drops again.
    state = { ...state, peers: [{ clientId: 7, id: "user-7", name: "Ada", color: "#0284c7" }] }; notify();
    expect(wrapper.emitted("collaborationPeers")!.at(-1)![0]).toEqual([expect.objectContaining({ clientId: 7, name: "Ada" })]);
    state = { ...state, connection: "offline" }; notify();
    expect(wrapper.emitted("collaborationPeers")!.at(-1)![0]).toEqual([]);
    expect(statuses().at(-1)).toMatchObject({ connection: "offline", canSave: false });
  });
  it.each([false, true])("adopts document presentation for the collaborative=%s branch", async (collaborative) => {
    const adapter = {} as MarkdownCollaborationAdapter;
    vi.mocked(joinPageCollaboration).mockResolvedValue({
      lease: { documentId: "page", generation: "session", currentVersion: 3 }, adapter,
    });
    wrapper = shallowMount(DocumentEditor, {
      props: { ...props, readonly: false, collaborationAllowed: collaborative },
      global: { stubs: { MarkdownEditor: {
        name: "MarkdownEditor",
        props: ["collaboration", "modelValue", "outline", "toolbarPresentation", "showProfileLabel", "showPresence"],
        template: "<div />",
      } } },
    });
    await flushPromises();
    const editor = wrapper.getComponent({ name: "MarkdownEditor" });
    expect(editor.props("collaboration")).toBe(collaborative ? adapter : undefined);
    expect(editor.props("outline")).toEqual({ mode: "hidden", preview: false });
    expect(editor.props("toolbarPresentation")).toBe("hidden");
    expect(editor.props("showProfileLabel")).toBe(false);
    expect(editor.props("showPresence")).toBe(false);
  });
  it("preserves a provider failure's kind and recoverability in the host notice", async () => {
    vi.mocked(joinPageCollaboration).mockResolvedValue({
      lease: { documentId: "page", generation: "session", currentVersion: 3 },
      adapter: {} as MarkdownCollaborationAdapter,
    });
    const error: NonNullable<MarkdownEditorState["error"]> = {
      kind: "configuration", message: "Relace má nekompatibilní formát.", recoverable: false,
    };
    const editorBoundary = defineComponent({
      props: ['collaboration'],
      setup(boundary, { expose }) {
        expose({ controller: { state: { lifecycle: boundary.collaboration ? 'failed' : 'ready', error: boundary.collaboration ? error : null }, subscribe: () => () => {}, query: () => ({ enabled: false }), getValue: emptyDocument } });
        return () => h('div');
      },
    });
    wrapper = shallowMount(DocumentEditor, { props: { ...props, readonly: false }, global: { stubs: { MarkdownEditor: editorBoundary } } });
    await flushPromises();
    expect(wrapper.emitted("collaborationStatus")!.at(-1)![0]).toMatchObject({
      connection: "failed", error,
    });
    expect(wrapper.get('[role="alert"]').text()).toContain(error.message);
  });
  it("does not reacquire a session when the parent recreates the same identity object", async () => {
    vi.mocked(joinPageCollaboration).mockReturnValue(new Promise(() => {}));
    wrapper = mount(DocumentEditor, {
      props: { ...props, readonly: false },
      attachTo: document.body,
    });
    await flushPromises();
    await wrapper.setProps({ identity: { ...props.identity }, dark: true });
    await flushPromises();
    expect(joinPageCollaboration).toHaveBeenCalledTimes(1);
    expect(wrapper.get(".tiptap").attributes("contenteditable")).toBe("false");
  });
  it("does not request a session for a reader", async () => {
    wrapper = mount(DocumentEditor, { props: { ...props, collaborationAllowed: false }, attachTo: document.body });
    await flushPromises();
    expect(joinPageCollaboration).not.toHaveBeenCalled();
    await vi.waitFor(() => expect(wrapper!.get(".tiptap").attributes("contenteditable")).toBe("false"));
  });
  it("keeps local editing disabled while the session is acquired, also for a locked writer", async () => {
    vi.mocked(joinPageCollaboration).mockReturnValue(new Promise(() => {}));
    wrapper = mount(DocumentEditor, { props: { ...props, readonly: false }, attachTo: document.body });
    await flushPromises();
    expect(joinPageCollaboration).toHaveBeenCalledExactlyOnceWith("page", props.identity, false);
    await vi.waitFor(() => expect(wrapper!.get(".tiptap").attributes("contenteditable")).toBe("false"));
    // The page's status chip says a slow join (documentNotice); the document itself never carries the line.
    expect(wrapper.text()).not.toContain("Připojujeme");
    expect(wrapper.emitted("collaborationStatus")!.at(-1)![0]).toMatchObject({ connection: "connecting", error: null, projectionSave: "local" });
    // Locking keeps the session: it only flips the editor's `editable`.
    await wrapper.setProps({ readonly: true });
    expect(joinPageCollaboration).toHaveBeenCalledTimes(1);
  });
  it("fails closed on denied acquisition and permits an explicit retry without losing displayed rich content", async () => {
    vi.mocked(joinPageCollaboration).mockRejectedValue(
      new Error("K úpravě stránky nemáte přístup."),
    );
    wrapper = mount(DocumentEditor, {
      props: { ...props, readonly: false },
      attachTo: document.body,
    });
    await flushPromises();
    expect(wrapper.get('[role="alert"]').text()).toContain("nemáte přístup");
    expect(wrapper.emitted("collaborationStatus")!.at(-1)![0]).toMatchObject({
      connection: "failed", canSave: false,
      error: { kind: "collaboration", message: "K úpravě stránky nemáte přístup." },
      projectionSave: "local",
    });
    expect(wrapper.get(".tiptap").attributes("contenteditable")).toBe("false");
    await wrapper.get('[role="alert"] button').trigger("click");
    await flushPromises();
    expect(joinPageCollaboration).toHaveBeenCalledTimes(2);
  });
});
