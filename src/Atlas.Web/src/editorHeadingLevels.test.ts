import { describe, expect, it } from "vitest";
import { createMarkdownEditorController, type MarkdownPlugin, type MarkdownRichValue } from "@atlas/markdown/core";
import { createAttachmentsPlugin } from "@atlas/markdown/plugins/attachments";
import { createCodeBlockHighlighting, createEditorToolsPlugin } from "@atlas/markdown/plugins/editor-tools";
import { createHeadingAnchorsPlugin } from "@atlas/markdown/plugins/heading-anchors";
import { createYouTrackPlugin } from "@atlas/markdown/plugins/youtrack";
import { createDocumentWidgets } from "./documentWidgets";

/** Every content plugin DocumentEditor registers (contentPlugins), which the history and public views share. */
function contentPlugins(): MarkdownPlugin[] {
  return [createEditorToolsPlugin(), createAttachmentsPlugin(), ...createDocumentWidgets({ kind: "history" }), createHeadingAnchorsPlugin(),
    createYouTrackPlugin({ client: { search: async () => [] } })];
}

const emptyValue = (): MarkdownRichValue => ({ format: "atlas-rich", version: 1, document: { type: "doc", content: [{ type: "paragraph" }] } });

describe("heading levels the server writes", () => {
  // Since 2.7.0 the server keeps ##### and ###### as H5 and H6 (MarkdownToRich, wiki import, MCP); an editor without those levels
  // draws them as H1 (0.15.0). Red until the pin moves to an editor with six levels (0.16.0), so the two cannot ship apart. In
  // editor source mode DocumentEditor.test.ts renders all six levels instead.
  it.skipIf(process.env.ATLAS_EDITOR_MODE === "source")("are heading levels of the pinned editor", async () => {
    let levels: unknown;
    const probe: MarkdownPlugin = { id: "atlas-heading-probe", apiVersion: 1, create: () => ({ onCreate: ({ editor }) => {
      levels = editor.extensionManager.extensions.find(extension => extension.name === "heading")?.options.levels;
    } }) };
    const controller = createMarkdownEditorController({ profile: "rich", source: { kind: "local", value: emptyValue() }, plugins: [...contentPlugins(), probe],
      codeBlock: await createCodeBlockHighlighting() });
    try {
      controller.attachView(document.createElement("div"), { ariaLabel: "Nadpisy" });
    } finally { controller.destroy(); }
    expect(levels).toEqual(expect.arrayContaining([5, 6]));
  });
});
