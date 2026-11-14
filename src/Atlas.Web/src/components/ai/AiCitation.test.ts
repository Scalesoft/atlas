import { afterEach, expect, it } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import AiCitation from "./AiCitation.vue";
import AiSourceList from "./AiSourceList.vue";
import { source, sources } from "../dev/ai/aiSampleData";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.replaceChildren(); });

it("names the source for assistive technology, highlights on focus and opens the current page at its heading", async () => {
  wrapper = mount(AiCitation, { props: { source: source(2) }, attachTo: document.body });
  const button = wrapper.get("[data-cite='2']");
  expect(button.attributes("aria-label")).toBe("Zdroj 2: Stránka 2");
  expect(button.text()).toBe("2");

  await button.trigger("focus");
  await button.trigger("blur");
  await button.trigger("mouseenter");
  await button.trigger("mouseleave");
  expect(wrapper.emitted("highlight")).toEqual([[2], [null], [2], [null]]);

  await button.trigger("click");
  await flushPromises();
  const preview = document.querySelector("[data-ai-citation-preview]")!;
  expect(preview.textContent).toContain("Stránka 2 › Nadpis 2");
  expect(preview.textContent).toContain("IT a infrastruktura");
  expect(preview.textContent).toContain("Úryvek 2.");
  const link = preview.querySelector<HTMLAnchorElement>("[data-ai-open-source]")!;
  // the page as it is now, at the heading; never a revision
  expect(link.getAttribute("href")).toBe("#/pages/page-2?heading=h-2");
  expect(link.getAttribute("href")).not.toMatch(/revision|version/i);
  link.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true, ctrlKey: true }));
  link.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true }));
  expect(wrapper.emitted("open-source")!.map(([, newTab]) => newTab)).toEqual([true, false]);
  expect(preview.querySelector("[data-ai-open-source]")!.textContent).toBe("Otevřít stránku");
});

it("says when a source changed since the answer and offers to ask again", async () => {
  wrapper = mount(AiCitation, { props: { source: source(1, { changed: true, excerpt: null }) }, attachTo: document.body });
  await wrapper.get("[data-cite]").trigger("click");
  await flushPromises();
  const preview = document.querySelector("[data-ai-citation-preview]")!;
  expect(preview.textContent).toContain("Zdroj se od té doby změnil.");
  expect(preview.querySelector("blockquote")).toBeNull();
  [...preview.querySelectorAll("button")].find((b) => b.textContent === "Zeptat se znovu")!.click();
  expect(wrapper.emitted("ask-again")).toHaveLength(1);
});

it("lists sources as a tray of links or as a list with the space, uncited and changed ones", async () => {
  const mixed = [source(1), source(2, { cited: false }), source(3, { changed: true })];
  wrapper = mount(AiSourceList, { props: { sources: mixed, variant: "list" }, attachTo: document.body });
  expect(wrapper.findAll("[data-source-chip]").map((chip) => chip.attributes("href"))).toEqual(["#/pages/page-1?heading=h-1", "#/pages/page-2?heading=h-2", "#/pages/page-3?heading=h-3"]);
  expect(wrapper.text()).toContain("použito, necitováno");
  expect(wrapper.text()).toContain("Zdroj se od té doby změnil.");
  await wrapper.findAll("button").find((b) => b.text() === "Zeptat se znovu")!.trigger("click");
  expect(wrapper.emitted("ask-again")).toHaveLength(1);

  await wrapper.setProps({ variant: "tray", hiddenNumbers: [2], highlighted: 3 });
  expect(wrapper.find("[data-source-chip='2']").attributes("style")).toContain("visibility: hidden");
  expect(wrapper.find("[data-source-chip='3']").classes()).toContain("is-hl");
  expect(wrapper.text()).not.toContain("použito, necitováno");
  expect(mount(AiSourceList, { props: { sources: [] } }).find("ul").exists()).toBe(false);
  expect(sources).toHaveLength(3);
});
