import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { ref } from "vue";
import { boardCanvasKey, type BoardCanvasContext } from "./boardCanvasContext";
import BoardNode from "./BoardNode.vue";

// The page's preview, as the node passes it its visibility.
const previews = vi.hoisted(() => ({ props: [] as Record<string, unknown>[] }));
vi.mock("../preview/PagePreview.vue", async () => {
  const { defineComponent, h } = await import("vue");
  return { __esModule: true, default: defineComponent({
    props: { pageId: String, anchor: String, visible: Boolean, editing: Boolean, at: Object },
    setup(props) { previews.props.push(props); return () => h("div", { "data-stub-preview": "" }); },
  }) };
});

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; previews.props.length = 0; vi.unstubAllGlobals(); });

describe("BoardNode: a page's preview", () => {
  function previewNode() {
    let seen: IntersectionObserverCallback | undefined;
    vi.stubGlobal("IntersectionObserver", class { constructor(callback: IntersectionObserverCallback) { seen = callback; } observe() {} unobserve() {} disconnect() {} });
    const overview = ref(false), editing = ref<string>();
    const ctx = { canEdit: ref(true), tool: ref("select"), editing, editPoint: ref(), entering: ref(), overview, single: ref(), shift: ref(false), highlight: ref(null),
      dropTarget: ref(new Set()), toneOf: () => undefined, storedHeight: () => undefined,
      per: { single: () => false, editing: (id: string) => editing.value === id, mark: () => undefined, linkMark: () => undefined, dropTarget: () => false, hoveredEdge: () => false } } as unknown as BoardCanvasContext;
    const item = { id: "p1", kind: "page", x: 0, y: 0, width: 420, height: 480, z: 1, targetResourceId: "page-1", props: { display: "preview" } };
    wrapper = mount(BoardNode, { props: { id: "p1", data: { item } as never }, global: { provide: { [boardCanvasKey as symbol]: ctx }, stubs: { Handle: true, NodeResizer: true, NodeResizeControl: true } } });
    const near = (isIntersecting: boolean) => seen!([{ isIntersecting } as IntersectionObserverEntry], {} as IntersectionObserver);
    return { near, overview, editing };
  }
  const visible = () => previews.props.at(-1)?.visible;

  it("reads the page only once its card comes near the screen, and not while the board is too far out to read it", async () => {
    const { near, overview } = previewNode();
    await vi.waitFor(() => expect(previews.props).toHaveLength(1));
    expect(visible(), "off screen: no read").toBe(false);
    near(true);
    await flushPromises();
    expect(visible()).toBe(true);
    overview.value = true;
    await flushPromises();
    expect(visible(), "zoomed out below a readable size").toBe(false);
  });

  it("reads an edited preview wherever it is", async () => {
    const { editing } = previewNode();
    await vi.waitFor(() => expect(previews.props).toHaveLength(1));
    editing.value = "p1";
    await flushPromises();
    expect(visible()).toBe(true);
  });
});
