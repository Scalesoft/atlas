import { afterEach, beforeAll, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { loadMarkdownDialect } from "@atlas/markdown/core";
import BoardEditorExample from "./BoardEditorExample.vue";

// Editor 0.18.0 loads the dialect of `a-` components (the contact block) on demand; in the app DocumentEditor's module starts it.
beforeAll(() => loadMarkdownDialect());

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.replaceChildren(); });

it("shows the editor's blocks on a board, the unknown one as a fallback, and the board's component menu", async () => {
  wrapper = mount(BoardEditorExample, { attachTo: document.body });
  await vi.waitFor(() => expect(wrapper!.get("[data-example-block=contact]").text()).toContain("Jana Nováková"));
  await vi.waitFor(() => expect(wrapper!.get("[data-example-block=youtrack]").text()).toContain("ATL-123"));
  expect(wrapper.get("[data-example-block=unknown] [data-board-block-fallback]").text()).toContain("kanban");
  expect(wrapper.find("[data-example-modules] [data-module='contacts.insert']").exists()).toBe(true);
  expect(wrapper.find("[data-example-modules] [data-module='attachments.insert']").exists()).toBe(false);
});

it("renders the note's Markdown and edits it in place with the page editor", async () => {
  wrapper = mount(BoardEditorExample, { attachTo: document.body });
  expect(wrapper.get("[data-example-note] h1").text()).toBe("Plán vydání");
  await wrapper.get("[data-example-note]").trigger("dblclick");
  await vi.waitFor(() => expect(wrapper!.find("[data-example-note] .ProseMirror[contenteditable=true]").exists()).toBe(true));
  expect(wrapper.get("[data-example-label] strong").text()).toBe("vedoucí");
});

it("previews the page, one section, a section that is gone and a page the reader may not see, and refreshes on a change", async () => {
  wrapper = mount(BoardEditorExample, { attachTo: document.body });
  await flushPromises();
  await vi.waitFor(() => expect(wrapper!.get("[data-example-preview-page]").text()).toContain("Verze 1."));
  expect(wrapper.get("[data-example-preview-section]").text()).toContain("Připravte větev");
  expect(wrapper.get("[data-example-preview-section]").text()).not.toContain("Jana");
  expect(wrapper.get("[data-example-preview-missing]").text()).toContain("Sekce už ve stránce není");
  await vi.waitFor(() => expect(wrapper!.get("[data-example-preview-locked]").text()).toBe("Stránka není dostupná"));
  await wrapper.get("[data-example-change-page]").trigger("click");
  await vi.waitFor(() => expect(wrapper!.get("[data-example-preview-page]").text()).toContain("Verze 2."), { timeout: 3000 });
});

it("draws a deployment's product and service from the page, folds a category and follows a change of the page", async () => {
  wrapper = mount(BoardEditorExample, { attachTo: document.body });
  const product = () => wrapper!.get("[data-example-deployment-product]"), service = () => wrapper!.get("[data-example-deployment-service]");
  await vi.waitFor(() => expect(product().find("[data-deployment-card=product]").exists()).toBe(true));
  expect(product().findAll(".dcard-tag").map(tag => tag.text())).toEqual(["Server", "DNS záznam", "SSL certifikát"]);
  expect(product().findAll("[data-deployment-category] .dcard-cat-name").map(name => name.text())).toEqual(["Jádro", "Autentizace"]);
  expect(product().findAll("[data-deployment-module]").map(row => row.text())).toEqual(["Základ portálu0/1", "NIA0/2", "BankID0/1"]);
  expect(product().get("[data-deployment-module]").attributes("href")).toContain("zaklad");
  // The tone and the dots read out too.
  expect(product().get("[data-deployment-module]").attributes("aria-label")).toMatch(/^Základ portálu — \S.*, 0\/1 hotovo$/);
  expect(product().get(".dcard-cat-head").attributes("aria-label")).toMatch(/^Jádro — \S.*, 1 modul$/);
  expect(product().get(".dcard-tag").attributes("aria-label")).toMatch(/^Server — \S/);
  await product().findAll(".dcard-cat-head")[1]!.trigger("click");
  expect(product().findAll(".dcard-cat-head")[1]!.attributes("aria-expanded")).toBe("false");
  await vi.waitFor(() => expect(product().findAll("[data-deployment-module]")).toHaveLength(1));
  // The service counts its inputs once, though the MPI needs the portal's certificate too.
  expect(service().text()).toContain("2 vstupy · 0/2 hotovo");
  expect(service().text()).toContain("Portál pacienta i MPI");
  expect(wrapper.get("[data-example-deployment-missing]").text()).toContain("Služba už ve stránce není");
  // The order's own section is the summary: the whole page's progress over its own requirements, marked apart from the systems.
  const order = wrapper.get("[data-example-deployment-order] [data-deployment-order]");
  expect(order.text()).toContain("2 produkty · celkem 3/10 hotovo");
  expect(order.findAll(".dcard-tag").map(tag => tag.text())).toEqual(["Podepsaný dodatek", "Potvrzený harmonogram"]);
  expect(product().find("[data-deployment-order]").exists()).toBe(false);
  await wrapper.get("[data-example-deliver]").trigger("click");
  await vi.waitFor(() => expect(service().text()).toContain("1/2 hotovo"), { timeout: 3000 });
});
