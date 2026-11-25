import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { VueFlow } from "@vue-flow/core";
import { nextTick } from "vue";
import BoardsExample from "./BoardsExample.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => {
  wrapper?.unmount();
  document.body.innerHTML = "";
});

const flow = () => wrapper!.findComponent(VueFlow);
const batch = () => wrapper!.get("[data-boards-example-batch]").text();
async function click(selector: string) { await wrapper!.get(selector).trigger("click"); await flushPromises(); }

it("runs the real canvas on the real model in memory, and undoes and redoes across kinds of change", async () => {
  wrapper = mount(BoardsExample, { attachTo: document.body });
  await flushPromises();
  expect(wrapper.findAll(".vue-flow__node")).toHaveLength(11);
  expect(wrapper.get('[data-board-item="p2"]').text()).toContain("Stránka není dostupná");

  // A connector's style (props), then the order of a note (z).
  flow().vm.$emit("edgesChange", [{ id: "c1", type: "select", selected: true }]);
  await nextTick();
  await click("[data-board-dash]");
  expect(batch()).toContain("set");
  flow().vm.$emit("edgesChange", [{ id: "c1", type: "select", selected: false }]);
  flow().vm.$emit("nodesChange", [{ id: "n1", type: "select", selected: true }]);
  await nextTick();
  await click("[data-board-front]");
  expect(batch()).toContain("reorder");

  await click("[data-board-undo]");
  expect(batch()).toContain("reorder");
  await click("[data-board-undo]");
  expect(batch()).toContain("set");
  expect(wrapper.get(".vue-flow__edge[data-id='c1'] .board-edge").attributes("data-dash")).toBe("solid");
  await click("[data-board-redo]");
  expect(wrapper.get(".vue-flow__edge[data-id='c1'] .board-edge").attributes("data-dash")).toBe("dashed");
});

it("opens a page card's detail in the dock with the object's tabs, and lists its relation there", async () => {
  wrapper = mount(BoardsExample, { attachTo: document.body });
  await flushPromises();
  // D1: a double-click edits the card; the detail is the named action „Otevřít detail“ on the selected card.
  flow().vm.$emit("nodesChange", [{ id: "p1", type: "select", selected: true }]);
  await flushPromises();
  // The selection's toolbar floats in the body, beside the board.
  document.querySelector<HTMLButtonElement>("[data-board-detail]")!.click();
  await flushPromises();
  expect(wrapper.findAll(".workspace-panel [role=tab]").map(tab => tab.text())).toEqual(["Přehled", "Popis", "Vazby", "Použito na", "Historie", "Diskuse"]);
  expect(wrapper.get("[data-board-object-detail]").text()).toContain("Provozní příručka");
  await wrapper.findAll(".workspace-panel [role=tab]")[2]!.trigger("click");
  expect(wrapper.findAll("[data-board-detail-relation]").map(row => row.text())).toEqual([expect.stringContaining("před vydáním")]);
  // Popis is the card's page, here from the example's own data.
  await wrapper.findAll(".workspace-panel [role=tab]")[1]!.trigger("click");
  await vi.waitFor(() => expect(wrapper!.get("[data-board-object-detail]").text()).toContain("příprava větve"));
});
