import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { beforeAll, describe, expect, it } from "vitest";
import { createMarkdownEditorController, loadMarkdownDialect, type MarkdownRichValue } from "@atlas/markdown/core";
import { createMarkdownSourceSession, type MarkdownSourceApplyResult } from "@atlas/markdown/source";
import { atlasContentPlugins } from "../documentWidgets";
import { headingBlockStart, readHeadingBlock, writeHeadingBlock } from "./requirementHeadingMarkdown";

/*
 * A requirement heading's Markdown, `## Title {#id state=… kind=…}`, is the server's (RequirementHeadingMarkdown.cs):
 * - the editor writes every heading of tests/fixtures/content/requirement-heading-lines.json (the server's lines for the headings of
 *   requirement-headings.json and a few with quoted values) as the server does, and reads the line back to the same attributes;
 * - in the page source („Zdroj“) such a heading is editable text whose identity is its id: moved, it keeps its state; deleted, it takes
 *   nothing along; an id twice or an id the page does not have is an error on its line.
 */

type Json = { type: string; text?: string; attrs?: Record<string, unknown>; content?: Json[] };
const lines = JSON.parse(readFileSync(resolve(dirname(fileURLToPath(import.meta.url)), "../../../../tests/fixtures/content/requirement-heading-lines.json"), "utf8")) as
  { heading: Json; markdown: string }[];
const plugins = () => atlasContentPlugins({ kind: "edit", canWrite: () => false });
const issue = { id: "2-42", title: "Úkol", url: "https://youtrack.example/issue/PX2-42" };
// A null attribute is the node's default; the editor's YouTrack node also needs the issue's id, title and url (as requirementHeadingParity.test.ts).
const withoutNulls = (node: Json): Json => ({ ...node,
  ...(node.attrs ? { attrs: { ...(node.type === "youtrackIssue" ? issue : {}), ...Object.fromEntries(Object.entries(node.attrs).filter(([name, value]) => value !== null || name === "state")) } } : {}),
  ...(node.content ? { content: node.content.map(withoutNulls) } : {}) });
const metadata = [{ id: "atlas-requirements", version: 4 }, { id: "heading-anchors", version: 2 }, { id: "pages", version: 1 }, { id: "contacts", version: 2 }, { id: "youtrack", version: 1 }];

beforeAll(() => loadMarkdownDialect());

/** The value an editor saves after opening `content`, and the Markdown it writes for it. */
async function opened(content: Json[]): Promise<{ value: MarkdownRichValue; markdown: string }> {
  const controller = createMarkdownEditorController({ profile: "rich", editable: false, plugins: plugins(),
    source: { kind: "local", value: { format: "atlas-rich", version: 1, plugins: metadata, document: { type: "doc", content } } as unknown as MarkdownRichValue } });
  const element = document.body.appendChild(document.createElement("div"));
  controller.attachView(element, { ariaLabel: "Stránka" });
  await new Promise(done => setTimeout(done, 0));
  const result = { value: controller.getValue() as MarkdownRichValue, markdown: controller.getProjection().markdown };
  controller.destroy();
  element.remove();
  return result;
}
const contentOf = (value: MarkdownRichValue) => value.document.content as unknown as Json[];
const titleOf = (node: Json) => (node.content ?? []).map(child => child.text ?? "").join("");
const headings = (value: MarkdownRichValue) => contentOf(value).filter(node => node.type === "heading").map(node => [titleOf(node), node.attrs!.headingId ?? null, node.attrs!.state ?? null]);
const plainTitle = (node: Json) => !!node.content?.length && node.content.every(child => child.type === "text" && !("marks" in child)) && /^[\p{L}\p{N}][\p{L}\p{N} ]*[\p{L}\p{N}]$/u.test(titleOf(node)) && !/ {2}/.test(titleOf(node));

describe("the editor writes a heading line as the server does (requirement-heading-lines.json)", () => {
  it.each(lines.map((entry, index) => [`${index}: ${entry.markdown.replace(/[^ -~]/g, "·").slice(0, 60)}`, entry] as const))("%s", async (_name, entry) => {
    const heading = withoutNulls(entry.heading);
    const { value, markdown } = await opened([heading]);
    // A YouTrack chip in the title is written with the issue's title and address, which the fixture leaves out: the rest of the line is the same.
    if (JSON.stringify(heading).includes("youtrackIssue")) expect(markdown.replace(/\[(PX2-\d+): [^\]]*\]\([^)]*\)/g, "[$1: ]()")).toBe(entry.markdown);
    else expect(markdown).toBe(entry.markdown);

    // The block alone: written from the attributes, found where the line has it, read back to them.
    const block = writeHeadingBlock(heading.attrs ?? {});
    // (A title with a line break ends on a line that is no heading line: neither side reads a block there, and the source protects it.)
    if (!entry.markdown.includes("\n")) expect(headingBlockStart(entry.markdown).start).toBe(block ? entry.markdown.length - block.length : -1);
    if (block) {
      const read = readHeadingBlock(block.slice(1, -1));
      expect(read.error).toBeUndefined();
      for (const [name, text] of Object.entries(read.attrs)) expect(heading.attrs![name]).toBe(text);
    }

    // The page source: its line is the same, a no-op changes nothing, and an edited title keeps every attribute, the id read from the line.
    const session = await createMarkdownSourceSession({ value, plugins: plugins() });
    try {
      const noop = session.apply(session.text);
      expect(noop.ok && !noop.changed).toBe(true);
      const stored = contentOf(value)[0]!;
      if (session.blocks.size || !block || !plainTitle(stored) || !block.startsWith("{#")) return;
      expect(session.text).toBe(`${entry.markdown}\n`);
      const result = session.apply(session.text.replace(titleOf(stored), `${titleOf(stored)} upraveno`));
      if (!result.ok) throw new Error(JSON.stringify(result.diagnostics));
      const edited = contentOf(result.value)[0]!;
      expect(edited.attrs).toMatchObject(stored.attrs!);
      expect(titleOf(edited)).toBe(`${titleOf(stored)} upraveno`);
    } finally { session.destroy(); }
  });
});

describe("requirement headings in the page source", () => {
  const h = (text: string, attrs: Record<string, unknown>): Json => ({ type: "heading", attrs: { level: 2, ...attrs }, content: [{ type: "text", text }] });
  const p = (text: string): Json => ({ type: "paragraph", content: [{ type: "text", text }] });
  const page = () => [h("Objednávky", { headingId: "obj", state: "in_progress", party: "Eva", aliases: "stare-obj" }), p("Text objednávek."),
    h("Faktury", { headingId: "fak", state: "done" }), p("Text faktur."), h("Poznámky", { headingId: "pozn" }), p("Konec.")];
  const SOURCE = "## Objednávky {#obj state=in_progress party=Eva}\n\nText objednávek.\n\n## Faktury {#fak state=done}\n\nText faktur.\n\n## Poznámky\n\nKonec.\n";

  async function apply(edit: (text: string) => string): Promise<{ result: MarkdownSourceApplyResult; value: MarkdownRichValue }> {
    const { value } = await opened(page());
    const session = await createMarkdownSourceSession({ value, plugins: plugins() });
    try {
      expect([...session.blocks.values()]).toEqual([]);
      expect(session.text).toBe(SOURCE);
      return { result: session.apply(edit(session.text)), value };
    } finally { session.destroy(); }
  }
  const applied = async (edit: (text: string) => string) => {
    const { result, value } = await apply(edit);
    if (!result.ok) throw new Error(JSON.stringify(result.diagnostics));
    return { now: result.value, before: value };
  };
  const errors = async (edit: (text: string) => string) => {
    const { result } = await apply(edit);
    return result.ok ? [] : result.diagnostics.filter(item => item.severity === "error").map(item => [item.line, item.message]);
  };
  const byId = (value: MarkdownRichValue, id: string) => contentOf(value).find(node => node.attrs?.headingId === id);

  it("a no-op changes nothing", async () => {
    const { result } = await apply(text => text);
    expect(result.ok && !result.changed).toBe(true);
  });

  it("a moved section keeps its id, state, party and aliases, edited on the way or not", async () => {
    const { now, before } = await applied(text => {
      const [first, rest] = text.split("\n\n## Faktury");
      return `## Faktury${rest!.replace(/\n$/, "")}\n\n${first!.replace("## Objednávky", "### Objednávky 2026")}\n`;
    });
    expect(headings(now)).toEqual([["Faktury", "fak", "done"], ["Poznámky", "pozn", null], ["Objednávky 2026", "obj", "in_progress"]]);
    expect(byId(now, "obj")!.attrs).toMatchObject({ ...byId(before, "obj")!.attrs, level: 3 });
    expect(byId(now, "fak")).toEqual(byId(before, "fak"));
  });

  it("a deleted requirement takes nothing along: the retitled next heading keeps its own", async () => {
    const { now } = await applied(text => text.replace("## Objednávky {#obj state=in_progress party=Eva}\n\nText objednávek.\n\n", "").replace("## Faktury", "## Faktury 2026"));
    expect(headings(now)).toEqual([["Faktury 2026", "fak", "done"], ["Poznámky", "pozn", null]]);
    // And a plain heading in a deleted requirement's place is plain: it does not get the requirement's id.
    const plain = (await applied(text => text.replace("## Objednávky {#obj state=in_progress party=Eva}", "## Jiný oddíl"))).now;
    expect(headings(plain)[0]).toEqual(["Jiný oddíl", null, null]);
  });

  it("a heading typed above a retitled requirement is a plain heading", async () => {
    const { now } = await applied(text => text.replace("## Objednávky", "## Nový oddíl\n\n## Objednávky 2026"));
    expect(headings(now).slice(0, 2)).toEqual([["Nový oddíl", null, null], ["Objednávky 2026", "obj", "in_progress"]]);
    expect(byId(now, "obj")!.attrs!.aliases).toBe("stare-obj");
  });

  it("a changed state, kind and own state word are the heading's attributes, as the state menu writes them", async () => {
    const { now, before } = await applied(text => text.replace("{#fak state=done}", '{#fak state=waiting_for_input kind=inbound stateLabel="čeká na IT"}'));
    expect(byId(now, "fak")!.attrs).toMatchObject({ ...byId(before, "fak")!.attrs, state: "waiting_for_input", direction: "inbound", stateLabel: "čeká na IT" });
    // Named by its id alone a requirement stays the requirement it was (as the server's merge reads `{#id}`); a new one needs no id.
    const named = (await applied(text => text.replace("{#fak state=done}", "{#fak}").replace("## Faktury", "## Faktury 2026").replace("## Poznámky", "## Poznámky {state=planned}")));
    expect(headings(named.now)).toEqual([["Objednávky", "obj", "in_progress"], ["Faktury 2026", "fak", "done"], ["Poznámky", "pozn", "planned"]]);
    expect(byId(named.now, "fak")!.attrs).toMatchObject(byId(named.before, "fak")!.attrs!);
  });

  it("a changed state drops the own state word unless the word is written anew, as the state menu does", async () => {
    const labelled = [h("Faktury", { headingId: "fak", state: "waiting_for_input", stateLabel: "čeká na IT" }), p("Text.")];
    const run = async (edit: (text: string) => string) => {
      const { value } = await opened(labelled);
      const session = await createMarkdownSourceSession({ value, plugins: plugins() });
      try {
        expect(session.text).toBe('## Faktury {#fak state=waiting_for_input stateLabel="čeká na IT"}\n\nText.\n');
        const result = session.apply(edit(session.text));
        if (!result.ok) throw new Error(JSON.stringify(result.diagnostics));
        const attrs = contentOf(result.value)[0]!.attrs!;
        return [attrs.state, attrs.stateLabel];
      } finally { session.destroy(); }
    };
    expect(await run(text => text.replace("state=waiting_for_input", "state=done"))).toEqual(["done", ""]);
    expect(await run(text => text.replace('state=waiting_for_input stateLabel="čeká na IT"', "state=blocked stateLabel=reklamace"))).toEqual(["blocked", "reklamace"]);
    expect(await run(text => text.replace("## Faktury", "## Faktury 2026"))).toEqual(["waiting_for_input", "čeká na IT"]);
  });

  it("a requirement does not vanish with its block: the heading line left without it is an error, the whole line deleted is not", async () => {
    expect(await errors(text => text.replace(" {#fak state=done}", ""))).toEqual([[5, "Nadpis „fak“ by přišel o své ID a s ním o stav požadavku nebo dřívější názvy: vraťte {#fak …}, nebo smažte celý nadpis."]]);
    const { now } = await applied(text => text.replace("## Faktury {#fak state=done}\n\n", ""));
    expect(headings(now)).toEqual([["Objednávky", "obj", "in_progress"], ["Poznámky", "pozn", null]]);
  });

  it("knows a heading by the id its block holds, wherever in the block and whatever its values hold", async () => {
    const braces = [h("Smlouva", { headingId: "sml", state: "planned", party: "Firma {CZ}", stateLabel: "čeká {na} IT" }), p("Text."), h("Jiný", { headingId: "jiny" }), p("Konec.")];
    const run = async (edit: (text: string, line: string) => string) => {
      const { value } = await opened(braces);
      const session = await createMarkdownSourceSession({ value, plugins: plugins() });
      try {
        expect([...session.blocks.values()]).toEqual([]);
        const line = session.text.split("\n")[0]!;
        expect(line).toBe('## Smlouva {#sml state=planned party="Firma \\{CZ\\}" stateLabel="čeká \\{na\\} IT"}');
        const result = session.apply(edit(session.text, line));
        return result.ok ? headings(result.value) : result.diagnostics.map(item => [item.line, item.message.slice(0, 24)]);
      } finally { session.destroy(); }
    };
    // A pasted copy of the line is the same id twice.
    expect(await run((text, line) => `${text}\n${line}\n`)).toEqual([[9, "ID nadpisu „sml“ je v te"]]);
    // Replaced by a plain heading, its id does not move to that heading.
    expect(await run((text, line) => text.replace(line, "## Úplně jiný nadpis"))).toEqual([["Úplně jiný nadpis", null, null], ["Jiný", "jiny", null]]);
    // The id as a later token of the block is the id all the same.
    expect(await run(text => `${text}\n## Kopie {state=done #sml}\n`)).toEqual([[9, "ID nadpisu „sml“ je v te"]]);
    expect(await run(text => `${text}\n## Nový {state=done #vymysleny}\n`)).toEqual([[9, "Nadpis s ID „vymysleny“ "]]);
    // A line that only looks the same inside a code block is not where the error points.
    expect(await run((text, line) => `\`\`\`\n${line}\n\`\`\`\n\n${text}\n${line}\n`)).toEqual([[13, "ID nadpisu „sml“ je v te"]]);
  });

  it("a requirement whose id is not written (a temporary one) stays protected, not carried by its place", async () => {
    const { value } = await opened([h("Dočasný", { headingId: "v3-h2", state: "done" }), p("Text.")]);
    const session = await createMarkdownSourceSession({ value, plugins: plugins() });
    try {
      expect([...session.blocks.values()].map(block => [block.kind, block.text])).toEqual([["heading", "## Dočasný {state=done}"]]);
      const noop = session.apply(session.text);
      expect(noop.ok && !noop.changed).toBe(true);
    } finally { session.destroy(); }
  });

  // The block is the page source's reading: Markdown pasted or imported into the document, and the assistant's, is read as before.
  it("pasted into the document editor the block stays text", async () => {
    const controller = createMarkdownEditorController({ profile: "rich", editable: true, plugins: plugins(),
      source: { kind: "local", value: { format: "atlas-rich", version: 1, plugins: metadata, document: { type: "doc", content: [h("Faktury", { headingId: "fak", state: "done" }), { type: "paragraph" }] } } as unknown as MarkdownRichValue } });
    const element = document.body.appendChild(document.createElement("div"));
    controller.attachView(element, { ariaLabel: "Stránka" });
    await new Promise(done => setTimeout(done, 0));
    try {
      const editor = (element.querySelector(".ProseMirror") as unknown as { editor: { commands: { setTextSelection(at: number): boolean; pasteMarkdown(text: string): boolean }; state: { doc: { content: { size: number } } }; getJSON(): { content: Json[] } } }).editor;
      editor.commands.setTextSelection(editor.state.doc.content.size - 1);
      expect(editor.commands.pasteMarkdown('## Převzato {#fak state=blocked party="Cizí"}')).toBe(true);
      const found = editor.getJSON().content.filter(node => node.type === "heading").map(node => [titleOf(node), node.attrs?.state ?? null]);
      expect(found).toEqual([["Faktury", "done"], ['Převzato {#fak state=blocked party="Cizí"}', null]]);
    } finally { controller.destroy(); element.remove(); }
  });

  it("refuses an id twice, an id the page does not have and a value the form does not know, on the line", async () => {
    expect(await errors(text => `${text}\n## Kopie {#fak state=done}\n`)).toEqual([[13, "ID nadpisu „fak“ je v textu dvakrát. Každý nadpis má své ID; kopii pište bez {#…}."]]);
    expect(await errors(text => text.replace("## Poznámky", "## Poznámky {#obj}"))).toEqual([[9, "ID nadpisu „obj“ je v textu dvakrát. Každý nadpis má své ID; kopii pište bez {#…}."]]);
    expect(await errors(text => text.replace("{#fak ", "{#fakt "))).toEqual([[5, "Nadpis s ID „fakt“ na stránce není. Nový nadpis pište bez {#…}; ID existujícího nadpisu neměňte."]]);
    expect(await errors(text => text.replace("state=done", "state=hotovo"))).toEqual([[5, "Stav požadavku je planned, in_progress, waiting_for_input, done, blocked nebo not_applicable."]]);
    expect(await errors(text => text.replace("state=done", "state=done kind=nikdo"))).toEqual([[5, "Druh požadavku (kind) je internal, inbound nebo outbound."]]);
    expect(await errors(text => text.replace("state=done", "state=done owner=x"))).toEqual([[5, "Neznámý atribut nadpisu owner (známé jsou #id, state, kind, party, partyContact, system, from a stateLabel)."]]);
  });

  it("a heading with aliases and no state names itself with {#id} and keeps them", async () => {
    const { value } = await opened([h("Starý název", { headingId: "novy", aliases: "stary" }), p("Text.")]);
    const session = await createMarkdownSourceSession({ value, plugins: plugins() });
    try {
      expect([...session.blocks.values()]).toEqual([]);
      expect(session.text).toBe("## Starý název {#novy}\n\nText.\n");
      const result = session.apply("Text.\n\n# Nový název {#novy}\n");
      if (!result.ok) throw new Error(JSON.stringify(result.diagnostics));
      expect(contentOf(result.value)[1]!.attrs).toMatchObject({ level: 1, headingId: "novy", aliases: "stary", state: null });
    } finally { session.destroy(); }
  });
});
