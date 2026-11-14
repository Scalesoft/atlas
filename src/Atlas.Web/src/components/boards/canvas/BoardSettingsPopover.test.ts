import { afterEach, expect, it } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import BoardSettingsPopover from "./BoardSettingsPopover.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.replaceChildren(); });

const settings = { background: "dots" as const, snapToGrid: true, gridSize: 16 as const };

it("opens as the page editor's context panel: its fields and choices, each choice with its icon", async () => {
  wrapper = mount(BoardSettingsPopover, { attachTo: document.body, props: { settings, canEdit: true, open: true } });
  await flushPromises();
  const panel = document.querySelector<HTMLElement>("[data-board-settings]")!;
  expect(panel.classList).toContain("md-context-popover");
  expect(panel.querySelector(".md-context-fields")).not.toBeNull();
  const options = [...panel.querySelectorAll<HTMLButtonElement>("[data-board-background-option]")];
  expect(options.map(option => option.getAttribute("aria-checked"))).toEqual(["true", "false", "false", "false"]);
  expect(options.every(option => option.closest(".md-context-choices") && option.querySelector("svg"))).toBe(true);
  options[3]!.click();
  expect(wrapper.emitted("change")?.[0]?.[0]).toMatchObject({ background: "timeline" });
});
