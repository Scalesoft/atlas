import { afterEach, expect, it } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import { nextTick } from "vue";
import PdfCoverDetailsField from "./PdfCoverDetailsField.vue";
import type { PdfCoverDetail } from "../contentExport";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; });

function field(initial: PdfCoverDetail[]) {
  const state = { value: initial };
  wrapper = mount(PdfCoverDetailsField, {
    props: { modelValue: initial, title: "Příručka Acme Care", space: "Zdravotnictví", "onUpdate:modelValue": (value: PdfCoverDetail[]) => { state.value = value; void wrapper!.setProps({ modelValue: value }); } },
    attachTo: document.body,
  });
  return state;
}
const preset = () => wrapper!.find("[data-pdf-cover-preset] [data-state=checked]").attributes("data-value");
const paper = () => wrapper!.find(".pdf-paper").text();

it("offers presets, keeps the details in the server's order and shows the cover with only what is ticked", async () => {
  const state = field(["revision", "state", "revisionDate", "exportedAt", "pageId"]);
  expect(preset()).toBe("all");
  expect(paper()).toContain("Příručka Acme Care");
  expect(paper()).toContain("Revize 12");
  expect(paper()).toContain("ID stránky");
  // The PDF carries no product branding, on the cover or in the page header.
  expect(paper()).not.toMatch(/ATLAS/);

  await wrapper!.get("[data-pdf-cover-preset] [data-value=formal]").trigger("click"); await nextTick();
  expect(state.value).toEqual(["revision", "revisionDate"]);
  expect(preset()).toBe("formal");

  await wrapper!.get("[data-pdf-cover-detail=pageId]").trigger("click"); await nextTick();
  expect(state.value).toEqual(["revision", "revisionDate", "pageId"]);
  // A choice that matches no preset leaves every preset unselected.
  expect(wrapper!.find("[data-pdf-cover-preset] [data-state=checked]").exists()).toBe(false);

  await wrapper!.get("[data-pdf-cover-preset] [data-value=none]").trigger("click"); await nextTick();
  expect(state.value).toEqual([]);
  expect(paper()).not.toContain("Revize 12");
  expect(paper()).toContain("Zdravotnictví · Dokumentace");
});
