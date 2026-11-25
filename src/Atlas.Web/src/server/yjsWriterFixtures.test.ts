import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { createRequire } from "node:module";
import { dirname, resolve } from "node:path";
import { gunzipSync, gzipSync } from "node:zlib";
import { afterAll, beforeAll, describe, expect, it, vi } from "vitest";
import * as Y from "yjs";
import { processLiveWrite } from "./liveWrite";

// Parity fixtures of Atlas.Yjs's managed writer (tests/Atlas.Yjs.Tests/Fixtures/yjs-writer/cases.json.gz):the live-write worker (processLiveWrite with
// Atlas's plugins) seeds stored pages (seedRich), writes targets into live documents and into documents a "web" client edits at the
// same time, with fixed client ids. The default run checks that the installed packages are the ones the fixtures were recorded with
// and rebuilds a sample of the cases (every seed, the writes of a few bases) against them. When either fails (a Yjs, y-tiptap,
// ProseMirror, plugin or schema change), regenerate them, then run Atlas.Yjs.Tests and BrowserSeedParityTests and port what changed:
//   ATLAS_EDITOR_MODE=source ATLAS_UPDATE_YJS_WRITER=1 pnpm exec vitest run src/server/yjsWriterFixtures.test.ts
const run = process.env.ATLAS_UPDATE_YJS_WRITER === "1";
const repo = resolve(__dirname, "../../../..");
const out = resolve(repo, "tests/Atlas.Yjs.Tests/Fixtures/yjs-writer");
const syncFixtures = resolve(repo, "tests/Atlas.Markdown.Sync.Tests/Fixtures");
const manifest = JSON.parse(readFileSync(resolve(repo, "src/Atlas.Server/Platform/Validation/host-markdown-schema.json"), "utf8"));
const schema = { version: manifest.schemaRevision, profile: "rich" as const, plugins: manifest.plugins.map(({ id, version }: { id: string; version: number }) => ({ id, version })) };
const SEED = 4242, WRITER = 777_777, WEB = 31_337;
// The bases whose writes and concurrent edits the default run rebuilds: plugin rules, tables, emoji and a live multi-client document.
const SAMPLE = new Set(["requirement-rules", "table-ragged", "autolink-renamed-last-heading", "math-table-tasks", "emoji-surrogates", "editor-concurrent"]);

// Every Y.Doc gets the next fixed client id instead of a random one (Doc's constructor assigns this.clientID), and every random UUID
// (a repeated heading's new id) is the next of a sequence restarted per request, so the same input always records the same case.
// Both only while this file runs: yjs is one module instance for every test file of the worker.
let nextClient = SEED, nextUuid = 0;
beforeAll(() => {
  Object.defineProperty(Y.Doc.prototype, "clientID", {
    configurable: true,
    set(this: Y.Doc, _random: number) { Object.defineProperty(this, "clientID", { value: nextClient, writable: true, enumerable: true, configurable: true }); },
  });
  vi.spyOn(globalThis.crypto, "randomUUID").mockImplementation(() => `00000000-0000-4000-8000-${String(++nextUuid).padStart(12, "0")}`);
});
afterAll(() => {
  delete (Y.Doc.prototype as { clientID?: number }).clientID;
  vi.restoreAllMocks();
});

type Json = Record<string, any>;
const b64 = (bytes: Uint8Array) => Buffer.from(bytes).toString("base64");
const unb64 = (text: string) => new Uint8Array(Buffer.from(text, "base64"));
const clone = <T>(value: T): T => JSON.parse(JSON.stringify(value));

function live(request: Parameters<typeof processLiveWrite>[0]) {
  nextUuid = 0;
  return processLiveWrite(request);
}

function reduce(payloads: string[]) {
  return live({ schema, fragment: "default", snapshot: payloads[0], updates: payloads.slice(1) });
}

function write(payloads: string[], target: unknown) {
  nextClient = WRITER;
  return live({ schema, fragment: "default", snapshot: payloads[0], updates: payloads.slice(1), target });
}

/** An installed package (the copy `from` resolves): its version and a file of it to resolve its own dependencies from. */
function installed(name: string, from: string, entry = name) {
  let dir = dirname(createRequire(from).resolve(entry));
  while (!existsSync(resolve(dir, "package.json")) || JSON.parse(readFileSync(resolve(dir, "package.json"), "utf8")).name !== name) dir = dirname(dir);
  return { version: JSON.parse(readFileSync(resolve(dir, "package.json"), "utf8")).version as string, file: resolve(dir, "package.json") };
}

/** The packages Atlas.Yjs ports (src/Atlas.Yjs/README.md), as installed here. */
function installedVersions() {
  const here = resolve(__dirname, "liveWrite.ts");
  const yjs = installed("yjs", here), yTiptap = installed("@tiptap/y-tiptap", here), pm = installed("@tiptap/pm", here, "@tiptap/pm/tables");
  return {
    yjs: yjs.version, lib0: installed("lib0", yjs.file).version, "@tiptap/y-tiptap": yTiptap.version,
    "prosemirror-model": installed("prosemirror-model", yTiptap.file).version, "prosemirror-tables": installed("prosemirror-tables", pm.file).version,
  };
}

const committed = () => JSON.parse(gunzipSync(readFileSync(resolve(out, "cases.json.gz"))).toString("utf8")) as { versions: Json; skipped: string[]; cases: Json[] };

/** Stored pages: the content fixtures (rich documents) and hand-made ones for what they miss. */
function storedPages(): Array<{ name: string; document: Json }> {
  const pages: Array<{ name: string; document: Json }> = [];
  const content = resolve(repo, "tests/fixtures/content");
  for (const file of ["components.json", "mark-order.json", "text-nodes.json", "requirements.json", "requirement-headings.json"]) {
    const parsed = JSON.parse(readFileSync(resolve(content, file), "utf8"));
    const cases = (Array.isArray(parsed) ? parsed : parsed.cases) as Json[];
    cases.forEach((entry, index) => {
      const document = Array.isArray(entry.content) ? { type: "doc", content: entry.content } : entry.content?.document;
      if (document) pages.push({ name: `${file.replace(".json", "")}-${index}`, document });
    });
  }
  const board = JSON.parse(readFileSync(resolve(content, "deployment-board.json"), "utf8"));
  pages.push({ name: "deployment-board", document: board.document });
  if (board.headingDocument) pages.push({ name: "deployment-board-headings", document: board.headingDocument });
  const text = (value: string, marks?: Json[]) => ({ type: "text", text: value, ...(marks && { marks }) });
  const paragraph = (...content: Json[]) => ({ type: "paragraph", content });
  pages.push(
    { name: "empty", document: { type: "doc", content: [{ type: "paragraph" }] } },
    { name: "emoji-surrogates", document: { type: "doc", content: [paragraph(text("Ahoj 👋🏽 světe 🇨🇿 "), { type: "emoji", attrs: { name: "smile" } }, text(" konec 𝒜"))] } },
    { name: "overlapping-links", document: { type: "doc", content: [paragraph(text("Dvojí odkaz", [{ type: "link", attrs: { href: "https://example.com/a" } }, { type: "link", attrs: { href: "https://example.com/b" } }]), text(" a "), text("tučný kód", [{ type: "code" }, { type: "bold" }]))] } },
    { name: "math-table-tasks", document: { type: "doc", content: [
      { type: "heading", attrs: { level: 2 }, content: [text("Výpočty")] },
      { type: "mathBlock", attrs: { data: { version: 1, expression: "a^2 + b^2 = c^2" } } },
      paragraph(text("Inline "), { type: "mathInline", attrs: { data: { version: 1, expression: "x_1" } } }, text(" vzorec.")),
      { type: "table", attrs: { chartRef: "chart-1" }, content: [
        { type: "tableRow", content: [{ type: "tableHeader", attrs: { colwidth: [120] }, content: [paragraph(text("A"))] }, { type: "tableHeader", content: [paragraph(text("B"))] }] },
        { type: "tableRow", content: [{ type: "tableCell", content: [paragraph(text("1"))] }, { type: "tableCell", attrs: { colspan: 1 },content: [paragraph(text("2"))] }] },
      ] },
      { type: "taskList", content: [
        { type: "taskItem", attrs: { checked: true }, content: [paragraph(text("hotovo"))] },
        { type: "taskItem", attrs: { checked: false }, content: [paragraph(text("úkol"))] },
      ] },
    ] } },
    { name: "secret", document: { type: "doc", content: [
      { type: "secretReference", attrs: { secretId: "0b7c2c1e-1111-4111-8111-111111111111", field: "password", showCopy: false } },
      paragraph(text("Heslo.")),
    ] } },
    { name: "overview", document: { type: "doc", content: [
      { type: "overview", attrs: { id: "22222222-2222-4222-8222-222222222222", kind: "requirements" } },
      paragraph(text("Po přehledu.")),
    ] } },
  );
  // What the browser seed's plugins append (BrowserSeed.cs, tests/Atlas.Server.Tests/Collaboration/BrowserSeedParityTests.cs): the
  // table repair (prosemirror-tables fixTables), Link's autolink of the first text block's last word, the chart references and the
  // requirement rules.
  const cell = (type: string, value: string, attrs?: Json) => ({ type, ...(attrs && { attrs }), content: [paragraph(text(value))] });
  const row = (...cells: Json[]) => ({ type: "tableRow", content: cells });
  const table = (...rows: Json[]) => ({ type: "table", content: rows });
  const link = (href: string) => [{ type: "link", attrs: { href } }];
  const reference = (title: string, heading: string) => ({ type: "pageReference", attrs: { id: "5e940f4d-55c2-4527-addd-10f1d68d742c", title,
    url: `#/pages/5e940f4d-55c2-4527-addd-10f1d68d742c?heading=${heading}` } });
  const requirement = (id: string, title: string) => ({ type: "heading", attrs: { level: 2, headingId: id, state: "planned" }, content: [text(title)] });
  const dependencies = (...content: Json[]) => ({ type: "paragraph", attrs: { dependencies: true }, ...(content.length && { content }) });
  pages.push(
    { name: "table-ragged", document: { type: "doc", content: [paragraph(text("Před tabulkou.")), table(
      row(cell("tableHeader", "A"), cell("tableHeader", "B"), cell("tableHeader", "C")),
      row(cell("tableCell", "1"), cell("tableCell", "2")),
      row(cell("tableCell", "3")),
      row(),
    ), paragraph(text("Po tabulce."))] } },
    { name: "table-colwidth-one-row", document: { type: "doc", content: [table(
      row(cell("tableHeader", "A", { colwidth: [120] }), cell("tableHeader", "B", { colwidth: [80] })),
      row(cell("tableCell", "1"), cell("tableCell", "2")),
      row(cell("tableCell", "3", { colwidth: [150] }), cell("tableCell", "4")),
    ), table(
      row(cell("tableHeader", "A"), cell("tableHeader", "B")),
      row(cell("tableCell", "1", { colwidth: [90] }), cell("tableCell", "2")),
    )] } },
    { name: "table-merged-collision", document: { type: "doc", content: [table(
      row(cell("tableHeader", "A", { rowspan: 2 }), cell("tableHeader", "B"), cell("tableHeader", "C", { rowspan: 3 })),
      row(cell("tableCell", "1", { colspan: 2, colwidth: [100, 50] }), cell("tableCell", "2")),
    ), paragraph(text("Mezi.")), table(
      row(cell("tableHeader", "A", { colspan: 3 })),
      row(cell("tableCell", "1"), cell("tableCell", "2", { rowspan: 2, colspan: 2 })),
      row(cell("tableCell", "3"), cell("tableCell", "4"), cell("tableCell", "5")),
    )] } },
    { name: "autolink-url", document: { type: "doc", content: [paragraph(text("Viz https://example.com/navod?x=1")), paragraph(text("Další https://example.org"))] } },
    { name: "autolink-domain", document: { type: "doc", content: [{ type: "heading", attrs: { level: 2 }, content: [text("Portál klienta (portal.example.com)")] },
      paragraph(text("Text."))] } },
    { name: "autolink-heading-url", document: { type: "doc", content: [{ type: "heading", attrs: { level: 1 }, content: [text("Příručka (https://portal.example.test/)")] },
      paragraph(text("Text."))] } },
    { name: "autolink-already-linked", document: { type: "doc", content: [paragraph(text("Odkaz "), text("https://example.com", link("https://example.com"))), paragraph(text("Text."))] } },
    { name: "autolink-in-code", document: { type: "doc", content: [paragraph(text("Kód "), text("https://example.com", [{ type: "code" }])), paragraph(text("Text."))] } },
    { name: "autolink-in-code-block", document: { type: "doc", content: [{ type: "codeBlock", attrs: { language: null }, content: [text("curl https://example.com")] }, paragraph(text("Text."))] } },
    { name: "autolink-email", document: { type: "doc", content: [paragraph(text("Pište na info@example.cz")), paragraph(text("Text."))] } },
    { name: "autolink-trailing-dot", document: { type: "doc", content: [paragraph(text("Viz example.cz.")), paragraph(text("Text."))] } },
    { name: "autolink-mid-text", document: { type: "doc", content: [paragraph(text("Na www.example.cz je návod")), paragraph(text("Text."))] } },
    { name: "autolink-in-table", document: { type: "doc", content: [table(row(cell("tableHeader", "Web example.cz"), cell("tableHeader", "B"))), paragraph(text("Text."))] } },
    { name: "autolink-single-block", document: { type: "doc", content: [paragraph(text("Jen odkaz https://example.com"))] } },
    { name: "autolink-single-block-rule", document: { type: "doc", content: [paragraph(text("Jen odkaz example.cz")), { type: "horizontalRule" }] } },
    // The next round: a heading at the end given a fresh id, then the trailing paragraph (the range of its end holds both).
    { name: "autolink-renamed-last-heading", document: { type: "doc", content: [{ type: "heading", attrs: { level: 2, headingId: "dup" }, content: [text("Úvod")] },
      paragraph(text("Text.")), { type: "heading", attrs: { level: 2, headingId: "dup" }, content: [text("Viz (example.cz)")] }] } },
    { name: "autolink-marked-word", document: { type: "doc", content: [paragraph(text("Viz exa"), text("mple.cz", [{ type: "bold" }])), paragraph(text("Text."))] } },
    { name: "requirement-rules", document: { type: "doc", content: [
      requirement("r-empty", "Prázdný řádek závislostí"), dependencies(text(", ")),
      requirement("r-text", "Text bez odkazů"), dependencies(text("jen text")),
      requirement("r-mixed", "Odkazy a text"), dependencies(reference("Jiný", "a"), text(", a k tomu text, ")), paragraph(text("do 15. 11. 2026")),
      requirement("r-links", "Jen odkazy"), dependencies(reference("Jiný", "a"), text(", "), reference("Další", "b")),
      { type: "heading", attrs: { level: 2, headingId: "r-links" }, content: [text("Kopie nadpisu")] },
      { type: "blockquote", content: [requirement("r-nested", "Vnořený požadavek"), dependencies(text(", "), reference("Jiný", "a"))] },
      paragraph(text("Konec.")),
    ] } },
    { name: "chart-linked", document: { type: "doc", content: [
      { type: "table", attrs: { chartRef: "chart-src" }, content: [
        row(cell("tableHeader", "Měsíc", { chartRef: "c-a1" }), cell("tableHeader", "Kč", { chartRef: "c-b1" })),
        row(cell("tableCell", "Leden", { chartRef: "c-a2" }), cell("tableCell", "12", { chartRef: "c-b2" })),
      ] },
      { type: "chart", attrs: { data: { version: 1, title: "Tržby", kind: "bar", grid: [["Měsíc", "Kč"], ["Leden", "1"]],
        source: { tableId: "chart-src", cells: [["c-a1", "c-b1"], ["c-a2", "c-b2"]] }, sourceStatus: "linked" } } },
      { type: "chart", attrs: { data: { version: 1, title: "Bez tabulky", kind: "line", grid: [["A", "B"], ["x", "2"]],
        source: { tableId: "chart-gone", cells: [["g-1", "g-2"], ["g-3", "g-4"]] }, sourceStatus: "linked" } } },
      paragraph(text("Konec.")),
    ] } },
  );
  // A table without its chart reference gets a random one from the charts plugin on every seed (as seedParity.test.ts notes).
  let chart = 0;
  for (const page of pages) JSON.stringify(page.document, (key, value) => {
    if (value && typeof value === "object" && !Array.isArray(value) && value.type === "table") value.attrs = { ...value.attrs, chartRef: value.attrs?.chartRef ?? `chart-${++chart}` };
    return value;
  });
  return pages;
}

/** Edits a writer makes: each a function of the current document, or null where it does not apply. */
const edits: Array<[string, (doc: Json) => Json | null]> = [
  ["append-paragraph", doc => ({ ...doc, content: [...doc.content, { type: "paragraph", content: [{ type: "text", text: "Nový odstavec ✓" }] }] })],
  ["type-in-first-text", doc => mapFirstText(doc, node => ({ ...node, text: node.text.slice(0, 2) + "XY" + node.text.slice(3) }))],
  ["bold-in-first-text", doc => mapFirstText(doc, node => node.text.length < 4 ? null : [
    { ...node, text: node.text.slice(0, 1) },
    { ...node, text: node.text.slice(1, 3), marks: [...(node.marks ?? []), { type: "bold" }] },
    { ...node, text: node.text.slice(3) },
  ])],
  ["unmark-all", doc => {
    const copy = clone(doc);
    let changed = false;
    JSON.stringify(copy, (_key, value) => { if (value?.type === "text" && value.marks) { delete value.marks; changed = true; } return value; });
    return changed ? copy : null;
  }],
  ["remove-second-block", doc => doc.content.length < 3 ? null : { ...doc, content: doc.content.filter((_: unknown, index: number) => index !== 1) }],
  ["swap-first-blocks", doc => doc.content.length < 2 ? null : { ...doc, content: [doc.content[1], doc.content[0], ...doc.content.slice(2)] }],
  ["change-attributes", doc => {
    const copy = clone(doc);
    let changed = false;
    JSON.stringify(copy, (_key, value) => {
      if (value?.type === "heading") { value.attrs = { ...value.attrs, level: value.attrs?.level === 3 ? 2 : 3 }; changed = true; }
      if (value?.type === "requirement") { value.attrs = { ...value.attrs, state: value.attrs?.state === "done" ? "planned" : "done" }; changed = true; }
      if (value?.type === "taskItem") { value.attrs = { ...value.attrs, checked: !value.attrs?.checked }; changed = true; }
      return value;
    });
    return changed ? copy : null;
  }],
  ["clear-first-paragraph", doc => {
    const index = doc.content.findIndex((block: Json) => block.type === "paragraph" && block.content?.length);
    return index < 0 ? null : { ...doc, content: doc.content.map((block: Json, i: number) => i === index ? { type: "paragraph" } : block) };
  }],
  ["emoji-edit", doc => mapFirstText(doc, node => ({ ...node, text: "🙂" + node.text.replace(/[\uD800-\uDFFF]{2}/u, "👍🏻") + " 𝒜" }))],
  ["rewrite-middle", doc => doc.content.length < 3 ? null : { ...doc, content: [doc.content[0], { type: "heading", attrs: { level: 2 }, content: [{ type: "text", text: "Přepsáno" }] }, { type: "bulletList", content: [{ type: "listItem", content: [{ type: "paragraph", content: [{ type: "text", text: "bod", marks: [{ type: "italic" }] }] }] }] }, doc.content.at(-1)] }],
];

function mapFirstText(doc: Json, change: (node: Json) => Json | Json[] | null): Json | null {
  const copy = clone(doc);
  let done = false;
  const walk = (node: Json) => {
    if (done || !Array.isArray(node.content)) return;
    const index = node.content.findIndex((child: Json) => child.type === "text" && child.text.length > 0);
    if (index >= 0) {
      const changed = change(node.content[index]);
      if (changed === null) return;
      node.content.splice(index, 1, ...(Array.isArray(changed) ? changed : [changed]));
      done = true;
      return;
    }
    node.content.forEach(walk);
  };
  walk(copy);
  return done ? copy : null;
}

/** A web client's typing on the base: text inserted into the XmlText of the `block`-th top-level block (its first text, depth-first). */
function webTyping(payloads: string[], block: number, typed: string): string | null {
  nextClient = WEB;
  const doc = new Y.Doc();
  for (const payload of payloads) Y.applyUpdate(doc, unb64(payload));
  const before = Y.encodeStateVector(doc);
  const top = doc.getXmlFragment("default").toArray()[block];
  const findText = (type: unknown): Y.XmlText | null => {
    if (type instanceof Y.XmlText) return type;
    if (type instanceof Y.XmlElement) for (const child of type.toArray()) { const found = findText(child); if (found) return found; }
    return null;
  };
  const text = findText(top);
  if (!text) return null;
  text.insert(Math.min(1, text.length), typed);
  return b64(Y.encodeStateAsUpdate(doc, before));
}

/** A case or skip of the default run's sample: every seed, and the writes and concurrent edits of the SAMPLE bases. */
const sampled = (name: string) => name.startsWith("seed-")
  || [...SAMPLE].some(base => name.startsWith(`write-${base}-`) || name.startsWith(`concurrent-${base}-`) || name.startsWith(`${base}: `));

interface Base { name: string; payloads: string[]; files?: string[] }
interface Built { cases: Json[]; skipped: string[] }

/** The seed cases of every stored page, and the bases to write into: the seeded pages and the live documents of the corpus. */
function seedCases({ cases, skipped }: Built, sample: boolean) {
  const bases: Base[] = [];

  for (const page of storedPages()) {
    try {
      nextClient = SEED;
      const seeded = live({ schema, fragment: "default", seedRich: clone(page.document) });
      // The seed of the normalized document: the same state unless the stored one had attributes set to null, which the fragment
      // cannot hold (y-prosemirror skips them), so that they read back as their defaults.
      nextClient = SEED;
      const again = live({ schema, fragment: "default", seedRich: clone(seeded.editorJson) });
      expect(again.editorJson).toEqual(seeded.editorJson);
      cases.push({ kind: "seed", name: `seed-${page.name}`, clientId: SEED, input: page.document, normalized: seeded.editorJson, state: seeded.snapshot,
        normalizedState: again.snapshot });
      bases.push({ name: page.name, payloads: [seeded.snapshot] });
    } catch (error) {
      skipped.push(`seed-${page.name}: ${(error as Error).message}`);
    }
  }

  // Live documents of the materializer corpus: states and updates real editors wrote (several clients, deletions, gc).
  const corpus = JSON.parse(readFileSync(resolve(syncFixtures, "yjs-materializer/cases.json"), "utf8")).cases as Json[];
  for (const name of ["fixture-web-document", "fixture-secrets-v4", "editor-unicode", "editor-marks", "editor-empty-blocks", "editor-session", "editor-concurrent",
    "gc-concurrent-delete", "gc-everything-deleted", "raw-surrogate-split", "corpus-node-table", "corpus-node-taskList", "corpus-node-chart", "corpus-node-mathBlock",
    "corpus-node-contactCard", "corpus-node-secretReference", "corpus-node-emoji", "corpus-node-heading", "corpus-perf-1m"]) {
    if (sample && !SAMPLE.has(name)) continue;
    const entry = corpus.find(candidate => candidate.name === name)!;
    const steps = name === "corpus-perf-1m" ? entry.steps.slice(0, 1) : entry.steps;
    const files = steps.map((step: Json) => `yjs-materializer/${step.payload}`.replace(/yjs-materializer\/\.\.\//, ""));
    const payloads = files.map((file: string) => {
      const bytes = readFileSync(resolve(syncFixtures, file));
      return b64(file.endsWith(".gz") ? gunzipSync(bytes) : bytes);
    });
    bases.push({ name, payloads, files });
  }
  return bases;
}

/** The write and concurrent cases of one base. */
function writeCases({ cases, skipped }: Built, base: Base) {
  let current: Json;
  try { current = reduce(base.payloads).editorJson as Json; } catch (error) { skipped.push(`${base.name}: ${(error as Error).message}`); return; }
  const stored = base.files ?? base.payloads;
  const perf = base.name === "corpus-perf-1m";
  for (const [edit, make] of perf ? edits.filter(([name]) => name === "type-in-first-text" || name === "append-paragraph") : edits) {
    const target = make(current);
    if (!target) continue;
    try {
      const written = write(base.payloads, target);
      cases.push({ kind: "write", name: `write-${base.name}-${edit}`, clientId: WRITER, base: stored, target, update: written.update, result: written.editorJson });
    } catch (error) {
      skipped.push(`write-${base.name}-${edit}: ${(error as Error).message}`);
    }
  }
  if (perf) return;
  // Concurrency: the web client types into a block the target keeps (the last) and into the one the target changes (the first text).
  for (const [edit, block] of [["append-paragraph", 0], ["type-in-first-text", 0], ["remove-second-block", 1], ["swap-first-blocks", 0]] as const) {
    const target = edits.find(([name]) => name === edit)![1](current);
    if (!target) continue;
    const web = webTyping(base.payloads, block, "web✍");
    if (!web) continue;
    try {
      const written = write(base.payloads, target);
      const oneWay = reduce([...base.payloads, web, written.update!]).editorJson;
      const otherWay = reduce([...base.payloads, written.update!, web]).editorJson;
      expect(otherWay).toEqual(oneWay);
      cases.push({ kind: "concurrent", name: `concurrent-${base.name}-${edit}-block${block}`, clientId: WRITER, base: stored, web, target, update: written.update,
        result: oneWay });
    } catch (error) {
      skipped.push(`concurrent-${base.name}-${edit}: ${(error as Error).message}`);
    }
  }
}

describe("Atlas.Yjs writer fixtures", () => {
  it.skipIf(run)("were recorded with the installed Yjs, y-tiptap and ProseMirror (else regenerate them)", () => {
    expect(installedVersions()).toEqual(committed().versions);
  });

  // A plugin's appended transaction, the schema or the binding changing what the browser seeds or writes fails here, not in production.
  // The sample is some 300 live writes, two seconds of work on an idle machine: as one test it ran out of the 5 s of a test on a
  // loaded runner (CI run 153). The seeds and each base are a test of their own, in this order, and the last one checks the whole.
  describe.skipIf(run)("are what the editor's seed and live write make of a sample of their cases now (else regenerate them)", () => {
    const built: Built = { cases: [], skipped: [] };
    let fixtures: ReturnType<typeof committed>, recorded: Map<string, Json>, bases: Base[] = [];
    const same = (from: number) => { for (const entry of built.cases.slice(from)) expect(entry, entry.name).toEqual(recorded.get(entry.name)); };
    beforeAll(() => { fixtures = committed(); recorded = new Map(fixtures.cases.map(entry => [entry.name, entry])); });

    it("the seeds", () => { bases = seedCases(built, true); same(0); });
    it.each([...SAMPLE])("the writes into %s", name => { const from = built.cases.length; writeCases(built, bases.find(base => base.name === name)!); same(from); });
    it("all of the sample, and nothing skipped that was not", () => {
      expect(built.cases.map(entry => entry.name).sort()).toEqual(fixtures.cases.map(entry => entry.name).filter(sampled).sort());
      expect([...built.skipped].sort()).toEqual(fixtures.skipped.filter(sampled).sort());
    });
  });

  it.runIf(run)("writes the seed, live-write and concurrent cases", () => {
    mkdirSync(out, { recursive: true });
    const built: Built = { cases: [], skipped: [] }, { cases, skipped } = built;
    for (const base of seedCases(built, false)) writeCases(built, base);
    writeFileSync(resolve(out, "cases.json.gz"), gzipSync(JSON.stringify({
      versions: installedVersions(), generator: "src/Atlas.Web/src/server/yjsWriterFixtures.test.ts", skipped, cases,
    }), { level: 9 }));
    expect(existsSync(resolve(out, "cases.json.gz"))).toBe(true);
    console.log(`${cases.length} cases (${cases.filter(c => c.kind === "seed").length} seeds), skipped ${skipped.length}:\n${skipped.join("\n")}`);
  }, 600_000);
});
