import { readdirSync, readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { beforeAll, describe, expect, it } from "vitest";
import { emojis } from "@tiptap/extension-emoji";
import { createMarkdownEditorController, loadMarkdownDialect, type MarkdownRichValue } from "@atlas/markdown/core";
import { createAttachmentsPlugin } from "@atlas/markdown/plugins/attachments";
import { createHeadingAnchorsPlugin } from "@atlas/markdown/plugins/heading-anchors";
import { createYouTrackPlugin } from "@atlas/markdown/plugins/youtrack";
import { createMarkdownSourceSession } from "@atlas/markdown/source";
import { captureDocument, readDocument } from "./document";
import { createDocumentWidgets } from "./documentWidgets";

// The server writes these files, editor JSON plus the Markdown projection EditorMarkdownWriter stores for it:
// - wiki-import: every page of the fixture wikis (tests/Atlas.Server.Tests/ContentImport/WikiImportGoldenTests.cs);
// - content: documents holding every text node and mark and every Atlas component
//   (tests/Atlas.Server.Tests/Content/EditorMarkdownWriterParityTests.cs), the parity of the writer the MCP projection and
//   the API share with the editor pinned in 2.0, and the documents the MCP merge writes back (Content/RichMarkdownMergeTests.cs).
// Opening them in the Atlas editor catches schema or serializer drift when the editor is upgraded.
const fixtures = resolve(dirname(fileURLToPath(import.meta.url)), "../../../tests/fixtures");
// Contacts and charts (`a-` components) need the editor's on-demand dialect; these controllers have no editor host.
beforeAll(() => loadMarkdownDialect());
const goldenSets = ["wiki-import", "content"].map((set) => {
  const directory = resolve(fixtures, set, "expected");
  return { set, directory, files: readdirSync(directory).filter((file) => file.endsWith(".json")).sort() };
});

interface GoldenPage { path?: string; name?: string; content: MarkdownRichValue; markdown: string }

// The node types a stored page may contain, as registered by the editor and the history view.
const plugins = () => [...createDocumentWidgets({ kind: "history" }), createAttachmentsPlugin(), createHeadingAnchorsPlugin(),
  createYouTrackPlugin({ client: { search: async () => [] } })];

function open(value: MarkdownRichValue) {
  const controller = createMarkdownEditorController({ profile: "rich", source: { kind: "local", value }, plugins: plugins() });
  controller.attachView(document.createElement("div"), { ariaLabel: "Importovaná stránka" });
  return controller;
}

describe("server golden files", () => {
  it("cover the GitHub, Obsidian and Wiki.js fixture wikis and the text-node documents", () => {
    expect(goldenSets[0]!.files).toEqual(expect.arrayContaining(["github.json", "obsidian.json", "wikijs.json"]));
    expect(goldenSets[1]!.files).toEqual(expect.arrayContaining(["components.json", "mark-order.json", "merge.json", "text-nodes.json"]));
  });

  describe.each(goldenSets.flatMap(({ set, directory, files }) => files.map((file) => [`${set}/${file}`, resolve(directory, file)] as const)))("%s", (_name, path) => {
    const pages = JSON.parse(readFileSync(path, "utf8")) as GoldenPage[];
    it.each(pages.map((page) => [page.path ?? page.name ?? "", page] as const))("%s opens unchanged and projects to the stored Markdown", (_path, page) => {
      const controller = open(readDocument(JSON.stringify(page.content)));
      try {
        const saved = captureDocument(controller);
        const value = readDocument(saved.contentJson);
        // Nothing the server wrote is dropped, reordered or changed; the editor may only add attribute defaults (its document; the
        // saved value leaves the defaults out again, storedDocument.ts).
        expect(readDocument(JSON.stringify(controller.getProjection().rich)).document).toMatchObject(page.content.document as Record<string, unknown>);
        // Every plugin the server declares is registered at the same schema version.
        for (const plugin of page.content.plugins ?? []) expect(value.plugins).toContainEqual(plugin);
        // The stored projection is exactly what the editor writes for the same document.
        expect(page.markdown).toBe(saved.markdown);
        // Saving the reopened page again changes nothing.
        const reopened = open(value);
        try { expect(captureDocument(reopened).contentJson).toBe(saved.contentJson); }
        finally { reopened.destroy(); }
      } finally {
        controller.destroy();
      }
    });
  });
});

/** The cells of a table a chart links to get new ids on every read (dialect N4): both readers' ids numbered in document order. */
function numberChartIds(value: unknown): unknown {
  const ids = new Map<string, string>();
  const visit = (node: unknown): unknown => {
    if (typeof node === "string" && node.startsWith("chart-")) {
      if (!ids.has(node)) ids.set(node, `chart-${ids.size + 1}`);
      return ids.get(node);
    }
    if (Array.isArray(node)) return node.map(visit);
    if (node && typeof node === "object") return Object.fromEntries(Object.entries(node).map(([key, item]) => [key, visit(item)]));
    return node;
  };
  return visit(value);
}

// A merge golden with "source" was read from that Markdown by the server (MarkdownToRich): the editor's own reader, the page
// source session, must read the same document from it.
describe("server Markdown reader", () => {
  const pages = (JSON.parse(readFileSync(resolve(fixtures, "content", "expected", "merge.json"), "utf8")) as (GoldenPage & { source?: string })[])
    .filter((page) => page.source !== undefined);

  it.each(pages.map((page) => [page.name ?? "", page] as const))("%s reads as the editor reads it", async (_name, page) => {
    const session = await createMarkdownSourceSession({
      value: { format: "atlas-rich", version: 1, document: { type: "doc", content: [{ type: "paragraph" }] } }, plugins: plugins(),
    });
    try {
      const result = session.apply(page.source!);
      if (!result.ok) throw new Error(JSON.stringify(result.diagnostics));
      expect(numberChartIds(result.value.document)).toMatchObject(numberChartIds(page.content.document) as Record<string, unknown>);
    } finally {
      session.destroy();
    }
  });
});

describe("server emoji table", () => {
  // EditorComponentText writes an emoji node as the editor's emoji plugin does: the first emoji whose name or shortcode
  // matches. The table is generated from the installed editor's list; regenerate it when this fails after an upgrade.
  it("matches the shortcodes of the installed editor", () => {
    const table: Record<string, string> = {};
    for (const item of emojis.filter((emoji) => !!emoji.emoji)) {
      for (const key of [item.name, ...item.shortcodes]) if (!(key in table)) table[key] = item.emoji!;
    }
    const server = JSON.parse(readFileSync(resolve(fixtures, "../../src/Atlas.Server/Platform/Content/emoji-shortcodes.json"), "utf8")) as Record<string, string>;
    expect(server).toEqual(table);
  });
});
