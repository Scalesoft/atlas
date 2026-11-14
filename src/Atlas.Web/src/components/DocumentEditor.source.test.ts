import { defineComponent, h, nextTick, onMounted, onUnmounted, watch } from "vue";
import { afterEach, it, expect, vi } from "vitest";
import { mount, flushPromises, type VueWrapper } from "@vue/test-utils";
import DocumentEditor from "./DocumentEditor.vue";
import { emptyDocument } from "../document";
import { joinPageCollaboration } from "../collaboration";
vi.mock("../collaboration", async (original) => ({
  ...(await original<object>()),
  joinPageCollaboration: vi.fn(),
}));
let wrapper: VueWrapper | undefined;
afterEach(() => {
  wrapper?.unmount();
  vi.clearAllMocks();
});
it("creates a new controller for a collaborative source while appearance keeps that controller", async () => {
  const mounts: string[] = [],
    destroyed: string[] = [];
  const editorBoundary = defineComponent({
    name: "MarkdownEditor",
    props: ["collaboration", "modelValue", "readonly"],
    emits: ["created"],
    setup(props, { emit, expose }) {
      const source = props.collaboration ? "collaborative" : "local";
      mounts.push(source);
      watch(
        () => props.collaboration,
        () => {
          throw new Error(
            "Remount MarkdownEditor with a new key to switch document source.",
          );
        },
      );
      const controller = {
          state: {
            binding: source,
            lifecycle: "ready",
            connection: source === "local" ? "local" : "online",
            documentSave: "saved",
            projectionSave: "current",
            pendingChanges: 0,
            error: null,
            editable: true,
          },
          subscribe: () => () => {},
          query: () => ({ enabled: false }), getValue: () => emptyDocument(),
          getProjection: () => ({ markdown: "" }),
        };
      expose({ controller });
      onMounted(() => emit('created', controller));
      onUnmounted(() => destroyed.push(source));
      return () => h("div", { "data-source": source });
    },
  });
  let joined!: (value: any) => void;
  vi.mocked(joinPageCollaboration).mockReturnValue(
    new Promise((resolve) => (joined = resolve)),
  );
  wrapper = mount(DocumentEditor, {
    props: {
      value: emptyDocument(),
      readonly: true,
      dark: false,
      accent: "blue",
      spaceId: "space",
      pageId: "page",
      identity: { id: "user", displayName: "User" },
      collaborationAllowed: true,
    },
    global: { stubs: { MarkdownEditor: editorBoundary } },
  });
  await flushPromises();
  await wrapper.setProps({ readonly: false });
  joined({
    lease: { documentId: "page:one", generation: "one", currentVersion: 1 },
    adapter: {},
  });
  await flushPromises();
  expect(wrapper.find('[role="alert"]').exists()).toBe(false);
  // The local editor stands in while the session joins; the session's controller then replaces it, once.
  expect(mounts).toEqual(["local", "collaborative"]);
  expect(destroyed).toEqual(["local"]);
  await wrapper.setProps({ dark: true, accent: "green" });
  expect(mounts).toEqual(["local", "collaborative"]);
  // Locking keeps the session and its editor.
  await wrapper.setProps({ readonly: true });
  await flushPromises();
  expect(mounts).toEqual(["local", "collaborative"]);
  expect(destroyed).toEqual(["local"]);
});

it("returns to the preserved local document and offers retry when the provider fails before created", async () => {
  const initial = emptyDocument();
  const localValues: unknown[] = [];
  const editorBoundary = defineComponent({
    name: "MarkdownEditor",
    props: ["collaboration", "modelValue"],
    emits: ["created"],
    setup(props, { emit, expose }) {
      const collaborative = !!props.collaboration;
      const listeners = new Set<(state: unknown) => void>();
      const state = { binding: collaborative ? 'collaborative' : 'local', lifecycle: collaborative ? 'starting' : 'ready', connection: collaborative ? 'connecting' : 'local', documentSave: 'local', projectionSave: 'local', pendingChanges: 0, error: null as unknown, editable: false };
      const controller = { state, subscribe: (listener: (state: unknown) => void) => { listeners.add(listener); return () => listeners.delete(listener); }, query: () => ({ enabled: false }), getValue: () => props.modelValue, getProjection: () => ({ markdown: '' }) };
      expose({ controller });
      onMounted(() => {
        if (collaborative) {
          void nextTick(() => {
            state.lifecycle = 'failed'; state.connection = 'failed'; state.error = {
            kind: "collaboration",
            message: "Server odmítl schéma dokumentu.",
            recoverable: false,
            };
            for (const listener of [...listeners]) listener(state);
          });
          return;
        }
        localValues.push(props.modelValue);
        emit('created', controller);
      });
      return () => h("div", { "data-source": collaborative ? "collaborative" : "local" });
    },
  });
  vi.mocked(joinPageCollaboration).mockResolvedValue({
    lease: { documentId: "page:one", generation: "one", currentVersion: 1 },
    adapter: {} as never,
  });
  wrapper = mount(DocumentEditor, {
    props: {
      value: initial,
      readonly: false,
      dark: false,
      accent: "blue",
      spaceId: "space",
      pageId: "page",
      identity: { id: "user", displayName: "User" },
      collaborationAllowed: true,
    },
    global: { stubs: { MarkdownEditor: editorBoundary } },
  });

  await flushPromises();
  await flushPromises();
  expect(wrapper.get('[role="alert"]').text()).toContain("Server odmítl schéma dokumentu.");
  // The preserved document stays readable in the local editor, which the failed session never replaced.
  expect(localValues).toEqual([initial]);
  expect(wrapper.findAll("[data-source]").map(found => found.attributes("data-source"))).toEqual(["local"]);

  await wrapper.get('[role="alert"] button').trigger("click");
  await flushPromises();
  expect(joinPageCollaboration).toHaveBeenCalledTimes(2);
});
