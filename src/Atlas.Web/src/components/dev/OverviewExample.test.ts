import { afterEach, beforeAll, expect, it, vi } from "vitest";
import { loadMarkdownDialect } from "@atlas/markdown/core";
import { mount, type VueWrapper } from "@vue/test-utils";
import OverviewExample from "./OverviewExample.vue";

// The contacts overview serializes `a-contact` forms, which need the dialect; it loads lazily, so do not depend on an earlier test.
beforeAll(() => loadMarkdownDialect(), 60_000);
let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.replaceChildren(); });

it("shows the overview of all three kinds on a page and on a board", async () => {
  wrapper = mount(OverviewExample, { attachTo: document.body });
  expect(wrapper.get("[data-example-overview-kinds]").text()).toContain("issues");
  const board = () => wrapper!.get("[data-example-overview-board]");
  await vi.waitFor(() => expect(board().findAll(".overview-read-card")).toHaveLength(3));
  await vi.waitFor(() => expect(board().findAll(".overview-read-card [data-issue]")).toHaveLength(2));
  expect(board().findAll("[data-overview-level]").map(slider => slider.attributes("data-overview-level")).sort()).toEqual(["contacts", "requirements"]);
  // Two overviews of contacts on the page, one slider each: they share one level state.
  await vi.waitFor(() => expect(wrapper!.findAll("[data-example-overview-page] [data-overview-level=contacts]").length).toBeGreaterThanOrEqual(2));
});
