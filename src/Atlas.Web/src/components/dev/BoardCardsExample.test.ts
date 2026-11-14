import { afterEach, expect, it, vi } from "vitest";
import { DOMWrapper, mount, type VueWrapper } from "@vue/test-utils";
import { nextTick } from "vue";
import BoardCardsExample from "./BoardCardsExample.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; vi.useRealTimers(); });

it("shows the page card with its content, short, locked without a title, and one that arrives", () => {
  wrapper = mount(BoardCardsExample, { attachTo: document.body });
  expect(wrapper.get("[data-example-page] .bcard-title").text()).toBe("Provozní příručka");
  expect(wrapper.get("[data-example-page] [data-board-excerpt]").text()).toContain("jak se u nás vydává");
  expect(wrapper.find("[data-example-short] [data-board-excerpt]").exists()).toBe(false);
  expect(wrapper.get("[data-example-locked]").text()).toBe("Stránka není dostupná");
  expect(wrapper.get("[data-example-locked]").text()).not.toContain("null");
  expect(wrapper.get("[data-example-arriving] .bcard-title").text()).toBe("Provozní příručka");
});

it("loads the fourth card again: nothing at first, the placeholder only after the defer when the answer is slow", async () => {
  vi.useFakeTimers();
  wrapper = mount(BoardCardsExample, { attachTo: document.body });
  await wrapper.get("[data-example-reload]").trigger("click");
  expect(wrapper.find("[data-example-arriving] [data-board-card]").exists()).toBe(false);
  await vi.advanceTimersByTimeAsync(150);
  expect(wrapper.find("[data-example-arriving] [data-board-pending]").exists()).toBe(true);
  await vi.advanceTimersByTimeAsync(1000);
  await nextTick();
  expect(wrapper.get("[data-example-arriving] .bcard-title").text()).toBe("Provozní příručka");
});

it("reads the pasted text with the real parser: pages (with a section), issues and notes", async () => {
  wrapper = mount(BoardCardsExample, { attachTo: document.body });
  const kinds = () => wrapper!.findAll("[data-example-parsed] li").map(item => item.attributes("data-kind"));
  expect(kinds()).toEqual(["note", "page", "page", "youtrack", "youtrack", "youtrack", "note"]);
  expect(wrapper.findAll("[data-example-parsed] li")[2]!.text()).toContain("sekce „postup“");
  expect(wrapper.findAll("[data-example-parsed] li")[6]!.text()).toBe("Poznámka: Sloupec A Sloupec B");
  await wrapper.get("[data-example-paste]").setValue("Jen text");
  expect(kinds()).toEqual(["note"]);
});

it("opens the pickers on their own search and shows the editor nodes they hand back", async () => {
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
  wrapper = mount(BoardCardsExample, { attachTo: document.body });
  await wrapper.get("[data-example-issues]").trigger("click");
  await nextTick();
  // The picker's panel is portalled to the body.
  const body = new DOMWrapper(document.body);
  await body.get("[data-board-issue-picker] input[type=search]").setValue("zaloha");
  await vi.advanceTimersByTimeAsync(300);
  await nextTick();
  expect(body.find("[data-board-issue-picker] .pick-item").exists()).toBe(false);
  await body.get("[data-board-issue-picker] input[type=search]").setValue("Záloha");
  await vi.advanceTimersByTimeAsync(300);
  await nextTick();
  await body.get("[data-board-issue-picker] input[type=checkbox]").setValue(true);
  await body.get("[data-board-issue-confirm]").trigger("click");
  expect(wrapper.get("[data-example-inserted]").text()).toBe("Poslední vložení: youtrackIssue");
});
