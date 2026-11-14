import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { computed, defineComponent, h, ref } from "vue";
import { emptyDocument } from "../document";

// createCodeBlockHighlighting() is invoked once, at module scope, when DocumentEditor.vue
// is first evaluated (finding 10). These tests control exactly when that promise settles
// so we can verify: (1) a rejection never becomes an unhandled rejection, and (2) an
// editor that mounts before the extension resolves still ends up with it once it arrives,
// instead of being permanently built without code highlighting.
let resolveExtension!: (value: unknown) => void;
let rejectExtension!: (reason: unknown) => void;
const loading = vi.hoisted(() => ({ promise: new Promise<unknown>(() => {}) }));

vi.mock("@atlas/markdown/plugins/editor-tools", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@atlas/markdown/plugins/editor-tools")>();
  return { ...actual, createCodeBlockHighlighting: () => loading.promise };
});
vi.mock("@atlas/markdown/vue", () => ({
  MarkdownSourcePanel: defineComponent({ render: () => null }),
  MarkdownDraftConfirmation: defineComponent({ render: () => null }),
  useMarkdownSourceDraft: () => ({ dirty: computed(() => false), busy: ref(false), apply: async () => true, discard() {}, close() {} }),
  MarkdownEditor: defineComponent({
    name: "MarkdownEditorStub",
    props: ["codeBlock"],
    emits: ["created", "change", "failed"],
    setup(props, { emit }) {
      emit("created", { state: {}, subscribe: () => () => {}, getValue: () => emptyDocument() });
      // The real editor captures immutable extensions when it constructs its controller.
      const initialCodeBlock = !!props.codeBlock;
      return () => h("div", { class: "stub-editor", "data-has-code-block": String(initialCodeBlock) });
    },
  }),
}));

// Load the shared source graph once, like ordinary component tests. Only DocumentEditor
// module evaluation must be fresh per case: its loader/ref are module-scoped. Distinct
// query IDs below re-evaluate that SFC without resetting every editor dependency.
import "./DocumentEditor.vue";

let wrapper: VueWrapper | undefined;
const baseProps = { value: emptyDocument(), readonly: false, dark: false, accent: "blue" as const, spaceId: "space" };
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.restoreAllMocks(); });

describe("DocumentEditor code block extension loading (finding 10)", () => {
  it("logs and recovers instead of leaving an unhandled rejection when loading fails", async () => {
    loading.promise = new Promise((_resolve, reject) => { rejectExtension = reject; });
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => {});
    const componentModule = "./DocumentEditor.vue?code-block-loading-failure";
    const { default: DocumentEditor, codeBlockHighlightingReady } = await import(componentModule) as typeof import("./DocumentEditor.vue");
    wrapper = mount(DocumentEditor, { props: baseProps });
    const failure = new Error("network down");
    rejectExtension(failure);
    await expect(codeBlockHighlightingReady).resolves.toBeUndefined();
    expect(errorSpy).toHaveBeenCalledExactlyOnceWith("Nepodařilo se načíst zvýrazňování kódu.", failure);
    expect(wrapper.find(".stub-editor").attributes("data-has-code-block")).toBe("false");
  }, 15000);

  it("gives an editor mounted before resolution the extension once it resolves", async () => {
    loading.promise = new Promise((resolve) => { resolveExtension = resolve; });
    const componentModule = "./DocumentEditor.vue?code-block-loading-delayed";
    const { default: DocumentEditor, codeBlockHighlightingReady } = await import(componentModule) as typeof import("./DocumentEditor.vue");
    wrapper = mount(DocumentEditor, { props: baseProps });
    await flushPromises();
    const initialEditor = wrapper.find(".stub-editor").element;
    expect(wrapper.find(".stub-editor").attributes("data-has-code-block")).toBe("false");
    resolveExtension({ name: "codeBlock" });
    await codeBlockHighlightingReady;
    await flushPromises();
    await vi.waitFor(() => expect(wrapper!.find(".stub-editor").attributes("data-has-code-block")).toBe("true"));
    expect(wrapper.find(".stub-editor").element).not.toBe(initialEditor);
  }, 15000);
});
