import { describe, expect, it } from "vitest";
import { compareHistoryText, openApiChanges } from "./historyDiff";

describe("history text comparison", () => {
  it("anchors unchanged paragraphs when another paragraph is removed and one is appended", () => {
    const shared = "Po nasazení zkontrolujte dostupnost služby.\n\n";
    const result = compareHistoryText("Starý postup: restartujte všechny služby ručně.\n\n" + shared, shared + "Výsledek zapište do provozního deníku.");
    expect(result.added).toBe(5);
    expect(result.removed).toBe(6);
    expect(result.parts.filter(p => p.kind === "removed").map(p => p.text).join("")).not.toContain("zkontrolujte");
  });
  it("keeps deleted words in context and counts additions separately", () => {
    const result = compareHistoryText("Platí staré přístupové heslo.\nDalší odstavec.", "Platí nové heslo.\nDalší odstavec.");
    expect(result.added).toBe(1);
    expect(result.removed).toBe(2);
    expect(result.parts.filter(p => p.kind !== "added").map(p => p.text).join("")).toBe("Platí staré přístupové heslo.\nDalší odstavec.");
    expect(result.parts.filter(p => p.kind !== "removed").map(p => p.text).join("")).toBe("Platí nové heslo.\nDalší odstavec.");
    expect(result.parts.find(p => p.kind === "removed")?.text).toContain("staré přístupové");
  });
  it("handles creation, identical content and punctuation without invented word changes", () => {
    expect(compareHistoryText("", "První verze")).toMatchObject({ added: 2, removed: 0 });
    expect(compareHistoryText("Stejný text", "Stejný text")).toMatchObject({ added: 0, removed: 0 });
    expect(compareHistoryText("Ano!", "Ano?")).toMatchObject({ added: 0, removed: 0 });
  });
  it("bounds work on a large rewrite while preserving both exact documents", () => {
    const before = "původní ".repeat(3000), after = "nový ".repeat(3000);
    const result = compareHistoryText(before, after);
    expect(result.coarse).toBe(true);
    expect(result.parts.filter(p => p.kind !== "added").map(p => p.text).join("")).toBe(before);
    expect(result.parts.filter(p => p.kind !== "removed").map(p => p.text).join("")).toBe(after);
  });
});


const spec = (title: string, paths = "{}") => `openapi: 3.1.0\ninfo:\n  title: ${title}\n  version: '1.0'\npaths: ${paths}\n`;
const api = (filename: string, source: string, mode?: string) => ({ type: "openapi", attrs: { data: { version: 1, filename, source, ...(mode ? { mode } : {}) } } });
const page = (...content: unknown[]) => JSON.stringify({ format: "atlas-rich", version: 1, document: { type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: "Beze změny" }] }, ...content] } });

describe("OpenAPI changes between revisions", () => {
  it("names a block whose source changed although the Markdown summary stayed the same", () => {
    const before = page(api("billing.yaml", spec("Billing API")));
    const after = page(api("billing.yaml", spec("Billing API", "\n  /invoices:\n    get:\n      responses: {}")));
    expect(openApiChanges(before, after)).toEqual([{ kind: "source", title: "Billing API", filename: "billing.yaml", label: "zdroj změněn" }]);
  });
  it("lists additions, removals and display-mode changes, and ignores untouched blocks", () => {
    const gone = JSON.stringify({ openapi: "3.1.0", info: { title: "Gone", version: "1" }, paths: {} });
    const before = page(api("keep.yaml", spec("Keep")), api("mode.yaml", spec("Mode")), api("gone.json", gone));
    const after = page(api("keep.yaml", spec("Keep")), api("mode.yaml", spec("Mode"), "download"), { type: "blockquote", content: [api("new.yaml", spec("'Nové API'"))] });
    expect(openApiChanges(before, after).map(change => [change.title, change.label])).toEqual([
      ["Mode", "nastavení změněno"], ["Nové API", "přidáno"], ["Gone", "odstraněno"],
    ]);
  });
  it("pairs a renamed file by its unchanged source and falls back to the filename for an unreadable source", () => {
    expect(openApiChanges(page(api("old.yaml", spec("Orders"))), page(api("orders.yaml", spec("Orders")))))
      .toEqual([{ kind: "settings", title: "Orders", filename: "orders.yaml", label: "nastavení změněno" }]);
    expect(openApiChanges(page(api("broken.yaml", "::")), page(api("broken.yaml", "::: still broken"))))
      .toEqual([{ kind: "source", title: "broken.yaml", filename: "broken.yaml", label: "zdroj změněn" }]);
  });
  it("treats the first revision and unreadable JSON as having no blocks before", () => {
    expect(openApiChanges(null, page(api("a.yaml", spec("A"))))).toMatchObject([{ kind: "added", title: "A" }]);
    expect(openApiChanges("not json", page())).toEqual([]);
    expect(openApiChanges(page(), page())).toEqual([]);
  });
});
