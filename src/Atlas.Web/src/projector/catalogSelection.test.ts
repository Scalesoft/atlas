import { describe, expect, it } from "vitest";
import type { CatalogDefinition } from "./projectorApi";
import { NEW_PAGE_ID, catalogModules, deploymentDocument, planSelection, planSummary } from "./catalogSelection";

const definition = (key: string, bundle: string, direction: CatalogDefinition["direction"] = "inbound", requires: string[] = [], pageId = "catalog"): CatalogDefinition =>
  ({ key, title: key.toUpperCase(), titleMarkdown: key, direction, requires, pageId, bundle });
// As the live catalog: each module's inputs, then „Modul … nasazen“ that needs them and the core; two sections share the name Core.
const catalog = [
  definition("portal.core.vpn", "Core"), definition("portal.core.ssl", "Core"),
  definition("portal.core", "Core", "internal", ["portal.core.vpn", "portal.core.ssl"]),
  definition("portal.nia.cert", "NIA"), definition("portal.nia", "NIA", "internal", ["portal.nia.cert", "portal.core"]),
  definition("mpi.core.vpn", "Core"), definition("mpi.core", "Core", "internal", ["mpi.core.vpn"]),
];

describe("the catalog's modules", () => {
  it("are the runs of definitions under one heading, a repeated name a module of its own, with the keys' common prefix", () => {
    expect(catalogModules(catalog).map(module => [module.title, module.keys.length, module.prefix])).toEqual([
      ["Core", 3, "portal.core"], ["NIA", 2, "portal.nia"], ["Core", 2, "mpi.core"]]);
  });
});

describe("the catalog's categories", () => {
  it("come from the definitions, and a module under another category is a module of its own", () => {
    const categorized = [{ ...definition("a.x", "Core"), category: "Jádro" }, { ...definition("b.x", "Core"), category: "Autentizace" }, definition("c.x", "Core")];
    expect(catalogModules(categorized).map(module => [module.title, module.category])).toEqual([["Core", "Jádro"], ["Core", "Autentizace"], ["Core", ""]]);
  });
});

describe("a selection of the catalog", () => {
  it("brings what it needs, dependencies first, and lays it out by module, a needed module before the one that needs it", () => {
    const modules = catalogModules(catalog);
    const plan = planSelection(catalog, modules, ["portal.nia.cert", "portal.nia"]);
    if ("refused" in plan) throw new Error("refused");
    expect(plan.items.map(item => item.definition.key)).toEqual(["portal.nia.cert", "portal.core.vpn", "portal.core.ssl", "portal.core", "portal.nia"]);
    expect(plan.sections.map(section => [section.module.title, section.items.map(item => item.definition.key)])).toEqual([
      ["Core", ["portal.core.vpn", "portal.core.ssl", "portal.core"]], ["NIA", ["portal.nia.cert", "portal.nia"]]]);
    expect(plan.added.map(item => item.definition.key)).toEqual(["portal.core.vpn", "portal.core.ssl", "portal.core"]);
    expect(plan.inbound).toBe(3);
    expect(planSummary(plan)).toBe("Vloží se 5 požadavků ve 2 modulech, z toho 3 vstupy od zákazníka.");
  });

  it("skips what the page has and says so plainly when nothing is chosen", () => {
    const modules = catalogModules(catalog);
    const plan = planSelection(catalog, modules, ["mpi.core"], ["mpi.core.vpn"]);
    if ("refused" in plan) throw new Error("refused");
    expect(plan.items.map(item => item.definition.key)).toEqual(["mpi.core"]);
    expect(planSummary(plan)).toBe("Vloží se 1 požadavek v 1 modulu, z toho 0 vstupů od zákazníka.");
    const none = planSelection(catalog, modules, []);
    expect("refused" in none ? "" : planSummary(none)).toBe("Zatím nic není vybráno.");
  });

  it("refuses a cycle", () => {
    const cyclic = [definition("a", "X", "internal", ["b"]), definition("b", "X", "internal", ["a"])];
    expect(planSelection(cyclic, catalogModules(cyclic), ["a"])).toEqual({ refused: { code: "cyclic_dependency", path: ["a", "b", "a"] } });
  });
});

describe("a new deployment's document", () => {
  const document = (nested = false) => {
    const modules = catalogModules(catalog);
    const plan = planSelection(catalog, modules, ["portal.nia"]);
    if ("refused" in plan) throw new Error("refused");
    const blocks = new Map(catalog.map(item => [item.key, { type: "requirement" as const, attrs: { id: "6f1c1d3e-8a4b-4c55-9d1e-000000000001", key: item.key, state: "done", party: "x", direction: item.direction, requires: item.requires.join(",") },
      content: [{ type: "paragraph", content: [{ type: "text", text: item.title }] }, ...(nested ? [{ type: "requirement", attrs: { key: `${item.key}.sub`, state: "done" }, content: [{ type: "paragraph", content: [{ type: "text", text: "Podúkol" }] }] }] : [])] }]));
    return deploymentDocument(plan, blocks);
  };
  type Node = { type: string; attrs?: Record<string, unknown>; content?: { text?: string; type?: string; attrs?: Record<string, unknown> }[] };

  it("is the summary, then a heading per module with its requirements as headings under it, and an empty line to write on", () => {
    const { value, ids } = document();
    const content = value.document.content as Node[];
    expect(content.map(node => node.type === "heading" ? `${"#".repeat(node.attrs!.level as number)} ${node.content![0]!.text}` : node.type)).toEqual([
      "overview", "## Core", "### PORTAL.CORE.VPN", "### PORTAL.CORE.SSL", "### PORTAL.CORE", "paragraph", "## NIA", "### PORTAL.NIA.CERT", "### PORTAL.NIA", "paragraph", "paragraph"]);
    expect(ids).toEqual(["portal.core.vpn", "portal.core.ssl", "portal.core", "portal.nia.cert", "portal.nia"]);
    expect(content[2]!.attrs).toMatchObject({ state: "planned", headingId: "portal.core.vpn" });
    expect(value.plugins).toContainEqual({ id: "atlas-requirements", version: 4 });
  });

  it("has no block of the old shape, nested catalog blocks included, and links the dependencies as the page's own headings", () => {
    const { value } = document(true);
    expect(JSON.stringify(value)).not.toContain('"type":"requirement"');
    const content = value.document.content as Node[];
    const line = content.find(node => node.type === "paragraph" && node.attrs?.dependencies)!;
    expect(line.content![0]).toMatchObject({ type: "pageReference", attrs: { id: NEW_PAGE_ID, url: `#/pages/${NEW_PAGE_ID}?heading=portal.core.vpn` } });
    expect(content.filter(node => node.type === "heading" && node.attrs!.level === 4).length).toBeGreaterThan(0);
  });
});
