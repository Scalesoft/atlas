import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { VueWrapper } from "@vue/test-utils";
import type { Editor } from "@tiptap/core";
import { createMarkdownEditorController, type MarkdownPlugin, type MarkdownRichValue } from "@atlas/markdown/core";
import { createDocumentWidgets } from "../documentWidgets";
import { ySyncPluginKey } from "@tiptap/y-tiptap";
import { isRemote } from "./remoteChange";
import { validAttribute } from "./requirementNode";

const fixture = JSON.parse(readFileSync(resolve(dirname(fileURLToPath(import.meta.url)), "../../../../tests/fixtures/content/requirements.json"), "utf8")) as
  { name: string; content: MarkdownRichValue }[];
const page = fixture[0]!.content;

let editor: Editor | undefined;
const probe: MarkdownPlugin = { id: "atlas-requirements-probe", apiVersion: 1, create: () => ({ onCreate: context => { editor = context.editor; } }) };

function open(value: MarkdownRichValue) {
  const controller = createMarkdownEditorController({ profile: "rich", source: { kind: "local", value }, plugins: [...createDocumentWidgets({ kind: "history" }), probe] });
  const host = document.createElement("div");
  controller.attachView(host, { ariaLabel: "Požadavky" });
  return { controller, host };
}

type Json = { type?: string; attrs?: Record<string, unknown>; content?: Json[] };
const requirementsOf = (node: Json): Record<string, unknown>[] =>
  [...(node.type === "requirement" ? [node.attrs!] : []), ...(node.content ?? []).flatMap(requirementsOf)];

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; });

describe("requirement blocks", () => {
  it("project to the readable Markdown of spec §2.3, the summary included, and report the projection as lossy", () => {
    const { controller } = open(page);
    try {
      const projection = controller.getProjection();
      expect(projection.markdown).toBe([
        "## Portál",
        "",
        "**Stav nasazení:** 33 % hotovo (1 z 3) · čeká na: SMS brána (IT KBN) · nedodané vstupy: SMS brána (IT KBN) · lze nasadit: Otestováno · čeká na vstup: SMS brána",
        "",
        "- [x] **SSL certifikát** — hotovo 10. 9. 2026 · vstup · od: IT KBN · uloženo: Keepass › KBN · klíč: portal.ssl",
        "  Poznámka pod požadavkem…",
        "  - [ ] **Otestováno** — neřešeno · naše práce · klíč: portal.orders.tested · závisí na: portal.orders",
        "",
        "- [ ] **SMS brána** — čeká na podpis · vstup · od: IT KBN · služba: NIA · klíč: portal.sms · závisí na: SSL certifikát, portal.orders",
        "",
        "Volný text stránky.",
      ].join("\n"));
      expect(projection.markdownLossy).toBe(true);
    } finally { controller.destroy(); }
  });

  it("count the whole projected document in a summary nested in a quote", () => {
    const requirement = (n: number, state: string) => ({ type: "requirement", attrs: { id: `6f1c1d3e-8a4b-4c55-9d1e-00000000008${n}`, key: `k${n}`, state }, content: [{ type: "paragraph", content: [{ type: "text", text: `R${n}` }] }] });
    const { controller } = open({ ...page, document: { type: "doc", content: [{ type: "blockquote", content: [{ type: "overview", attrs: { id: "6f1c1d3e-8a4b-4c55-9d1e-000000000080", kind: "requirements" } }] },
      requirement(1, "done"), requirement(2, "planned")] } } as MarkdownRichValue);
    try { expect(controller.getProjection().markdown).toContain("> **Stav nasazení:** 50 % hotovo (1 z 2)"); } finally { controller.destroy(); }
  });

  it("read a collaborator's change by the sync plugin's own key", () => {
    expect((ySyncPluginKey as unknown as { key: string }).key).toBe("y-sync$");
    const { controller } = open(page);
    try {
      expect(isRemote(editor!.state.tr.setMeta(ySyncPluginKey, { isChangeOrigin: true }))).toBe(true);
      expect(isRemote(editor!.state.tr.insertText("x", 1))).toBe(false);
    } finally { controller.destroy(); }
  });

  it("project an empty title, deep nesting, quotes, table cells and a summary without requirements as a reader expects", () => {
    const markdown = (index: number) => { const { controller } = open(fixture[index]!.content); try { return controller.getProjection().markdown; } finally { controller.destroy(); } };
    expect(markdown(1).split("\n")[0]).toBe("- [ ] neřešeno · naše práce");
    const odd = markdown(2);
    expect(odd).toContain([
      "- [ ] **Úroveň 1** — neřešeno · naše práce · klíč: a",
      "  - [x] **Úroveň 2** — hotovo · naše práce · klíč: a.b",
      "    Poznámka úrovně 2",
      "    - [ ] **Úroveň 3** — neřešeno · naše práce · klíč: a.b.c",
      "      - [ ] **Úroveň 4** — neřešeno · naše práce · klíč: a.b.c.d · závisí na: Úroveň 1, Úroveň 2",
    ].join("\n"));
    expect(odd).toContain("> - [ ] **V citaci** — neřešeno · vstup · klíč: in-quote\n>   Poznámka v citaci");
    expect(odd).toContain("- [ ] **V buňce** — neřešeno · naše práce · klíč: in-cell");
    expect(markdown(3)).toBe("**Stav nasazení:** 0 % hotovo (0 z 0)\n\nZatím bez požadavků.");
    // Not applicable and handed over read so.
    expect(markdown(1)).toContain("- [-] **Předat *dokumentaci* a [odkaz](https://example.com/docs) \\[x\\] \\_a\\_** — netýká se 1. 10. 2026 · předání · komu: ");
    // A dependency reads by its title, with the key when two blocks share the title, as the key without a title or a block;
    // the summary names who is waited on.
    expect(markdown(4)).toContain("- [ ] **Závislý** — rozpracováno · naše práce · klíč: dep · závisí na: Test (t1), Test (t2), e, missing");
    expect(markdown(4)).toContain("**Stav nasazení:** 25 % hotovo (1 z 4) · čeká na: Test");
  });

  it("keep special characters of the title, the party and the place as text", () => {
    const { controller } = open(fixture[2]!.content);
    try {
      const line = controller.getProjection().markdown.split("\n").findIndex(text => text.includes("Brána"));
      const lines = controller.getProjection().markdown.split("\n").slice(line, line + 2);
      expect(lines).toEqual([
        "- [ ] **Brána | \\*SMS\\* &lt;b&gt; 😀 \\[x\\] # 1.\\",
        "  druhý řádek 🚀** — blokováno · vstup · od: A | B &lt;c&gt; \\*d\\* 😀 · uloženo: Trezor › |x| \\_y\\_ \\[z\\] · klíč: odd",
      ]);
      // The summary names the title on one line, its line break read as a space.
      expect(controller.getProjection().markdown).toContain(
        "nedodané vstupy: V citaci, Brána | \\*SMS\\* &lt;b&gt; 😀 \\[x\\] # 1. druhý řádek (A | B &lt;c&gt; \\*d\\* 😀)");
    } finally { controller.destroy(); }
  });

  it("refuse what the server refuses: a block that does not start with its title, a party over two lines", () => {
    const withContent = (content: unknown[], attrs: Record<string, unknown> = {}) => ({ ...page, document: { type: "doc", content: [{ type: "requirement", attrs, content }] } }) as MarkdownRichValue;
    const title = { type: "paragraph", content: [{ type: "text", text: "Název" }] };
    expect(() => open(withContent([title])).controller.destroy()).not.toThrow();
    expect(() => open(withContent([{ type: "heading", attrs: { level: 2 }, content: [{ type: "text", text: "Název" }] }])).controller.destroy()).toThrow();
    expect(() => open(withContent([title], { party: "IT\nKBN" })).controller.destroy()).toThrow();
    expect(() => open(withContent([title], { key: "Velká písmena" })).controller.destroy()).toThrow();
  });

  it("refuse a document with an attribute value outside the contract", () => {
    const invalid = JSON.parse(JSON.stringify(page).replace('"state":"done"', '"state":"foo"')) as MarkdownRichValue;
    expect(JSON.stringify(invalid)).toContain('"state":"foo"');
    expect(() => open(invalid).controller.destroy()).toThrow();
  });

  it("compile each attribute pattern once, however often the editor validates the document", () => {
    let compiled = 0;
    vi.stubGlobal("RegExp", new Proxy(RegExp, { construct: (target, args) => { compiled++; return Reflect.construct(target, args); } }));
    try {
      for (let index = 0; index < 500; index++) {
        expect(validAttribute("requires", "portal.ssl,portal.vpn")).toBe(true);
        expect(validAttribute("key", "Portal")).toBe(false);
        expect(validAttribute("date", "2026-10-27")).toBe(true);
      }
    } finally { vi.unstubAllGlobals(); }
    expect(compiled).toBeLessThanOrEqual(3);
  });

  it("project a page of 2 000 paragraphs and 500 blocks with notes no slower than the same lines as plain paragraphs", () => {
    const text = (value: string) => ({ type: "paragraph", content: [{ type: "text", text: value }] });
    const notes = ["Kontakt: IT KBN", "Certifikát platí do září 2027.", "Úkol v YouTracku", "Ověřeno při nasazení."].map(text);
    const pageOf = (blocks: boolean) => ({ format: "atlas-rich", version: 1, plugins: [{ id: "atlas-requirements", version: 1 }], document: { type: "doc", content: [
      ...Array.from({ length: 2000 }, (_, n) => text(`Odstavec ${n} s textem stránky.`)),
      ...Array.from({ length: 500 }, (_, n) => blocks
        ? [{ type: "requirement", attrs: { id: `6f1c1d3e-8a4b-4c55-9d1e-${String(n + 1).padStart(12, "0")}`, key: `k${n}`, direction: "inbound", party: "IT KBN" },
          content: [text(`Požadavek ${n}`), ...notes] }]
        : [text(`Požadavek ${n} — neřešeno · vstup · od: IT KBN · klíč: k${n}`), ...notes]).flat(),
    ] } }) as MarkdownRichValue;
    // The median of three projections, each of a changed document (a projection is kept per document). No warm-up page: the
    // blocks run first and cold, which only counts against them; they project in about half the plain time.
    const measure = (blocks: boolean) => {
      const { controller } = open(pageOf(blocks));
      try {
        const times = Array.from({ length: 3 }, (_, index) => {
          editor!.commands.insertContentAt(editor!.state.doc.content.size - 2, String(index));
          const start = performance.now();
          controller.getProjection();
          return performance.now() - start;
        });
        return times.sort((a, b) => a - b)[1]!;
      } finally { controller.destroy(); }
    };
    expect(measure(true)).toBeLessThan(1.25 * measure(false));
    // The ratio above is the check; building three 3 500-node editors takes ~2.5 s alone and more beside the parallel suite.
  }, 20_000);

});
