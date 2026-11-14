import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { defineComponent, h } from "vue";
import { TooltipProvider } from "../../ui/tooltip";
import SemanticSearchResultExample from "./SemanticSearchResultExample.vue";
import SemanticBadge from "../../search/SemanticBadge.vue";
import ResultPassage from "../../search/ResultPassage.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; vi.useRealTimers(); });
const within = (inner: () => ReturnType<typeof h>) => defineComponent({ render: () => h(TooltipProvider, null, { default: inner }) });

it("marks a meaning-only result with a neutral badge and shows its passage under its heading", () => {
  wrapper = mount(within(() => h("div", [h(SemanticBadge), h(ResultPassage, { text: "Pošleme odkaz.", headingTitle: "Ztráta přístupu", query: "heslo" })])), { attachTo: document.body });
  expect(wrapper.get("[data-semantic-badge]").text()).toBe("Významová shoda");
  expect(wrapper.get("[data-semantic-badge] .status-badge").attributes("data-tone")).toBe("neutral");
  expect(wrapper.get("[data-search-passage]").text()).toContain("Ztráta přístupu");
  expect(wrapper.get("[data-search-passage]").text()).toContain("Pošleme odkaz.");
  expect(wrapper.find("[data-ai-glyph]").exists()).toBe(false);
});

it("lets the meaning rows arrive below the selected row without moving it, and never marks a lexical or draft row", async () => {
  vi.useFakeTimers();
  wrapper = mount(within(() => h(SemanticSearchResultExample)), { attachTo: document.body });
  const ids = () => wrapper!.findAll("[data-row]").map(row => row.attributes("data-row"));
  await vi.advanceTimersByTimeAsync(50); await flushPromises();
  expect(ids()).toEqual(["login", "profile"]);
  await vi.advanceTimersByTimeAsync(400); await flushPromises();
  expect(ids()).toEqual(["login", "profile", "recovery", "reset"]);
  expect(wrapper.findAll("[data-row][data-semantic]").map(row => row.attributes("data-row"))).toEqual(["recovery", "reset"]);
  expect(wrapper.get('[data-row="profile"]').attributes("aria-selected")).toBe("true");
  expect(wrapper.get('[data-row="login"]').find("[data-semantic-badge]").exists()).toBe(false);
  expect(wrapper.find("[data-ai-glyph], .atlas-ai-twinkle, .atlas-ai-shimmer").exists()).toBe(false);
});
