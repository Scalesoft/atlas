import { afterEach, expect, it } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import BoardZoomControls from "./BoardZoomControls.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.replaceChildren(); });

it("offers the zoom levels and fitting in the board's ActionMenu: a level zooms, a fit fits, fitting the selection needs one", async () => {
  wrapper = mount(BoardZoomControls, { attachTo: document.body, props: { zoom: 1.15, minimap: false, hasSelection: false } });
  await wrapper.get("[data-board-zoom]").trigger("keydown", { key: "ArrowDown" });
  await flushPromises();
  const menu = document.querySelector<HTMLElement>("[role=menu]")!;
  expect(menu.classList).toContain("md-context-menu");
  const rows = [...menu.querySelectorAll<HTMLElement>("[data-board-zoom-item]")];
  expect(rows.map(row => row.getAttribute("data-board-zoom-item"))).toEqual(["0.5", "1", "2", "content", "selection"]);
  expect(rows[4]!.hasAttribute("data-disabled")).toBe(true);
  rows[0]!.click();
  await flushPromises();
  await wrapper.get("[data-board-zoom]").trigger("keydown", { key: "ArrowDown" });
  await flushPromises();
  document.querySelector<HTMLElement>('[data-board-zoom-item="content"]')!.click();
  await flushPromises();
  expect(wrapper.emitted("zoomTo")).toEqual([[0.5]]);
  expect(wrapper.emitted("fit")).toEqual([["content"]]);
});
