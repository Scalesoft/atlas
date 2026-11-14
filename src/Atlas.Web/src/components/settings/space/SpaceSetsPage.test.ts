import { afterEach, expect, it, vi } from "vitest";
import { flushPromises } from "@vue/test-utils";
import { clearCsrf } from "../../../api";
import { mountPage, testContext } from "../general/testing";
import SpaceSetsPage from "./SpaceSetsPage.vue";

let mounted: ReturnType<typeof mountPage> | undefined;
afterEach(() => {
  mounted?.wrapper.unmount();
  mounted = undefined;
  vi.unstubAllGlobals();
  document.body.innerHTML = "";
  history.replaceState(null, "", "/");
});

const definition = (key: string, bundle: string, category: string, product: string) =>
  ({ key, title: key, titleMarkdown: key, direction: "inbound", requires: [], pageId: "catalog", bundle, category, product });

// Plan 3.5 C, O1 and O3: the catalog's size and products, each set with what it is, its modules, and the blocks that read otherwise than the catalog.
it("shows the catalog by product and each set with its first paragraph and the requirements whose text differs from the catalog", async () => {
  clearCsrf();
  vi.stubGlobal("fetch", vi.fn(async (input: string) => {
    const path = new URL(String(input), "http://atlas.test").pathname;
    if (path.endsWith("/catalog")) return Response.json({ definitions: [definition("a", "Základ", "Jádro", "Portál pacienta"), definition("b", "Webové objednávání", "Rezervace", "Portál pacienta"),
      definition("c", "Webové objednávání", "Rezervace", "Portál pacienta"), definition("d", "Instalace", "MPI", "")], warnings: ["Klíč „a“ je v katalogu dvakrát."] });
    if (path.endsWith("/presets")) return Response.json([{ pageId: "std", title: "Standardizace", requirements: 52, summary: "Dodávka MPI a Portálu v rozsahu dodatku.",
      products: ["MPI", "Portál pacienta"], modules: 8, differs: ["SSL certifikát"] }]);
    return Response.json({ token: "csrf" });
  }));
  const context = testContext();
  mounted = mountPage(SpaceSetsPage, { spaceId: "portal" }, { hash: "#/spaces/portal/settings/sets", context });
  await flushPromises();
  const catalog = mounted.wrapper.get("[data-sets-catalog]");
  expect(catalog.text()).toContain("4 požadavky · 2 produkty · 3 moduly");
  expect(catalog.text()).toContain("Portál pacienta (2 moduly)");
  expect(catalog.get("[data-sets-catalog-warning]").text()).toContain("dvakrát");
  const set = mounted.wrapper.get('[data-sets-preset="std"]');
  expect(set.text()).toContain("Dodávka MPI a Portálu v rozsahu dodatku.");
  expect(set.text()).toContain("52 požadavků · 8 modulů · MPI · Portál pacienta");
  expect(set.get("[data-sets-differs]").text()).toBe("1 požadavek má jiný text než katalog");
  await set.get("button").trigger("click");
  expect(context.openPage).toHaveBeenCalledWith("std");
});
