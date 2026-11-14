import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import RelationsGraph from "./RelationsGraph.vue";
import BoardCanvas from "../boards/BoardCanvas.vue";
import { GROUP_LIMIT, groupId } from "../../objects/relationGraph";
import type { RelationGraph } from "../../objects/relationsApi";

const node = (id: string, typeId = "atlas.page", extra = {}) => ({ id, typeId, title: id, spaceId: "s", spaceName: "Provoz", readable: true, ...extra });
const small: RelationGraph = { nodes: [node("hub"), node("a"), node("b", "atlas.board")], edges: [{ sourceId: "hub", targetId: "a", kind: "link", count: 1 }, { sourceId: "hub", targetId: "b", kind: "placement", count: 1 }] };
const many: RelationGraph = {
  nodes: [node("hub"), ...Array.from({ length: GROUP_LIMIT + 6 }, (_, index) => node(`n${index}`, "atlas.page", { spaceName: "Provoz" }))],
  edges: [{ sourceId: "hub", targetId: "n0", kind: "link", count: 1 }],
};
let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; });
async function show(graph: RelationGraph | null, props: Record<string, unknown> = {}) {
  wrapper = mount(RelationsGraph, { props: { graph, focusId: "hub", label: "Okolí", ...props }, attachTo: document.body });
  await flushPromises();
  return wrapper;
}
const canvas = () => wrapper!.findComponent(BoardCanvas);
const itemsOf = () => canvas().props("items") as readonly { id: string; kind: string; props?: { collapsed?: boolean } }[];

describe("RelationsGraph", () => {
  it("keeps the 50 % minimum zoom of a small graph only on a container wide enough to read it", async () => {
    await show(small);
    expect(canvas().props("fitMinZoom")).toBe(0.5);
    wrapper!.unmount();
    const width = vi.spyOn(HTMLElement.prototype, "clientWidth", "get").mockReturnValue(390);
    await show(small);
    expect(canvas().props("fitMinZoom")).toBeUndefined();
    width.mockRestore();
  });

  it("is a read-only BoardCanvas over derived items with the subject preselected", async () => {
    await show(small);
    expect(canvas().props("canEdit")).toBe(false);
    expect(canvas().props("preselect")).toBe("hub");
    expect(itemsOf().filter(item => item.kind === "page")).toHaveLength(3);
    expect(itemsOf().filter(item => item.kind === "connector")).toHaveLength(2);
  });

  it("tells what is selected (a click shows the dock) and opens a node on Ctrl+Enter or from the actions menu, never the stand-in", async () => {
    const graph = await show({ ...small, edges: [...small.edges, { sourceId: "a", targetId: "gone", kind: "link", count: 1 }] });
    // The subject selected on opening is not a choice: no dock until the reader touches the graph.
    canvas().vm.$emit("selection", ["hub"]);
    expect(graph.emitted("select")).toBeUndefined();
    await graph.get("[data-relations-graph]").trigger("pointerdown");
    canvas().vm.$emit("selection", ["b"]);
    expect(graph.emitted("select")).toEqual([["b", "atlas.board"]]);
    canvas().vm.$emit("selection", ["a", "b"]);
    expect(graph.emitted("select")).toHaveLength(1);
    canvas().vm.$emit("openDetail", "a");
    expect(graph.emitted("open")).toEqual([["a", "atlas.page"]]);
    canvas().vm.$emit("openDetail", "unavailable");
    canvas().vm.$emit("selection", ["unavailable"]);
    expect(graph.emitted("open")).toHaveLength(1);
    expect(graph.emitted("select")).toHaveLength(1);
  });

  it("goes back to its trigger on Escape from the subject or from nothing, not from another node", async () => {
    const graph = await show(small);
    const press = () => graph.get("[data-relations-graph]").element.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    canvas().vm.$emit("selection", ["a"]);
    press();
    expect(graph.emitted("back")).toBeUndefined();
    canvas().vm.$emit("selection", ["hub"]);
    press();
    expect(graph.emitted("back")).toHaveLength(1);
    canvas().vm.$emit("selection", []);
    press();
    expect(graph.emitted("back")).toHaveLength(2);
  });

  it("folds a graph past the limit into groups and opens one on a click on its card", async () => {
    const graph = await show(many);
    const folded = itemsOf().filter(item => item.kind === "group");
    expect(folded).toHaveLength(1);
    expect(folded[0]!.props?.collapsed).toBe(true);
    const card = graph.find(`.vue-flow__node[data-id="${groupId("Provoz")}"] .board-group-card`);
    expect(card.exists()).toBe(true);
    await card.trigger("click");
    expect(itemsOf().find(item => item.id === groupId("Provoz"))?.props?.collapsed).toBe(false);
  });

  it("drags a node out as an object", async () => {
    const graph = await show(small);
    const data = new Map<string, string>();
    const target = graph.get('.vue-flow__node[data-id="b"]');
    await target.trigger("mouseover");
    expect((target.element as HTMLElement).draggable).toBe(true);
    await target.trigger("dragstart", { dataTransfer: { setData: (type: string, value: string) => data.set(type, value) } });
    expect(JSON.parse(data.get("application/x-atlas-object")!)).toMatchObject({ id: "b", typeId: "atlas.board", title: "b" });
  });

  it("says plainly that an object has no relations, and draws nothing before the graph arrives", async () => {
    const alone = await show({ nodes: [node("hub")], edges: [] });
    expect(alone.text()).toContain("Bez vazeb");
    alone.unmount(); wrapper = undefined;
    const waiting = await show(null);
    expect(waiting.findComponent(BoardCanvas).exists()).toBe(false);
  });
});
